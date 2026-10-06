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
    private const int MaxCandidates = 5;
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

    internal async Task<(IReadOnlyList<string> AutomationIds, string? Role, string? Name)> ObserveTargetsAsync(string cancellationActionId, CancellationToken cancellationToken)
    {
        var startResult = await _client.CallToolAsync(
            "desktop_start_test_session",
            new { actionId = NewActionId(), profileId = _profileId, criteria = (string[]?)null },
            LaunchTimeout,
            cancellationToken).ConfigureAwait(false);
        var start = Structured(startResult);
        var sessionId = start.TryGetProperty("sessionRef", out var sessionRef) ? sessionRef.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var (code, message) = ErrorOf(startResult);
            throw new InvalidOperationException($"The app session did not start: {code ?? "SessionNotStarted"}: {message ?? ToolText(startResult) ?? "No session reference was returned."}");
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow + _elementTimeout;
            while (true)
            {
                var observationResult = await ObserveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var observation = Structured(observationResult);
                var (code, message) = ErrorOf(observationResult);
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
                    return (ids, null, null);
                }

                var namedElement = observation.GetProperty("elements").EnumerateArray()
                    .FirstOrDefault(item => item.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()));
                if (namedElement.ValueKind == JsonValueKind.Object)
                {
                    var role = namedElement.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
                    var name = namedElement.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(name))
                    {
                        return (ids, role, name);
                    }
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

    internal async Task<IReadOnlyList<string>> ObserveAutomationIdsAsync(string cancellationActionId, CancellationToken cancellationToken)
    {
        var targets = await ObserveTargetsAsync(cancellationActionId, cancellationToken).ConfigureAwait(false);
        return targets.AutomationIds;
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
        var startResult = await _client.CallToolAsync(
            "desktop_start_test_session",
            new { actionId = NewActionId(), profileId = _profileId, criteria = scenario.Criteria.Count == 0 ? null : scenario.Criteria },
            LaunchTimeout,
            cancellationToken).ConfigureAwait(false);
        var start = Structured(startResult);
        var sessionId = start.TryGetProperty("sessionRef", out var sessionRef) ? sessionRef.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var (code, startMessage) = ErrorOf(startResult);
            var message = startMessage ?? ToolText(startResult);
            var launch = new VerificationStepResult(-1, "launch", VerificationStatus.Fail, $"launch app profile '{_profileId}'", code ?? "SessionNotStarted", message);
            return new VerificationScenarioResult(
                scenario.Id, VerificationStatus.Fail, null, [], [launch, .. Skipped(scenario, 0)], null, false, null, ["The app session did not start."]);
        }

        var steps = new List<VerificationStepResult>();
        var stopped = false;
        var notReady = await WaitForReadyAsync(sessionId, "launch", cancellationToken).ConfigureAwait(false);
        if (notReady is not null)
        {
            steps.Add(new VerificationStepResult(-1, "launch", VerificationStatus.Fail, $"launch app profile '{_profileId}'", notReady.Value.Code, notReady.Value.Message));
            steps.AddRange(Skipped(scenario, 0));
        }

        for (var index = 0; index < scenario.Steps.Count && notReady is null; index++)
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
            var end = await _client.CallToolAsync(
                "desktop_end_test_session", new { sessionId, actionId = NewActionId() }, ActionTimeout, cancellationToken).ConfigureAwait(false);
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
                        return new VerificationStepResult(
                            index,
                            step.Kind,
                            VerificationStatus.Fail,
                            description,
                            "FocusRequired",
                            $"Keyboard steps need the app in the foreground. {focus.Message ?? "Window focus was refused."} Use invoke steps with a role and name locator instead.");
                    }

                    return ActionResult(index, step, description, await _client.CallToolAsync(
                        "desktop_press_keys",
                        new { sessionId, actionId = NewActionId(), virtualKeys = pressKeys.VirtualKeys, windowRef = element.WindowRef },
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
                        if (restart.Code == "TargetStillRunning" && DateTimeOffset.UtcNow < deadline)
                        {
                            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (restart.Status != VerificationStatus.Pass)
                        {
                            return restart;
                        }

                        var notReady = await WaitForReadyAsync(sessionId, "restart", cancellationToken).ConfigureAwait(false);
                        return notReady is null
                            ? restart
                            : new VerificationStepResult(index, step.Kind, VerificationStatus.Fail, description, notReady.Value.Code, notReady.Value.Message);
                    }
                }

            case VerificationStep.Check check:
                {
                    // A check right after a launch or restart waits for its element through the server's own
                    // polling, so a window that is still opening is not a failure.
                    var checkResult = await _client.CallToolAsync(
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
                        cancellationToken).ConfigureAwait(false);
                    var response = Structured(checkResult);
                    var verification = response.TryGetProperty("verification", out var value) ? value.GetString() : null;
                    var actual = response.TryGetProperty("actualValue", out var actualValue) && actualValue.ValueKind == JsonValueKind.String
                        ? actualValue.GetString()
                        : null;
                    var (code, message) = ErrorOf(checkResult);
                    if (code == "ElementNotFound")
                    {
                        var observed = await ObserveAsync(sessionId, cancellationToken).ConfigureAwait(false);
                        var elements = Structured(observed).TryGetProperty("elements", out var observedElements)
                            ? observedElements.EnumerateArray().ToArray()
                            : [];
                        message = AppendMenuHint($"{message}{NameHint(check.Locator, elements)}", check.Locator, elements);
                    }

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
        IReadOnlyList<JsonElement> observed = [];
        while (true)
        {
            var observationResult = await ObserveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var observation = Structured(observationResult);
            var (code, message) = ErrorOf(observationResult);
            if (code == "SessionNotFound")
            {
                return ResolvedElement.Failed(code, message ?? "The desktop test session was not found.");
            }

            if (code is null && observation.TryGetProperty("elements", out var elements))
            {
                var all = elements.EnumerateArray().ToArray();
                var matches = locator is null ? all.Take(1).ToArray() : all.Where(element => Matches(element, locator)).ToArray();
                if (matches.Length > 1)
                {
                    var candidates = matches.Take(MaxCandidates).Select(Candidate);
                    return ResolvedElement.Failed(
                        "AmbiguousLocator",
                        $"{matches.Length} elements matched {locator}, and an input needs exactly one. No input was sent. Candidates: {string.Join("; ", candidates)}. Narrow the locator (a different role or name, or an automationId).");
                }

                if (matches.Length == 1)
                {
                    var match = matches[0];
                    return new ResolvedElement(
                        observation.GetProperty("observationRef").GetString()!,
                        observation.GetProperty("images")[0].GetProperty("imageRef").GetString()!,
                        match.GetProperty("elementRef").GetString()!,
                        match.GetProperty("windowRef").GetString()!,
                        null);
                }

                observed = all;
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
                return ResolvedElement.Failed(
                    "ElementNotFound",
                    AppendMenuHint($"No element matched {target} within {_elementTimeout.TotalSeconds:0} seconds. {hint}{NameHint(locator, observed)}", locator, observed));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ObserveAsync(string sessionId, CancellationToken cancellationToken) =>
        await _client.CallToolAsync(
            "desktop_observe_app",
            new
            {
                sessionId,
                captureBoundsPixels = _captureBounds.Select(bounds => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height }).ToArray(),
                includeUiAutomation = true,
                includeImages = false,
            },
            ActionTimeout,
            cancellationToken).ConfigureAwait(false);

    // A launch or restart returns while the app's window is still opening, so the first observation can see
    // no window at all. Wait for a visible window with UI elements before the first step runs.
    private async Task<(string Code, string Message)?> WaitForReadyAsync(string sessionId, string phase, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _elementTimeout;
        while (true)
        {
            var result = await ObserveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var (code, message) = ErrorOf(result);
            if (code == "SessionNotFound")
            {
                return (code, message ?? "The desktop test session was not found.");
            }

            if (code is null
                && Structured(result).TryGetProperty("elements", out var elements)
                && elements.ValueKind == JsonValueKind.Array
                && elements.GetArrayLength() > 0)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var reason = code is null ? string.Empty : $" The last observation returned {code}{(message is null ? string.Empty : $": {message}")}.";
                return ("NoVisibleWindow", $"The app showed no visible window with UI elements within {_elementTimeout.TotalSeconds:0} seconds after the {phase}.{reason}");
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Candidate(JsonElement element)
    {
        static string Text(JsonElement item, string property) =>
            item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        var bounds = element.TryGetProperty("boundsPixels", out var value) ? value.GetRawText() : "unknown";
        return $"role '{Text(element, "role")}', name '{Text(element, "name")}', automationId '{Text(element, "automationId")}', bounds {bounds}";
    }

    private static string NameHint(ElementLocator? locator, IReadOnlyList<JsonElement> observed)
    {
        if (locator?.AutomationId is not { Length: > 0 } automationId)
        {
            return string.Empty;
        }

        var wanted = StripPrefix(automationId);
        foreach (var element in observed)
        {
            var name = element.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (string.IsNullOrEmpty(name) || !string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var role = element.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
            return $" Hint: WinForms menu items and many controls expose no AutomationId; use role + name, e.g. {{ \"role\": \"{role}\", \"name\": \"{name}\" }}.";
        }

        return string.Empty;
    }

    private static string AppendMenuHint(string? message, ElementLocator? locator, IReadOnlyList<JsonElement> observed)
    {
        var isMenuItem = string.Equals(locator?.Role, "menu item", StringComparison.OrdinalIgnoreCase);
        var menuBarIsPresent = observed.Any(element =>
            element.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            && role.GetString() is { } value
            && (string.Equals(value, "menu bar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "menubar", StringComparison.OrdinalIgnoreCase)));
        if (!isMenuItem && !menuBarIsPresent)
        {
            return message ?? string.Empty;
        }

        return $"{message} Menu items appear in UI Automation only after their parent menu is open: invoke the parent first, e.g. {{ \"invoke\": {{ \"role\": \"menu item\", \"name\": \"Options\" }} }}, then the item.";
    }

    private static string StripPrefix(string automationId)
    {
        foreach (var prefix in new[] { "Menu_", "menu", "btn", "BT_", "bt", "txt", "lbl" })
        {
            if (automationId.Length > prefix.Length && automationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return automationId[prefix.Length..].TrimStart('_');
            }
        }

        return automationId;
    }

    private static string? ToolText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString()?.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    return value.Length > 500 ? value[..500] : value;
                }
            }
        }

        return null;
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
        var (code, message) = ErrorOf(result);
        var dispatch = response.TryGetProperty("dispatch", out var value) ? value.GetString() : null;
        if (step is VerificationStep.Invoke && dispatch == "Complete" && code == "InvokePending")
        {
            return new VerificationStepResult(
                index,
                step.Kind,
                VerificationStatus.Pass,
                description,
                code,
                "Invoke was delivered; the app has not returned yet.");
        }

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

    // Takes the raw tool result: a failure that carries only isError and a text block still keeps the server's words.
    private static (string? Code, string? Message) ErrorOf(JsonElement result)
    {
        var response = Structured(result);
        if (!response.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True
                ? ("ToolError", ToolText(result) ?? "The tool reported an error without a message.")
                : (null, null);
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
