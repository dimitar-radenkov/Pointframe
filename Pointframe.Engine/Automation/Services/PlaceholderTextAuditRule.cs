using System.Text.RegularExpressions;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public sealed class PlaceholderTextAuditRule : IDesktopUiAuditRule
{
    public const string RuleId = "placeholder-text";

    private const int MaxEvidenceLength = 200;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Editable and document surfaces hold user content, which may legitimately contain any of these tokens.
    private static readonly string[] EditableRoles = ["Edit", "Document"];

    // The one place the token list lives. A null display means the matched text itself is the token ({0} .. {9}).
    private static readonly PlaceholderToken[] Tokens =
    [
        new("TODO", new Regex(@"(?<![\p{L}\p{N}_])TODO(?![\p{L}\p{N}_])", Options)),
        new("FIXME", new Regex(@"(?<![\p{L}\p{N}_])FIXME(?![\p{L}\p{N}_])", Options)),
        new("lorem ipsum", new Regex("lorem ipsum", Options)),
        new(null, new Regex(@"\{[0-9]\}", Options)),
        new("null", new Regex(@"\Anull\z", Options)),
        new("undefined", new Regex(@"\Aundefined\z", Options)),
        new("NaN", new Regex(@"\ANaN\z", Options)),
    ];

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    public string Id => RuleId;

    public int Version => 1;

    public DesktopUiAuditRuleOutput Evaluate(DesktopObservationResult observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var findings = new List<DesktopUiAuditFinding>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var uiTokens = new HashSet<string>(StringComparer.Ordinal);
        var skippedUserText = new List<string>();
        var inspected = 0;

        foreach (var element in observation.UiAutomation?.Elements ?? [])
        {
            if (element.IsSensitive)
            {
                Skip(skipped, DesktopUiAuditSkipReason.Sensitive);
                continue;
            }

            if (EditableRoles.Contains(element.Role, StringComparer.OrdinalIgnoreCase))
            {
                Skip(skipped, DesktopUiAuditSkipReason.EditableRole);
                foreach (var text in new[] { element.Name, element.Text })
                {
                    var normalized = Normalize(text);
                    if (normalized.Length > 0)
                    {
                        skippedUserText.Add(normalized);
                    }
                }

                continue;
            }

            inspected++;
            var order = new List<string>();
            var best = new Dictionary<string, (string Field, string Text, bool Whole)>(StringComparer.Ordinal);
            foreach (var (field, text) in new[] { ("name", element.Name), ("text", element.Text) })
            {
                foreach (var match in FindTokens(text))
                {
                    var token = match.Token;
                    if (!best.TryGetValue(token, out var current))
                    {
                        order.Add(token);
                        best[token] = (field, text!, match.Whole);
                    }
                    else if (match.Whole && !current.Whole)
                    {
                        best[token] = (field, text!, match.Whole);
                    }
                }
            }

            foreach (var token in order)
            {
                var (field, text, whole) = best[token];
                uiTokens.Add(token);
                var shown = Truncate(Normalize(text));
                findings.Add(new DesktopUiAuditFinding(
                    RuleId,
                    Version,
                    DesktopUiAuditSeverity.Medium,
                    whole ? DesktopUiAuditConfidence.High : DesktopUiAuditConfidence.Medium,
                    DesktopUiAuditSource.Uia,
                    token,
                    $"{element.Role} {field} {(whole ? "is" : "contains")} the placeholder \"{token}\": \"{shown}\".",
                    ElementRef: element.ElementRef,
                    WindowRef: element.WindowRef,
                    Role: element.Role,
                    Name: element.Name,
                    AutomationId: element.AutomationId,
                    BoundsPixels: element.BoundsPixels,
                    Field: field,
                    Text: shown));
            }
        }

        var suppressedOcrLines = 0;
        if (observation.Ocr is { IsAvailable: true, Text.Length: > 0 } ocr)
        {
            var seen = new HashSet<(string Token, string Line)>();
            foreach (var rawLine in ocr.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var normalizedLine = Normalize(rawLine);
                if (normalizedLine.Length == 0)
                {
                    continue;
                }

                if (skippedUserText.Any(text => text.Contains(normalizedLine, StringComparison.OrdinalIgnoreCase)))
                {
                    suppressedOcrLines++;
                    continue;
                }

                foreach (var match in FindTokens(normalizedLine))
                {
                    var token = match.Token;
                    if (uiTokens.Contains(token) || !seen.Add((token, normalizedLine.ToUpperInvariant())))
                    {
                        continue;
                    }

                    findings.Add(new DesktopUiAuditFinding(
                        RuleId,
                        Version,
                        DesktopUiAuditSeverity.Low,
                        DesktopUiAuditConfidence.Low,
                        DesktopUiAuditSource.Ocr,
                        token,
                        $"OCR text contains the placeholder \"{token}\" with no matching UI Automation text: \"{OcrEvidence(normalizedLine, match.Index, match.Length)}\".",
                        ImageRef: ocr.ImageRef,
                        OcrLine: OcrEvidence(normalizedLine, match.Index, match.Length)));
                }
            }
        }

        return new DesktopUiAuditRuleOutput(findings, inspected, skipped, suppressedOcrLines);
    }

    private static IEnumerable<TokenMatch> FindTokens(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            yield break;
        }

        foreach (var token in Tokens)
        {
            foreach (Match match in token.Pattern.Matches(normalized))
            {
                yield return new TokenMatch(token.Display ?? match.Value, match.Length == normalized.Length, match.Index, match.Length);
            }
        }
    }

    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Whitespace.Replace(text.Trim(), " ");

    private static string Truncate(string text) =>
        text.Length <= MaxEvidenceLength ? text : text[..MaxEvidenceLength] + "...";

    private static string OcrEvidence(string line, int matchIndex, int matchLength)
    {
        if (line.Length <= MaxEvidenceLength)
        {
            return line;
        }

        var start = Math.Max(0, matchIndex - (MaxEvidenceLength - matchLength) / 2);
        var end = Math.Min(line.Length, start + MaxEvidenceLength);
        start = Math.Max(0, end - MaxEvidenceLength);
        return (start > 0 ? "..." : string.Empty) + line[start..end] + (end < line.Length ? "..." : string.Empty);
    }

    private static void Skip(Dictionary<string, int> skipped, string reason) =>
        skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

    private sealed record PlaceholderToken(string? Display, Regex Pattern);
    private sealed record TokenMatch(string Token, bool Whole, int Index, int Length);
}
