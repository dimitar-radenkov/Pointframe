using System.Text.Json;

namespace Pointframe.Cli;

internal sealed record ReviewFlag(string File, int? Line, string Severity, string Message);

internal sealed record Review(string Summary, IReadOnlyList<ReviewFlag> Flags, string Reviewer);

internal interface IReviewer
{
    Task<Review> ReviewAsync(string taskText, string diff, string verdictSummary, CancellationToken cancellationToken);
}

// The reviewer reads the task, the diff, and the verdict after a pass, with a fresh context and no MCP tools,
// on whichever agent the person chose, and flags what checks cannot see: weakened tests, edited gates or hooks, work outside the task, and
// criteria met only in form. Its flags go to the person; they never change the verdict.
internal sealed class AgentReviewer(IAgentRunner? runner, decimal maxBudgetUsd = 1m) : IReviewer
{
    internal const int MaxDiffCharacters = 150_000;

    // Strict structured-output form (every object closed, every property required): Codex
    // rejects any other schema, and Claude Code accepts this one.
    internal const string OutputSchema = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "flags": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "file": { "type": "string" },
                  "line": { "type": "integer" },
                  "severity": { "type": "string", "enum": ["high", "medium", "low"] },
                  "message": { "type": "string" }
                },
                "required": ["file", "line", "severity", "message"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "flags"],
          "additionalProperties": false
        }
        """;

    internal const string Instructions = """
        You review a change an AI agent made to a project after its verification passed. You did
        not write it. You see the task, the full diff, and the verdict. You have no tools: read and judge.

        Flag only what the passing verdict cannot show:
        - tests deleted, skipped, or weakened (assertions removed, expected values changed to match the output);
        - a test that is unchanged still covers behaviour; do not flag behaviour as untested without checking the diff for test files;
        - changes to the verification spec (.pointframe/verify.json), its gates, hooks, or CI configuration;
        - work outside the task, or behaviour the task did not ask for;
        - criteria met only in form, such as a check on a label while the data behind it is not saved;
        - anything that would surprise the person who wrote the task.

        Give file and line for each flag, and severity high (the person must look before merging), medium, or
        low. Do not flag style. Do not repeat what the verdict already proves. If nothing needs a flag, return
        an empty list and say so in the summary, in one or two sentences.
        """;

    public async Task<Review> ReviewAsync(string taskText, string diff, string verdictSummary, CancellationToken cancellationToken)
    {
        if (runner is null)
        {
            throw new AgentException("No agent is available to act as the reviewer, so the review was skipped.");
        }

        var truncated = diff.Length > MaxDiffCharacters;
        var input = $"""
            The task:

            {taskText}

            The verdict:

            {verdictSummary}

            The diff{(truncated ? $" (truncated to the first {MaxDiffCharacters} characters)" : string.Empty)}:

            {(truncated ? diff[..MaxDiffCharacters] : diff)}
            """;
        var workDirectory = Path.Combine(Path.GetTempPath(), $"pointframe-reviewer-{Guid.NewGuid():N}");
        var answer = await runner.RunAsync(
            new AgentRequest("reviewer", Instructions, input, OutputSchema, workDirectory, McpServer: null, maxBudgetUsd),
            cancellationToken);
        return ParseReview(answer, runner.Name);
    }

    // The model sometimes ends the summary with leftover tool-call markup ("</parameter>"); cut it off.
    internal static string CleanSummary(string? summary)
    {
        var text = summary ?? string.Empty;
        var markup = text.IndexOf("</", StringComparison.Ordinal);
        return (markup >= 0 ? text[..markup] : text).Trim();
    }

    internal static Review ParseReview(JsonElement review, string reviewer)
    {
        try
        {
            var flags = review.GetProperty("flags").EnumerateArray().Select(flag => new ReviewFlag(
                flag.GetProperty("file").GetString() ?? string.Empty,
                flag.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var number) && number != 0 ? number : null,
                flag.GetProperty("severity").GetString() ?? "low",
                flag.GetProperty("message").GetString() ?? string.Empty)).ToArray();
            return new Review(CleanSummary(review.GetProperty("summary").GetString()), flags, reviewer);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new AgentException($"The reviewer's answer is not the expected JSON: {exception.Message}");
        }
    }
}
