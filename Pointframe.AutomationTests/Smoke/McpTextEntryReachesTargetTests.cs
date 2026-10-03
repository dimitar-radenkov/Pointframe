using Pointframe.AutomationTests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.AutomationTests.Smoke;

/// <summary>
/// Verifies that synthesized text actually reaches a real application's edit control. The fixture
/// reports its own text box contents, so this needs no OCR - which matters because the driver's only
/// other read-back path depends on the Windows OCR engine.
/// </summary>
[Trait("Category", "DesktopAutomation")]
public class McpTextEntryReachesTargetTests(ITestOutputHelper output)
{
    private const string TypedText = "hello from agent";

    [SkippableFact]
    public async Task EnterTextArrivesInTheTargetsEditControl()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var fixturePath = Environment.GetEnvironmentVariable(DesktopFixtureHarness.FixtureExecutableVariable);
        Skip.If(string.IsNullOrWhiteSpace(fixturePath), "Set POINTFRAME_FIXTURE_EXECUTABLE for the desktop gate.");
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        var artifacts = Path.Combine(Path.GetTempPath(), $"pointframe-text-{Guid.NewGuid():N}");

        await using var harness = await DesktopFixtureHarness.StartAsync(
            fixturePath!,
            mcpPath,
            artifacts,
            0,
            criteria: [$"The text box shows '{TypedText}'"]);
        var displays = await harness.ListDisplaysAsync();
        var display = displays[0];

        var before = harness.ReadState();
        output.WriteLine($"[before] textBoxText='{before.TextBoxText}' box={before.TextBoxScreen}");

        var observation = await harness.ObserveAsync(display);
        var image = observation.Images[0];

        // Aim at the middle of the text box, converted from desktop pixels into the preview's space.
        var centreX = before.TextBoxScreen.X + (before.TextBoxScreen.Width / 2);
        var centreY = before.TextBoxScreen.Y + (before.TextBoxScreen.Height / 2);
        var imageX = (int)Math.Round((centreX - image.DesktopX) * (double)image.Width / image.DesktopWidth);
        var imageY = (int)Math.Round((centreY - image.DesktopY) * (double)image.Height / image.DesktopHeight);
        output.WriteLine($"[target] desktop ({centreX},{centreY}) -> preview ({imageX},{imageY})");

        var result = await harness.EnterTextAsync(observation, imageX, imageY, TypedText);
        output.WriteLine($"[enter_text] {result.GetProperty("structuredContent").GetRawText()}");

        var after = harness.WaitForState(
            state => state.TextBoxText.Contains(TypedText, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        output.WriteLine($"[after] textBoxText='{after.TextBoxText}'");

        Assert.Equal(TypedText, after.TextBoxText);

        // Defect 2: the driver must be able to confirm this itself, rather than the test reading the
        // fixture's private state file. This is the first verification path the agent actually has.
        var check = await harness.CheckUiAsync("textEquals", automationId: "textBox", expected: TypedText, criterionId: "C1");
        var checkJson = check.GetProperty("structuredContent");
        output.WriteLine($"[check_ui textEquals] {checkJson.GetRawText()}");
        Assert.Equal("passed", checkJson.GetProperty("verification").GetString());

        // The negative control: the same oracle must reject a wrong expectation, or its "passed" above
        // proves nothing. Recorded as a negative control, it passes because the condition does not hold.
        var absent = await harness.CheckUiAsync(
            "textEquals",
            automationId: "textBox",
            expected: "something else",
            timeoutSeconds: 1,
            expectFailure: true);
        var absentJson = absent.GetProperty("structuredContent");
        output.WriteLine($"[check_ui negative control] {absentJson.GetRawText()}");
        Assert.Equal("passed", absentJson.GetProperty("verification").GetString());
        Assert.False(absentJson.GetProperty("matches").GetBoolean());

        var report = (await harness.GetTestReportAsync()).GetProperty("structuredContent");
        output.WriteLine($"[report] {report.GetRawText()}");
        Assert.Equal("passed", report.GetProperty("verdict").GetString());
        var criterion = Assert.Single(report.GetProperty("criteria").EnumerateArray());
        Assert.Equal("passed", criterion.GetProperty("verdict").GetString());
        Assert.False(string.IsNullOrEmpty(report.GetProperty("criteriaSha256").GetString()));

        // The fixture policy asks for all evidence: every check must point at a server-captured image whose
        // bytes still match the hash in the report.
        var evidenceDirectory = report.GetProperty("evidenceDirectory").GetString()!;
        foreach (var recorded in report.GetProperty("checks").EnumerateArray())
        {
            var evidence = recorded.GetProperty("evidence");
            var file = Path.Combine(evidenceDirectory, evidence.GetProperty("path").GetString()!);
            Assert.True(File.Exists(file), $"Missing evidence {file}: {evidence.GetRawText()}");
            Assert.Equal(
                evidence.GetProperty("sha256").GetString(),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        }

        // The report as the MCP client received it must verify: hash chain, signature from this user's
        // persisted key, and every evidence file.
        var proof = Pointframe.Engine.Automation.Services.DesktopProofService.Verify(report.GetRawText(), evidenceDirectory);
        output.WriteLine($"[proof] valid={proof.IsValid} keyId={proof.KeyId} problems={string.Join("; ", proof.Problems)}");
        Assert.True(proof.IsValid, string.Join("; ", proof.Problems));

        // The proof bundle on disk must verify on its own, without the MCP response.
        var sessionDirectory = report.GetProperty("sessionDirectory").GetString()!;
        var reportPath = Path.Combine(sessionDirectory, "report.json");
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "index.html")), "The bundle has no index.html.");
        var fromDisk = Pointframe.Engine.Automation.Services.DesktopProofService.Verify(File.ReadAllText(reportPath), evidenceDirectory);
        Assert.True(fromDisk.IsValid, string.Join("; ", fromDisk.Problems));
        output.WriteLine($"[bundle] {sessionDirectory}");

        // Replay in a fresh launch. The fixture does not save its text, so C1 must not replay as passed, while
        // the negative control must still reject the wrong text: replay reports what really survives a restart.
        var replay = (await harness.ReplayInFreshSessionAsync(reportPath)).GetProperty("structuredContent");
        output.WriteLine($"[replay] {replay.GetRawText()}");
        Assert.Equal("differs", replay.GetProperty("status").GetString());
        var replayedCriterion = Assert.Single(replay.GetProperty("criteria").EnumerateArray());
        Assert.Equal("passed", replayedCriterion.GetProperty("original").GetString());
        Assert.Equal("failed", replayedCriterion.GetProperty("replayed").GetString());
        var negativeControl = replay.GetProperty("checks")[1];
        Assert.Equal("passed", negativeControl.GetProperty("replayed").GetString());
    }
}
