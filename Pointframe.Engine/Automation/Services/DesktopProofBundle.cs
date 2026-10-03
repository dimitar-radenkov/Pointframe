using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Pointframe.Engine.Automation.Services;

// A proof bundle is one folder a reviewer can keep or hand on: the signed report.json, the evidence images
// it hashes, and index.html, a readable timeline of both. Only report.json and the images are covered by the
// proof; index.html is a view of them and says so.
public static class DesktopProofBundle
{
    public const string ReportFileName = "report.json";
    public const string IndexFileName = "index.html";
    public const string EvidenceFolderName = "evidence";

    private static readonly JsonSerializerOptions IndentedJson = new(DesktopProofService.CanonicalJson) { WriteIndented = true };

    public static async Task WriteAsync(DesktopTestReport report, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, ReportFileName),
            JsonSerializer.Serialize(report, IndentedJson),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(directory, IndexFileName),
            RenderIndex(report),
            cancellationToken).ConfigureAwait(false);
    }

    public static string RenderIndex(DesktopTestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            """);
        html.Append(CultureInfo.InvariantCulture, $"<title>Proof {E(report.SessionRef)}</title>\n");
        html.Append("""
            <style>
              :root { --bg: #f7f8fa; --surface: #ffffff; --border: #d9dee5; --text: #16202c; --dim: #556273;
                --passed: #15803d; --failed: #b91c1c; --inconclusive: #b45309; --mono: Consolas, "Cascadia Code", monospace; }
              @media (prefers-color-scheme: dark) { :root { --bg: #11161d; --surface: #19212b; --border: #2c3846; --text: #e3e8ee;
                --dim: #93a1b1; --passed: #4ade80; --failed: #f87171; --inconclusive: #fbbf24; color-scheme: dark; } }
              body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
              main { max-width: 1000px; margin: 0 auto; padding-block: 24px 48px; padding-inline: 16px; }
              h1 { font-size: 1.4rem; margin: 0 0 4px; } h2 { font-size: 1.1rem; margin: 28px 0 8px; }
              code, .mono { font-family: var(--mono); font-size: 0.85em; overflow-wrap: anywhere; }
              .verdict { font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; }
              .passed { color: var(--passed); } .failed { color: var(--failed); }
              .inconclusive, .uncovered { color: var(--inconclusive); }
              .scroll { overflow-x: auto; } table { border-collapse: collapse; width: 100%; }
              th, td { border: 1px solid var(--border); padding: 6px 10px; text-align: left; vertical-align: top; }
              th { background: var(--surface); }
              ol.timeline { list-style: none; padding: 0; margin: 0; display: grid; gap: 12px; }
              ol.timeline li { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 12px; min-width: 0; }
              .meta { color: var(--dim); font-size: 0.85rem; }
              img { max-width: 100%; height: auto; border: 1px solid var(--border); border-radius: 4px; margin-top: 8px; }
              .note { color: var(--dim); font-size: 0.9rem; border-top: 1px solid var(--border); margin-top: 32px; padding-top: 12px; }
            </style>
            </head>
            <body>
            <main>
            """);

        html.Append(CultureInfo.InvariantCulture, $"<h1>Proof of work: <span class=\"verdict {Css(report.Verdict)}\">{E(report.Verdict)}</span></h1>\n");
        html.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">Session <code>{E(report.SessionRef)}</code> · {Time(report.StartedUtc)} to {(report.CompletedUtc is { } completed ? Time(completed) : "not finished")}</p>\n");
        html.Append(CultureInfo.InvariantCulture, $"<p>Executable <code>{E(report.ExecutablePath ?? "unknown")}</code><br>SHA-256 <code>{E(report.ExecutableSha256 ?? "unknown")}</code></p>\n");

        if (report.Criteria.Count > 0)
        {
            html.Append("<h2>Acceptance criteria</h2>\n");
            html.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">Frozen at session start. SHA-256 <code>{E(report.CriteriaSha256 ?? "")}</code></p>\n");
            html.Append("<div class=\"scroll\"><table><tr><th>ID</th><th>Criterion</th><th>Verdict</th></tr>\n");
            foreach (var criterion in report.Criteria)
            {
                html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(criterion.Id)}</td><td>{E(criterion.Text)}</td><td class=\"verdict {Css(criterion.Verdict)}\">{E(criterion.Verdict)}</td></tr>\n");
            }

            html.Append("</table></div>\n");
        }

        html.Append("<h2>Timeline</h2>\n<ol class=\"timeline\">\n");
        var steps = report.Actions.Select(action => (action.RecordedUtc, Html: RenderAction(action)))
            .Concat(report.Checks.Select(check => (check.RecordedUtc, Html: RenderCheck(check))))
            .OrderBy(step => step.RecordedUtc);
        foreach (var step in steps)
        {
            html.Append(step.Html);
        }

        html.Append("</ol>\n");

        html.Append("<h2>Proof</h2>\n");
        if (report.Proof is { } proof)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>Key ID <code>{E(proof.KeyId)}</code><br>Root hash <code>{E(proof.RootHash)}</code><br>Algorithm <code>{E(proof.Algorithm)}</code><br>Entries signed: {proof.Entries.Count}</p>\n");
        }
        else
        {
            html.Append("<p class=\"failed\">This report is not signed.</p>\n");
        }

        html.Append("""
            <p class="note">This page is a readable view of <code>report.json</code> and is not itself signed. The proof covers
            <code>report.json</code> and the images in <code>evidence/</code>. Verify them with
            <code>DesktopProofService.Verify</code>, and compare the key ID with the one you expect: anyone can sign an edited
            report with their own key.</p>
            </main>
            </body>
            </html>
            """);
        return html.ToString();
    }

    private static string RenderAction(DesktopTestActionReport action)
    {
        var result = action.Result;
        var outcome = result.Error is { } error ? $" · {E(error.Code)}: {E(error.Message)}" : "";
        return $"""
            <li><strong>Action: {E(action.Description)}</strong>
            <div class="meta">{Time(action.RecordedUtc)} · dispatch {E(result.Dispatch.ToString())} · verification {E(result.Verification.ToString())}{outcome}</div>
            {RenderEvidence(action.Evidence)}</li>

            """;
    }

    private static string RenderCheck(DesktopTestCheckReport check)
    {
        var verdict = check.Verdict.ToString().ToLowerInvariant();
        var labels = new List<string>();
        if (check.CriterionId is not null)
        {
            labels.Add($"for {E(check.CriterionId)}");
        }

        if (check.NegativeControl)
        {
            labels.Add("negative control");
        }

        var label = labels.Count == 0 ? "" : $" · {string.Join(" · ", labels)}";
        var message = check.Message is null ? "" : $" · {E(check.Message)}";
        return $"""
            <li><strong>Check: <code>{E(check.Description)}</code></strong> <span class="verdict {Css(verdict)}">{E(verdict)}</span>
            <div class="meta">{Time(check.RecordedUtc)}{label}{message}</div>
            {RenderEvidence(check.Evidence)}</li>

            """;
    }

    private static string RenderEvidence(DesktopTestEvidence? evidence)
    {
        if (evidence is null)
        {
            return "";
        }

        if (evidence.Path is null)
        {
            return $"<div class=\"meta failed\">No screenshot: {E(evidence.Error ?? "unknown")}</div>";
        }

        var path = $"{EvidenceFolderName}/{evidence.Path}";
        return $"<img src=\"{E(path)}\" alt=\"Screenshot {E(evidence.Path)}\" loading=\"lazy\"><div class=\"meta mono\">{E(evidence.Path)} · SHA-256 {E(evidence.Sha256 ?? "")}</div>";
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string Css(string verdict) => verdict switch
    {
        "passed" or "failed" or "inconclusive" or "uncovered" => verdict,
        _ => "inconclusive",
    };

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
