using System.Text.Json;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Cli;

internal sealed record CaptureRectangle(int X, int Y, int Width, int Height);

// Runs one scenario as one desktop test session through the MCP server's own desktop_* tools, so a CLI
// run and an agent's hand-driven session produce the same signed report. The first failed step stops the
// scenario and the rest are reported as skipped; the report is always fetched and the session always
// ended, and a session that cannot be ended can never pass.
internal sealed class DesktopScenarioRunner
{
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(90);

    private readonly IMcpToolClient _client;
    private readonly string _profileId;
    private readonly IReadOnlyList<CaptureRectangle> _captureBounds;
    private readonly TimeSpan _elementTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _restartWait;

    internal DesktopScenarioRunner(
        IMcpToolClient client,
        string profileId,
        IReadOnlyList<CaptureRectangle> captureBounds,
        TimeSpan? elementTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? restartWait = null)
    {
        _client = client;
        _profileId = profileId;
        _captureBounds = captureBounds;
        _elementTimeout = elementTimeout ?? TimeSpan.FromSeconds(15);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _restartWait = restartWait ?? TimeSpan.FromSeconds(10);
    }

    internal async Task<IReadOnlyList<string>> ObserveAutomationIdsAsync(string cancellationActionId, CancellationToken cancellationToken)
    {
        var start = Structured(await _client.CallToolAsync(
            "desktop_start_test_session",
            new { actionId = NewActionId(), profileId = _profileId, criteria = (string[]?)null },
            LaunchTimeout,
            cancellationToken).ConfigureAwait(false));
        var sessionId = start.TryGetProperty("sessionRef", out var sessionRef) ? sessionRef.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var (code, message) = ErrorOf(start);
            throw new InvalidOperationException($"The app session did not start: {code ?? "SessionNotStarted"}: {message ?? "No session reference was returned."}");
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow + _elementTimeout;
            while (true)
            {
                var observation = Structured(await _client.CallToolAsync(
                    "desktop_observe_app",
                    new
                    {
                        sessionId,
                        captureBoundsPixels = _captureBounds.Select(bounds => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height }).ToArray(),
                        includeUiAutomation = true,
                        includeImages = false,
                    },
                    ActionTimeout,
                    cancellationToken).ConfigureAwait(false));
                var (code, message) = ErrorOf(observation);
                if (code is not null)
                {
                    throw new InvalidOperationException($"The app could not be observed: {code}: {message}");
                }

                var ids = observation.GetProperty("elements").EnumerateArray()
                    .Select(item => item.TryGetProperty("automationId", out var id) ? id.GetString() : null)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (ids.Length > 0)
                {
                    return ids;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InvalidOperationException($"The app exposed no automation ids within {_elementTimeout.TotalMilliseconds:0} ms.");
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await _client.CallToolAsync(
                "desktop_end_test_session",
                new { actionId = cancellationActionId, sessionId },
                ActionTimeout,
                cancellationToken).ConfigureAwait(false);
        }
    }

    // continueAfterFailedChecks is the fail-before mode of `verify task start`: on unchanged code every
    // criterion is expected to fail, and the negative controls after those failures must still run. A
    // failed action (launch, enterText, invoke, ...) still stops the scenario.
    internal async Task<VerificationScenarioResult> RunAsync(
        VerificationScenario scenario,
        CancellationToken cancellationToken,
        bool continueAfterFailedChecks = false)
    {
        var problems = new List<string>();
        var start = Structured(await _client.CallToolAsync(
            "desktop_start_test_session",
            new { actionId = NewActionId(), profileId = _profileId, criteria = scenario.Criteria.Count == 0 ? null : scenario.Criteria },
            LaunchTimeout,
            cancellationToken).ConfigureAwait(false));
        var sessionId = start.TryGetProperty("sessionRef", out var sessionRef) ? sessionRef.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var (code, message) = ErrorOf(start);
            var launch = new VerificationStepResult(-1, "launch", VerificationStatus.Fail, $"launch app profile '{_profileId}'", code ?? "SessionNotStarted", message);
            return new VerificationScenarioResult(
                scenario.Id, VerificationStatus.Fail, null, [], [launch, .. Skipped(scenario, 0)], null, false, null, ["The app session did not start."]);
        }

