using System.Text.Json;

namespace Pointframe.Cli;

internal sealed record ExaminerRequest(
    string TaskText,
    string ProfileId,
    string McpExecutablePath,
    string PolicyPath,
    string WorkDirectory,
    IReadOnlyDictionary<string, string> Environment);

internal sealed record ExaminerProposal(
    IReadOnlyList<string> Criteria,
    JsonElement Steps,
    IReadOnlyList<string> RequiredAutomationIds,
    string? Notes);

internal interface IExaminer
{
    string Name { get; }

    Task<ExaminerProposal> ProposeAsync(ExaminerRequest request, CancellationToken cancellationToken);
}

// The examiner sees only the task text and the running app. Whichever agent plays it (see AgentSelection)
// runs in an empty folder outside the repository with only the Pointframe MCP server, so it cannot read
// the code and the worker's hooks never run inside it. Its proposal is checked by the CLI, never trusted:
// see FailBefore.
internal sealed class AgentExaminer(IAgentRunner? runner, decimal maxBudgetUsd = 3m) : IExaminer
{
    internal const string ServerName = "pointframe";

    // Strict structured-output form, which Codex requires and Claude Code accepts: every object closed and
    // every property required. Steps are different kinds of objects, which a strict schema cannot describe, so
    // each step comes back as JSON text and ParseProposal turns it into an object; the spec loader then checks
    // it like any other step.
    internal const string OutputSchema = """
        {
          "type": "object",
          "properties": {
            "criteria": { "type": "array", "items": { "type": "string" } },
            "steps": { "type": "array", "items": { "type": "string" } },
            "requiredAutomationIds": { "type": "array", "items": { "type": "string" } },
            "notes": { "type": "string" }
          },
          "required": ["criteria", "steps", "requiredAutomationIds", "notes"],
          "additionalProperties": false
        }
        """;

    public string Name => runner?.Name ?? "none";

    internal static string Instructions(string profileId) => $$$"""
        You are the examiner for a change to a Windows desktop app. Another agent will implement the change.
        You write the acceptance test it is graded by, before any code exists. You never see the code.

        Your only tools are the Pointframe MCP server's desktop_* tools. The app's policy profile id is '{{{profileId}}}'.
        Start a session with desktop_start_test_session (profileId '{{{profileId}}}', no criteria), explore with
        desktop_observe_app (includeUiAutomation true, includeImages false; call list_displays first for the
        capture rectangles), and end every session with desktop_end_test_session. Never call desktop_check_ui:
        every check is recorded, and you are only exploring.

        1. Restate the task as observable behaviour: what a user does, and what they then see in the app.
        2. Write 1 to 5 criteria. Each is one observable outcome, testable through the UI, free of
           implementation detail. Cover the main path and the one edge case the task's wording implies
           (for example "after a restart" or "when empty"). Write each criterion as plain text with no id
           prefix: they are numbered C1, C2, ... by their order.
        3. Use the automation ids you observed. Where the task needs an element that does not exist yet, name
           the automation id it must have (camelCase, like the existing ones) and list it in
           requiredAutomationIds, so the implementer knows the contract.
        4. Write the steps. Each item of "steps" is the JSON text of one step, an object with exactly one
           property:
           {"enterText": {"automationId": "...", "text": "..."}}
           {"invoke": {"automationId": "..."}}          (buttons, menu items; use the app's own Close or Exit to close it)
           {"pressKeys": {"keys": [17, 83]}}            (Windows virtual-key codes, modifiers first)
           {"restart": {}}                              (relaunch; close the app with an invoke first)
           {"check": {"kind": "textEquals", "automationId": "...", "expected": "...", "criterion": "C1"}}
           Instead of automationId an element may be {"role": "Button", "name": "Save"}. Check kinds: exists,
           absent, enabled (expected true/false), toggleEquals (expected on/off), selectionEquals (expected
           selected/notSelected), textEquals. Optional "timeoutSeconds" from 1 to 30.
           Criteria are numbered C1, C2, ... in your order. Every criterion needs a check naming it. Add at
           least one negative control: a check with "expectFailure": true and no "criterion", whose expected
           value is deliberately wrong both before and after the change (for example textEquals with text the
           field will never hold). It proves the check can tell states apart. Never use a real requirement of
           the task as a negative control.
        5. Your criteria will be run on the current, unchanged app. Each criterion check must fail there,
           because the behaviour does not exist yet; a criterion that already holds does not test the change
           and makes your proposal rejected. If part of the task already holds in the current app, leave it
           out of the criteria and say so in notes. Negative controls must pass there.
        6. Do not write criteria to be easy or hard to pass. Do not guess the implementation.
        7. In notes, say in two or three sentences what each criterion checks and what is out of scope.

        Return only the structured output: criteria, steps, requiredAutomationIds, notes.
        """;

    public async Task<ExaminerProposal> ProposeAsync(ExaminerRequest request, CancellationToken cancellationToken)
    {
        if (runner is null)
        {
            throw new AgentException("No agent is available to act as the examiner. Install Claude Code or Codex, or configure one with 'Pointframe.Cli.exe verify agent'.");
        }

        var answer = await runner.RunAsync(
            new AgentRequest(
                "examiner",
                Instructions(request.ProfileId),
                $"The task:{Environment.NewLine}{Environment.NewLine}{request.TaskText}",
                OutputSchema,
                request.WorkDirectory,
                new AgentMcpServer(ServerName, request.McpExecutablePath, ["--desktop-testing", "--desktop-policy", request.PolicyPath], request.Environment),
                maxBudgetUsd),
            cancellationToken);
        return ParseProposal(answer);
    }

    internal static ExaminerProposal ParseProposal(JsonElement proposal)
    {
        try
        {
            // A step is JSON text (the strict schema) or, from older answers, already an object.
            var steps = proposal.GetProperty("steps").EnumerateArray()
                .Select(step => step.ValueKind == JsonValueKind.String
                    ? AgentProcess.ParseJson(step.GetString()!, "An examiner step")
                    : step.Clone())
                .ToArray();
            return new ExaminerProposal(
                proposal.GetProperty("criteria").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray(),
                JsonSerializer.SerializeToElement(steps),
                proposal.TryGetProperty("requiredAutomationIds", out var ids) ? ids.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [],
                proposal.TryGetProperty("notes", out var notes) ? notes.GetString() : null);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new AgentException($"The examiner's answer is not the expected JSON: {exception.Message}");
        }
    }
}

internal static class FailBefore
{
    internal const string Confirmed = "confirmed";
    internal const string Rejected = "rejected";

    // The examiner's scenario on the unchanged app: every criterion must fail or be unreachable (an element
    // the task adds does not exist yet), no criterion may pass, every negative control that ran must pass,
    // and the app must have launched. A criterion that already holds does not test the change.
    internal static TaskFailBefore Evaluate(VerificationScenario scenario, VerificationScenarioResult result)
    {
        var problems = new List<string>();
        if (result.Steps.Count == 0 || result.Steps.Any(step => step.Kind == "launch" && step.Status == VerificationStatus.Fail))
        {
            problems.Add($"The app did not launch or the run failed ({string.Join(" ", result.Problems)}), so nothing was shown to fail before the change.");
        }

        // Only two failures say something about the app: a check that read a different state, and an element
        // the task is about to add. A timeout, an inconclusive check, or a failed action proves nothing.
        foreach (var step in result.Steps.Where(step => step.Index >= 0 && step.Status == VerificationStatus.Fail))
        {
            var meaningful = scenario.Steps[step.Index] is VerificationStep.Check
                ? step.Code == "CheckFailed"
                : step.Code == "ElementNotFound";
            if (!meaningful)
            {
                problems.Add($"Step {step.Index} ({step.Description}) failed with {step.Code} on the unchanged app, which does not show the behaviour is missing.");
            }
        }

        var criteria = new List<TaskCriterionBefore>();
        for (var number = 1; number <= scenario.Criteria.Count; number++)
        {
            var id = $"C{number}";
            var checks = result.Steps
                .Where(step => step.Index >= 0 && scenario.Steps[step.Index] is VerificationStep.Check { Criterion: var criterion } && criterion == id)
                .ToArray();
            var before = checks.Any(step => step.Status == VerificationStatus.Pass) ? "passed"
                : checks.Any(step => step.Status == VerificationStatus.Fail) ? "failed"
                : "notReached";
            if (before == "passed")
            {
                problems.Add($"{id} already holds on the unchanged app, so it does not test the change: \"{scenario.Criteria[number - 1]}\".");
            }

            criteria.Add(new TaskCriterionBefore(id, scenario.Criteria[number - 1], before));
        }

        foreach (var step in result.Steps.Where(step => step.Index >= 0 && scenario.Steps[step.Index] is VerificationStep.Check { ExpectFailure: true }))
        {
            if (step.Status == VerificationStatus.Fail)
            {
                problems.Add($"The negative control at step {step.Index} failed on the unchanged app ({step.Code}), so the check cannot tell states apart.");
            }
        }

        return new TaskFailBefore(problems.Count == 0 ? Confirmed : Rejected, criteria, problems, result.BundleDirectory);
    }
}