        var steps = new List<VerificationStepResult>();
        var stopped = false;
        for (var index = 0; index < scenario.Steps.Count; index++)
        {
            var step = scenario.Steps[index];
            if (stopped)
            {
                steps.Add(new VerificationStepResult(index, step.Kind, VerificationStatus.Skipped, Describe(step)));
                continue;
            }

            VerificationStepResult result;
            try
            {
                result = await RunStepAsync(sessionId, index, step, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException or JsonException or KeyNotFoundException)
            {
                result = new VerificationStepResult(index, step.Kind, VerificationStatus.Fail, Describe(step), "StepError", exception.Message);
            }

            steps.Add(result);
            stopped = result.Status != VerificationStatus.Pass
                && !(continueAfterFailedChecks && step is VerificationStep.Check && result.Code is "CheckFailed");
        }

        string? reportVerdict = null;
        var criteria = new List<VerificationCriterionResult>();
        string? bundleDirectory = null;
        var proofValid = false;
        string? proofKeyId = null;
        try
        {
            var report = Structured(await _client.CallToolAsync(
                "desktop_get_test_report", new { sessionId }, ActionTimeout, cancellationToken).ConfigureAwait(false));
            reportVerdict = report.TryGetProperty("verdict", out var verdict) ? verdict.GetString() : null;
            if (report.TryGetProperty("criteria", out var criteriaElement))
            {
                criteria.AddRange(criteriaElement.EnumerateArray().Select(criterion => new VerificationCriterionResult(
                    criterion.GetProperty("id").GetString()!,
                    criterion.GetProperty("text").GetString()!,
                    criterion.GetProperty("verdict").GetString()!)));
            }

            bundleDirectory = report.TryGetProperty("sessionDirectory", out var directory) ? directory.GetString() : null;
            (proofValid, proofKeyId) = VerifyBundle(bundleDirectory, report, problems);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            problems.Add($"The signed report could not be read: {exception.Message}");
        }

        try
        {
            var end = Structured(await _client.CallToolAsync(
                "desktop_end_test_session", new { sessionId, actionId = NewActionId() }, ActionTimeout, cancellationToken).ConfigureAwait(false));
            var (code, message) = ErrorOf(end);
            if (code is not null)
            {
                problems.Add($"Cleanup failed ({code}): {message}");
            }
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException or JsonException)
        {
            problems.Add($"Cleanup failed: {exception.Message}");
        }

        if (reportVerdict is not null && reportVerdict != "passed")
        {
            problems.Add($"The signed report's verdict is '{reportVerdict}'.");
        }

        var passed = steps.All(item => item.Status == VerificationStatus.Pass)
            && reportVerdict == "passed"
            && proofValid
            && problems.Count == 0;
        return new VerificationScenarioResult(
            scenario.Id,
            passed ? VerificationStatus.Pass : VerificationStatus.Fail,
            reportVerdict,
            criteria,
            steps,
            bundleDirectory,
            proofValid,
            proofKeyId,
            problems);
    }

    internal static string Describe(VerificationStep step) => step switch
    {
        VerificationStep.EnterText enterText => $"enter text into {enterText.Locator}",
        VerificationStep.Invoke invoke => $"invoke {invoke.Locator}",
        VerificationStep.PressKeys pressKeys => $"press keys {string.Join("+", pressKeys.VirtualKeys.Select(key => $"0x{key:X2}"))}",
        VerificationStep.Restart => "restart the app",
        VerificationStep.Check check => check.ExpectFailure
            ? $"negative control: {check.ConditionKind} on {check.Locator} must not hold"
            : $"check {check.ConditionKind} on {check.Locator}{(check.Criterion is null ? string.Empty : $" for {check.Criterion}")}",
        _ => step.Kind,
    };

    private async Task<VerificationStepResult> RunStepAsync(string sessionId, int index, VerificationStep step, CancellationToken cancellationToken)
    {
        var description = Describe(step);
        switch (step)
        {
            case VerificationStep.EnterText enterText:
                {
                    var element = await ResolveAsync(sessionId, enterText.Locator, cancellationToken).ConfigureAwait(false);
                    if (element.Error is not null)
                    {
                        return new VerificationStepResult(index, step.Kind, VerificationStatus.Fail, description, element.Error.Value.Code, element.Error.Value.Message);
                    }

                    return ActionResult(index, step, description, await _client.CallToolAsync(
                        "desktop_enter_text",
                        new
                        {
                            sessionId,
                            actionId = NewActionId(),
                            observationRef = element.ObservationRef,
                            imageRef = element.ImageRef,
                            x = 0,
                            y = 0,
                            text = enterText.Text,
                            semanticValue = true,
                            elementRef = element.ElementRef,
                        },
                        ActionTimeout,
                        cancellationToken).ConfigureAwait(false));
                }

            case VerificationStep.Invoke invoke:
                {
                    var element = await ResolveAsync(sessionId, invoke.Locator, cancellationToken).ConfigureAwait(false);
                    if (element.Error is not null)
                    {
                        return new VerificationStepResult(index, step.Kind, VerificationStatus.Fail, description, element.Error.Value.Code, element.Error.Value.Message);
                    }

                    return ActionResult(index, step, description, await _client.CallToolAsync(
                        "desktop_invoke",
                        new { sessionId, actionId = NewActionId(), elementRef = element.ElementRef },
                        ActionTimeout,
                        cancellationToken).ConfigureAwait(false));
                }

            case VerificationStep.PressKeys pressKeys:
                {
                    var element = await ResolveAsync(sessionId, locator: null, cancellationToken).ConfigureAwait(false);
                    if (element.Error is not null)
                    {
                        return new VerificationStepResult(index, step.Kind, VerificationStatus.Fail, description, element.Error.Value.Code, element.Error.Value.Message);
                    }

                    var focus = ActionResult(index, step, description, await _client.CallToolAsync(
                        "desktop_focus_window",
                        new { sessionId, actionId = NewActionId(), windowRef = element.WindowRef },
                        ActionTimeout,
                        cancellationToken).ConfigureAwait(false));
                    if (focus.Status != VerificationStatus.Pass)
                    {
                        return focus;
                    }

                    return ActionResult(index, step, description, await _client.CallToolAsync(
                        "desktop_press_keys",
                        new { sessionId, actionId = NewActionId(), virtualKeys = pressKeys.VirtualKeys },
                        ActionTimeout,
                        cancellationToken).ConfigureAwait(false));
                }

            case VerificationStep.Restart:
                {
                    // Invoking the app's own Close returns before its process has exited, and the server refuses a
                    // restart while the target still runs. Wait for the exit instead of failing on the race.
                    var deadline = DateTimeOffset.UtcNow + _restartWait;
                    while (true)
                    {
                        var restart = ActionResult(index, step, description, await _client.CallToolAsync(
                            "desktop_restart_app",
                            new { sessionId, actionId = NewActionId() },
                            LaunchTimeout,
                            cancellationToken).ConfigureAwait(false));
                        if (restart.Code != "TargetStillRunning" || DateTimeOffset.UtcNow >= deadline)
                        {
                            return restart;
                        }

                        await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                    }
                }

            case VerificationStep.Check check:
                {
                    // A check right after a launch or restart waits for its element through the server's own
                    // polling, so a window that is still opening is not a failure.
                    var response = Structured(await _client.CallToolAsync(
                        "desktop_check_ui",
                        new
                        {
                            sessionId,
                            kind = check.ConditionKind,
                            automationId = check.Locator.AutomationId,
                            role = check.Locator.Role,
                            name = check.Locator.Name,
                            expected = check.Expected,
                            timeoutSeconds = check.TimeoutSeconds,
                            criterionId = check.Criterion,
                            expectFailure = check.ExpectFailure,
                        },
                        TimeSpan.FromSeconds(check.TimeoutSeconds) + ActionTimeout,
                        cancellationToken).ConfigureAwait(false));
                    var verification = response.TryGetProperty("verification", out var value) ? value.GetString() : null;
                    var actual = response.TryGetProperty("actualValue", out var actualValue) && actualValue.ValueKind == JsonValueKind.String
                        ? actualValue.GetString()
                        : null;
                    var (code, message) = ErrorOf(response);
                    return new VerificationStepResult(
                        index,
                        step.Kind,
                        verification == "passed" ? VerificationStatus.Pass : VerificationStatus.Fail,
                        description,
                        verification == "passed" ? null : code ?? $"Check{Capitalize(verification ?? "Unanswered")}",
                        message,
                        check.Expected,
                        actual);
                }

            default:
                throw new InvalidOperationException($"Unsupported step '{step.Kind}'.");
        }
    }

    private async Task<ResolvedElement> ResolveAsync(string sessionId, ElementLocator? locator, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _elementTimeout;
        IReadOnlyList<string> seen = [];
        while (true)
        {
            var observation = Structured(await _client.CallToolAsync(
                "desktop_observe_app",
                new
                {
                    sessionId,
                    captureBoundsPixels = _captureBounds.Select(bounds => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height }).ToArray(),
                    includeUiAutomation = true,
                    includeImages = false,
                },
                ActionTimeout,
                cancellationToken).ConfigureAwait(false));
            var (code, message) = ErrorOf(observation);
            if (code == "SessionNotFound")
            {
                return ResolvedElement.Failed(code, message ?? "The desktop test session was not found.");
            }

            if (code is null && observation.TryGetProperty("elements", out var elements))
            {
                var all = elements.EnumerateArray().ToArray();
                var match = locator is null ? all.FirstOrDefault() : all.FirstOrDefault(element => Matches(element, locator));
                if (match.ValueKind == JsonValueKind.Object)
                {
                    return new ResolvedElement(
                        observation.GetProperty("observationRef").GetString()!,
                        observation.GetProperty("images")[0].GetProperty("imageRef").GetString()!,
                        match.GetProperty("elementRef").GetString()!,
                        match.GetProperty("windowRef").GetString()!,
                        null);
                }

                seen = all
                    .Select(element => element.TryGetProperty("automationId", out var id) ? id.GetString() : null)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Take(20)
                    .ToArray();
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var target = locator?.ToString() ?? "any element of the app";
                var hint = seen.Count == 0 ? "The app exposed no automation ids." : $"Automation ids seen: {string.Join(", ", seen)}.";
                return ResolvedElement.Failed("ElementNotFound", $"No element matched {target} within {_elementTimeout.TotalSeconds:0} seconds. {hint}");
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Matches(JsonElement element, ElementLocator locator)
    {
        if (locator.AutomationId is not null)
        {
            return element.TryGetProperty("automationId", out var automationId)
                && string.Equals(automationId.GetString(), locator.AutomationId, StringComparison.Ordinal);
        }

        return element.TryGetProperty("role", out var role)
            && string.Equals(role.GetString(), locator.Role, StringComparison.OrdinalIgnoreCase)
            && element.TryGetProperty("name", out var name)
            && string.Equals(name.GetString(), locator.Name, StringComparison.Ordinal);
    }

    private static VerificationStepResult ActionResult(int index, VerificationStep step, string description, JsonElement result)
    {
        var response = Structured(result);
        var (code, message) = ErrorOf(response);
        var dispatch = response.TryGetProperty("dispatch", out var value) ? value.GetString() : null;
        return code is null && dispatch == "Complete"
            ? new VerificationStepResult(index, step.Kind, VerificationStatus.Pass, description)
            : new VerificationStepResult(index, step.Kind, VerificationStatus.Fail, description, code ?? $"Dispatch{dispatch ?? "Unknown"}", message);
    }

    private static (bool Valid, string? KeyId) VerifyBundle(string? bundleDirectory, JsonElement report, List<string> problems)
    {
        if (bundleDirectory is null)
        {
            problems.Add("The report names no proof bundle directory.");
            return (false, null);
        }

        var reportPath = Path.Combine(bundleDirectory, DesktopProofBundle.ReportFileName);
        if (!File.Exists(reportPath))
        {
            problems.Add($"The proof bundle has no {DesktopProofBundle.ReportFileName} at {bundleDirectory}.");
            return (false, null);
        }

        var evidenceDirectory = report.TryGetProperty("evidenceDirectory", out var evidence) ? evidence.GetString() : null;
        var verification = DesktopProofService.Verify(
            File.ReadAllText(reportPath),
            evidenceDirectory is not null && Directory.Exists(evidenceDirectory) ? evidenceDirectory : null);
        problems.AddRange(verification.Problems.Select(problem => $"Proof: {problem}"));
        return (verification.IsValid, verification.KeyId);
    }

    private static JsonElement Structured(JsonElement result) =>
        result.TryGetProperty("structuredContent", out var structured) ? structured : result;

    private static (string? Code, string? Message) ErrorOf(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (
            error.TryGetProperty("code", out var code) ? code.GetString() : "Error",
            error.TryGetProperty("message", out var message) ? message.GetString() : null);
    }

    private static IEnumerable<VerificationStepResult> Skipped(VerificationScenario scenario, int from) =>
        scenario.Steps.Skip(from).Select((step, offset) =>
            new VerificationStepResult(from + offset, step.Kind, VerificationStatus.Skipped, Describe(step)));

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string NewActionId() => Guid.NewGuid().ToString();

    private readonly record struct ResolvedElement(
        string? ObservationRef,
        string? ImageRef,
        string? ElementRef,
        string? WindowRef,
        (string Code, string Message)? Error)
    {
        internal static ResolvedElement Failed(string code, string message) => new(null, null, null, null, (code, message));
    }
}
