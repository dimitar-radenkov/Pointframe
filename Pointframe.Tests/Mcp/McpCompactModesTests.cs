using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

// The additive compact modes: every default must stay what it was, and each opt-in must keep what an
// agent needs to act safely (refs, observation identity, action outcome) and what a report needs as proof.
public sealed class McpCompactModesTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    public McpCompactModesTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ----- image options -----

    [Fact]
    public void ImageOptions_WithNothingSet_AreTheOriginalDefaults()
    {
        Assert.True(McpImageOptions.TryCreate(null, null, out var options));

        Assert.Equal(1600, options.MaxLongestEdge);
        Assert.False(options.Jpeg);
    }

    [Theory]
    [InlineData(63, null)]
    [InlineData(1601, null)]
    [InlineData(null, "gif")]
    [InlineData(800, "bmp")]
    public void ImageOptions_RejectOutOfRangeEdgeAndUnknownFormat(int? edge, string? format)
    {
        Assert.False(McpImageOptions.TryCreate(edge, format, out _));
    }

    [Fact]
    public void ImageOptions_Default_ReturnsTheOriginalPngBytesWhenTheImageFits()
    {
        var png = CreatePng(800, 600);

        var (bytes, mimeType) = McpImageOptions.Default.Encode(png);

        Assert.Equal("image/png", mimeType);
        Assert.Equal(png, bytes);
    }

    [Fact]
    public void ImageOptions_MaxEdge_DownscalesPreservingAspectRatio()
    {
        McpImageOptions.TryCreate(400, null, out var options);

        var (bytes, mimeType) = options.Encode(CreatePng(1600, 900));

        Assert.Equal("image/png", mimeType);
        var (width, height) = Size(bytes);
        Assert.Equal(400, width);
        Assert.Equal(225, height);
    }

    [Fact]
    public void ImageOptions_Jpeg_ProducesAJpegThatIsSmallerThanThePng()
    {
        McpImageOptions.TryCreate(null, "jpeg", out var options);
        var png = CreateNoisyPng(1200, 800);

        var (bytes, mimeType) = options.Encode(png);

        Assert.Equal("image/jpeg", mimeType);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.True(bytes.Length < png.Length, $"jpeg {bytes.Length} bytes, png {png.Length} bytes");
        Assert.Equal((1200, 800), Size(bytes));
    }

    [Fact]
    public void CaptureResultBuilder_WithImageOptions_InlinesTheRequestedFormatAndSize()
    {
        var path = Path.Combine(_directory, "capture.png");
        File.WriteAllBytes(path, CreateNoisyPng(1200, 800));
        var response = CaptureResponseFor(path);
        McpImageOptions.TryCreate(300, "jpeg", out var options);

        var result = McpCaptureResultBuilder.Build(response, includeImage: true, options);

        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal((300, 200), Size(image.DecodedData.ToArray()));
        Assert.True(result.StructuredContent!.Value.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void CaptureResultBuilder_WithoutImageOptions_StillInlinesTheOriginalPng()
    {
        var path = Path.Combine(_directory, "capture.png");
        var png = CreatePng(640, 480);
        File.WriteAllBytes(path, png);

        var result = McpCaptureResultBuilder.Build(CaptureResponseFor(path), includeImage: true);

        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(png, image.DecodedData.ToArray());
    }

    [Fact]
    public async Task CaptureMonitorAsync_WithInvalidImageOptions_IsRejectedBeforeCapturing()
    {
        var capture = new Mock<IDirectCaptureService>(MockBehavior.Strict);
        var tools = new PointframeMcpTools(capture.Object, new Mock<IDirectRecordingMcpService>().Object, new Mock<ICaptureCatalogService>().Object);

        var result = await tools.CaptureMonitorAsync(@"\\.\DISPLAY1", maxImageEdge: 10);

        Assert.True(result.IsError);
        Assert.Contains("invalid_image_options", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        capture.VerifyNoOtherCalls();
    }

    [Fact]
    public void CapturePreviewImage_CreateDownscaledJpeg_RejectsOutOfRangeQuality()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CapturePreviewImage.CreateDownscaledJpeg(CreatePng(10, 10), 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CapturePreviewImage.CreateDownscaledJpeg(CreatePng(10, 10), 100, 101));
    }

    // ----- compact observation -----

    [Fact]
    public void FullObservation_SerializesExactlyAsBefore()
    {
        var response = SampleObservation();

        var result = DesktopObservationResultBuilder.Build(response, observation: null, includeImages: false);

        Assert.Equal(JsonSerializer.Serialize(response, Web), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Contains("\"toggleState\":null", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public void CompactObservation_KeepsEveryRefAndIdentityAnActionNeeds()
    {
        var full = SampleObservation();

        var compact = DesktopTestingResponseMapper.ToCompact(full);

        Assert.Equal(full.ObservationRef, compact.ObservationRef);
        Assert.Equal(full.ProcessRef, compact.ProcessRef);
        Assert.Equal(full.TopologyGeneration, compact.TopologyGeneration);
        Assert.Equal(full.PixelCapturedUtc, compact.PixelCapturedUtc);
        var image = Assert.Single(compact.Images);
        Assert.Equal("image-1", image.ImageRef);
        Assert.Equal((1290, 897), (image.Width, image.Height));
        Assert.Equal(new McpPixelBounds(635, 231, 1290, 897), image.DesktopBoundsPixels);
        Assert.Equal(full.Elements.Select(element => element.ElementRef), compact.Elements.Select(element => element.ElementRef));
        Assert.Equal("window-a", compact.WindowRef);
        Assert.Null(compact.Elements[0].WindowRef);
        Assert.Equal("window-b", compact.Elements[2].WindowRef);
        Assert.Equal([671, 476, 157, 53], compact.Elements[0].Bounds);
        Assert.Equal("saveButton", compact.Elements[0].AutomationId);
        Assert.Null(compact.Elements[0].Disabled);
        Assert.True(compact.Elements[1].Disabled);
        Assert.Equal("Off", compact.Elements[2].ToggleState);
    }

    [Fact]
    public void CompactObservation_TruncatesVeryLongNamesAndTextWithTheOmittedCount()
    {
        var long300 = new string('x', 300);
        var full = SampleObservation() with
        {
            Elements = [new DesktopElementBlock("el-1", "window-a", "Text", long300, "statusLabel", new McpPixelBounds(0, 0, 10, 10), true, null, null, long300)],
        };

        var element = Assert.Single(DesktopTestingResponseMapper.ToCompact(full).Elements);

        Assert.Equal(new string('x', 200) + "... (+100 chars)", element.Name);
        Assert.Equal(element.Name, element.Text);
        Assert.Equal("el-1", element.ElementRef);
    }

    [Fact]
    public void CompactObservation_OmitsNullsAndIsMuchSmallerThanFull()
    {
        var full = SampleObservation();
        var compact = DesktopTestingResponseMapper.ToCompact(full);

        var result = DesktopObservationResultBuilder.BuildCompact(compact, observation: null, includeImages: false);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain("null", text);
        Assert.Equal("compact", result.StructuredContent!.Value.GetProperty("detail").GetString());
        Assert.True(text.Length < JsonSerializer.Serialize(full, Web).Length * 0.7, text);
        Assert.False(result.IsError);
    }

    [Fact]
    public void CompactObservation_CarriesTheErrorAndFlagsItAsAnError()
    {
        var failed = SampleObservation() with { Error = new McpCaptureError("UiaUnavailable", "UI automation data was not available.") };

        var result = DesktopObservationResultBuilder.BuildCompact(DesktopTestingResponseMapper.ToCompact(failed), observation: null, includeImages: false);

        Assert.True(result.IsError);
        Assert.Equal("UiaUnavailable", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ObserveAppAsync_DefaultsToFullDetailAndFullEdge()
    {
        var (tools, observations) = CreateObservingTools();

        var result = await tools.ObserveAppAsync("session-1", [new McpPixelBounds(0, 0, 100, 100)], includeImages: false);

        Assert.False(result.StructuredContent!.Value.TryGetProperty("detail", out _));
        Assert.True(result.StructuredContent.Value.GetProperty("elements")[0].TryGetProperty("isEnabled", out _));
        observations.Verify(
            service => service.ObserveAsync(It.Is<DesktopObservationRequest>(request => request.MaxImageLongestEdge == null), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ObserveAppAsync_Compact_ReturnsCompactElementsAndPassesTheEdgeCap()
    {
        var (tools, observations) = CreateObservingTools();

        var result = await tools.ObserveAppAsync("session-1", [new McpPixelBounds(0, 0, 100, 100)], includeImages: false, detail: "compact", maxImageEdge: 400);

        var structured = result.StructuredContent!.Value;
        Assert.Equal("compact", structured.GetProperty("detail").GetString());
        Assert.Equal("observation-1", structured.GetProperty("observationRef").GetString());
        Assert.Equal("el-1", structured.GetProperty("elements")[0].GetProperty("elementRef").GetString());
        observations.Verify(
            service => service.ObserveAsync(It.Is<DesktopObservationRequest>(request => request.MaxImageLongestEdge == 400), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("verbose", null, "InvalidDetail")]
    [InlineData("full", 20, "InvalidImageOptions")]
    [InlineData("compact", 5000, "InvalidImageOptions")]
    public async Task ObserveAppAsync_RejectsInvalidOptionsWithoutObserving(string detail, int? edge, string code)
    {
        var (tools, observations) = CreateObservingTools();

        var result = await tools.ObserveAppAsync("session-1", [new McpPixelBounds(0, 0, 100, 100)], detail: detail, maxImageEdge: edge);

        Assert.True(result.IsError);
        Assert.Equal(code, result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        observations.Verify(
            service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ObservationService_WithEdgeCap_DownscalesAndKeepsCoordinatesMappingBackToTheDesktop()
    {
        var service = new DesktopObservationService(new FixedCaptureEngine(1000, 500), new DesktopObservationStore());

        var result = await service.ObserveAsync(new DesktopObservationRequest(
            Process(),
            [new PixelBounds(100, 50, 1000, 500)],
            IncludeUiAutomation: false,
            MaxImageLongestEdge: 250));

        var image = Assert.Single(result.Observation.Images);
        Assert.Equal((250, 125), (image.Width, image.Height));
        var point = service.ToDesktopPixels(result.Observation.ObservationRef, image.ImageRef, 125, 62);
        Assert.InRange(point.X, 595, 605);
        Assert.InRange(point.Y, 295, 305);
    }

    // ----- compact report -----

    [Fact]
    public async Task GetTestReportAsync_Compact_TrimsTheResponseButWritesTheFullReportToDisk()
    {
        var report = SampleReport(_directory);
        var coordinator = new Mock<IDesktopActionCoordinator>();
        coordinator.Setup(item => item.FinalizeAsync("session-1", It.IsAny<CancellationToken>())).ReturnsAsync(report);
        var tools = CreateTools(coordinator.Object, new Mock<IDesktopObservationService>().Object);

        var compact = await tools.GetTestReportAsync("session-1", "compact");

        Assert.Equal("passed", compact.Verdict);
        Assert.Equal(report.Criteria, compact.Criteria);
        Assert.Equal(report.Actions.Select(action => action.Result), compact.Actions.Select(action => action.Result));
        Assert.All(compact.Actions, action => Assert.Null(action.Evidence));
        Assert.All(compact.Checks, check => Assert.Null(check.Evidence));
        Assert.All(compact.Checks, check => Assert.Null(check.Spec));
        Assert.Null(compact.Proof);
        Assert.NotNull(report.Proof);
        Assert.Equal(report.SessionDirectory, compact.SessionDirectory);

        var onDisk = JsonSerializer.Deserialize<DesktopTestReport>(
            await File.ReadAllTextAsync(Path.Combine(_directory, DesktopProofBundle.ReportFileName)),
            DesktopProofService.CanonicalJson)!;
        Assert.Equal(report.Proof!.Entries.Count, onDisk.Proof!.Entries.Count);
        Assert.NotNull(onDisk.Actions[0].Evidence);
        Assert.NotNull(onDisk.Checks[0].Spec);
    }

    [Fact]
    public async Task GetTestReportAsync_Default_ReturnsTheFullReport()
    {
        var report = SampleReport(_directory);
        var coordinator = new Mock<IDesktopActionCoordinator>();
        coordinator.Setup(item => item.FinalizeAsync("session-1", It.IsAny<CancellationToken>())).ReturnsAsync(report);
        var tools = CreateTools(coordinator.Object, new Mock<IDesktopObservationService>().Object);

        var result = await tools.GetTestReportAsync("session-1");

        Assert.Same(report, result);
    }

    [Fact]
    public async Task GetTestReportAsync_WithUnknownDetail_FailsBeforeFinalizing()
    {
        var coordinator = new Mock<IDesktopActionCoordinator>(MockBehavior.Strict);
        var tools = CreateTools(coordinator.Object, new Mock<IDesktopObservationService>().Object);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.GetTestReportAsync("session-1", "tiny"));
    }

    // ----- compact text -----

    [Fact]
    public void OutputOptions_AreOffByDefaultAndOptInByFlagOrEnvironment()
    {
        Assert.False(McpOutputOptions.Parse([], _ => null).CompactText);
        Assert.False(McpOutputOptions.Parse(["--desktop-testing"], _ => "0").CompactText);
        Assert.True(McpOutputOptions.Parse(["--compact-text"], _ => null).CompactText);
        Assert.True(McpOutputOptions.Parse([], name => name == "POINTFRAME_MCP_COMPACT_TEXT" ? "1" : null).CompactText);
        Assert.True(McpOutputOptions.Parse([], _ => "TRUE").CompactText);
    }

    [Fact]
    public void CompactText_ForAnAction_KeepsEveryOutcomeFieldAndIsShorterThanTheJson()
    {
        var result = ActionResult("Complete", "Passed", error: null);
        var original = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        var compacted = McpCompactTextFilter.Apply("desktop_click", Arguments("action-123"), result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text;
        Assert.Contains("operationStatus=Completed", text);
        Assert.Contains("dispatch=Complete", text);
        Assert.Contains("verification=Passed", text);
        Assert.Contains("observationStatus=NotRequested", text);
        Assert.DoesNotContain("{\"", text);
        Assert.DoesNotContain("session-1", text);
        Assert.True(text.Length < original.Length, text);
        Assert.Equal("Complete", compacted.StructuredContent!.Value.GetProperty("dispatch").GetString());
        Assert.Equal("session-1", compacted.StructuredContent.Value.GetProperty("sessionRef").GetString());
    }

    [Fact]
    public void CompactText_ForStartSession_KeepsTheSessionAndTargetRefs()
    {
        var response = new DesktopTestingActionResponse(1, "Completed", "Complete", "NotRequested", "NotRequested", null, "session-1", "target-1");
        var structured = JsonSerializer.SerializeToElement(response, Web);
        var result = new CallToolResult { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };

        var compacted = McpCompactTextFilter.Apply("desktop_start_test_session", Arguments("action-1"), result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text;
        Assert.Contains("sessionRef=session-1", text);
        Assert.Contains("targetRef=target-1", text);
    }

    [Theory]
    [InlineData("Partial")]
    [InlineData("Unknown")]
    public void CompactText_WhenTheDispatchIsUncertain_TellsTheAgentNotToResend(string dispatch)
    {
        var result = ActionResult(dispatch, "Inconclusive", error: null);

        var compacted = McpCompactTextFilter.Apply("desktop_click", Arguments("action-9"), result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text;
        Assert.Contains("do not resend", text);
        Assert.Contains("desktop_get_action_result with actionId=action-9", text);
    }

    [Fact]
    public void CompactText_WhenNothingWasDispatched_SaysNoInputWasSentAndKeepsTheError()
    {
        var result = ActionResult("NotStarted", "Failed", new McpCaptureError("StaleObservation", "The observation expired."));

        var compacted = McpCompactTextFilter.Apply("desktop_click", Arguments("action-3"), result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text;
        Assert.Contains("error=StaleObservation: The observation expired.", text);
        Assert.Contains("no input was sent", text);
    }

    [Theory]
    [InlineData("list_windows")]
    [InlineData("list_displays")]
    [InlineData("search_captures")]
    [InlineData("desktop_observe_app")]
    [InlineData("desktop_replay_checks")]
    public void CompactText_LeavesDataBearingToolsUntouched(string tool)
    {
        var result = ActionResult("Complete", "Passed", error: null);
        var original = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        var compacted = McpCompactTextFilter.Apply(tool, Arguments("a"), result);

        Assert.Equal(original, Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text);
    }

    [Fact]
    public void CompactText_ForAReport_NamesVerdictCriteriaFailuresAndTheBundle()
    {
        var report = SampleReport(_directory) with { Verdict = "failed" };
        var structured = JsonSerializer.SerializeToElement(report, DesktopProofService.CanonicalJson);
        var result = new CallToolResult { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };

        var compacted = McpCompactTextFilter.Apply("desktop_get_test_report", Arguments("a"), result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text;
        Assert.Contains("verdict=failed", text);
        Assert.Contains("C1=passed", text);
        Assert.Contains("sessionDirectory=", text);
        Assert.Contains("not-passed: exists automationId=missing (Failed)", text);
        Assert.Contains("rootHash=ROOT", text);
        Assert.True(text.Length < structured.GetRawText().Length / 2, text);
    }

    [Fact]
    public void CompactText_WhenThereIsNoStructuredContent_ReturnsTheResultUnchanged()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom" }] };

        var compacted = McpCompactTextFilter.Apply("desktop_click", Arguments("a"), result);

        Assert.Equal("boom", Assert.IsType<TextContentBlock>(Assert.Single(compacted.Content)).Text);
    }

    // ----- contract -----

    [Theory]
    [InlineData(typeof(PointframeMcpTools), "CaptureMonitorAsync", new[] { "maxImageEdge", "imageFormat" })]
    [InlineData(typeof(PointframeMcpTools), "CaptureWindowAsync", new[] { "maxImageEdge", "imageFormat" })]
    [InlineData(typeof(PointframeMcpTools), "ReadTextFromMonitorAsync", new[] { "maxImageEdge", "imageFormat" })]
    [InlineData(typeof(PointframeMcpTools), "ReadTextFromWindowAsync", new[] { "maxImageEdge", "imageFormat" })]
    [InlineData(typeof(DesktopTestingMcpTools), "ObserveAppAsync", new[] { "detail", "maxImageEdge" })]
    [InlineData(typeof(DesktopTestingMcpTools), "GetTestReportAsync", new[] { "detail" })]
    public void CompactOptions_AreOptionalSoExistingCallersKeepWorking(Type toolType, string method, string[] options)
    {
        var parameters = toolType.GetMethod(method)!.GetParameters().ToDictionary(parameter => parameter.Name!);

        foreach (var option in options)
        {
            Assert.True(parameters[option].IsOptional, $"{method}.{option} must be optional");
        }

        Assert.Equal("full", parameters.TryGetValue("detail", out var detail) ? detail.DefaultValue : "full");
        if (parameters.TryGetValue("imageFormat", out var format))
        {
            Assert.Null(format.DefaultValue);
        }
    }

    [Fact]
    public void CompactOptions_NeverBecomeRequiredInThePublishedInputSchema()
    {
        var required = new[]
            {
                (typeof(PointframeMcpTools), "CaptureMonitorAsync"),
                (typeof(PointframeMcpTools), "CaptureWindowAsync"),
                (typeof(DesktopTestingMcpTools), "ObserveAppAsync"),
                (typeof(DesktopTestingMcpTools), "GetTestReportAsync"),
            }
            .SelectMany(entry =>
            {
                var tool = McpServerTool.Create(entry.Item1.GetMethod(entry.Item2)!, _ => null!).ProtocolTool;
                var names = tool.InputSchema.TryGetProperty("required", out var list)
                    ? list.EnumerateArray().Select(item => item.GetString()!).ToArray()
                    : [];
                return names.Select(name => $"{tool.Name}.{name}");
            })
            .ToArray();

        Assert.DoesNotContain(required, name => name.EndsWith(".detail") || name.EndsWith(".maxImageEdge") || name.EndsWith(".imageFormat"));
    }
    // ----- helpers -----

    private static IDictionary<string, JsonElement> Arguments(string actionId) =>
        new Dictionary<string, JsonElement> { ["actionId"] = JsonSerializer.SerializeToElement(actionId) };

    private static CallToolResult ActionResult(string dispatch, string verification, McpCaptureError? error)
    {
        var response = new DesktopTestingActionResponse(1, "Completed", dispatch, verification, "NotRequested", error, "session-1", null);
        var structured = JsonSerializer.SerializeToElement(response, Web);
        return new CallToolResult { Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured, IsError = error is not null };
    }

    private static DesktopTestingObservationResponse SampleObservation() =>
        new(
            1,
            "observation-1",
            "Running",
            "Available",
            "Available",
            "process-1",
            false,
            2,
            DateTimeOffset.Parse("2026-10-05T08:42:56Z"),
            DateTimeOffset.Parse("2026-10-05T08:42:56Z"),
            [new DesktopImageBlock("image-1", 1290, 897, new McpPixelBounds(635, 231, 1290, 897), DateTimeOffset.Parse("2026-10-05T08:42:56Z"))],
            [
                new DesktopElementBlock("el-1", "window-a", "Button", "Save", "saveButton", new McpPixelBounds(671, 476, 157, 53), true, null, null, null),
                new DesktopElementBlock("el-2", "window-a", "Button", "Close", "closeButton", new McpPixelBounds(843, 476, 157, 53), false, null, null, null),
                new DesktopElementBlock("el-3", "window-b", "CheckBox", "Option", "checkBox", new McpPixelBounds(671, 563, 232, 29), true, "Off", null, null),
            ]);

    private static DesktopProcessIdentity Process() =>
        new("process-1", 1234, DateTimeOffset.UnixEpoch, "C:/fixture/fixture.exe", new string('a', 64));

    private (DesktopTestingMcpTools Tools, Mock<IDesktopObservationService> Observations) CreateObservingTools()
    {
        var sessions = new Mock<IDesktopTestSessionService>();
        sessions
            .Setup(service => service.GetAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopTestSessionSnapshot(
                "session-1",
                "target",
                DesktopSessionState.Active,
                new DesktopTargetReference("target-1", "target", 1, Process(), default, LaunchedByDriver: true)));
        var observation = new DesktopObservation(
            1,
            "observation-1",
            Process(),
            DesktopTargetState.Running,
            DesktopObservationStatus.Available,
            [new DesktopImageReference("image-1", 100, 100, new PixelBounds(0, 0, 100, 100), DateTimeOffset.UtcNow)],
            DesktopUiAutomationStatus.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var ui = new DesktopUiSnapshot(
            DesktopUiAutomationStatus.Available,
            [new DesktopUiElementSnapshot("el-1", "window-a", "Button", "Save", "saveButton", new PixelBounds(1, 2, 3, 4), true)],
            DateTimeOffset.UtcNow);
        var observations = new Mock<IDesktopObservationService>();
        observations
            .Setup(service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopObservationResult(observation, ui, 1));
        return (CreateTools(new Mock<IDesktopActionCoordinator>().Object, observations.Object, sessions), observations);
    }

    private static DesktopTestingMcpTools CreateTools(IDesktopActionCoordinator coordinator, IDesktopObservationService observations, Mock<IDesktopTestSessionService>? sessions = null) =>
        new(
            coordinator,
            (sessions ?? new Mock<IDesktopTestSessionService>()).Object,
            observations,
            new Mock<IWindowsDesktopInputService>().Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            new Mock<IDesktopUiCheckService>().Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            new Mock<IDesktopTestReportService>().Object,
            new Mock<IDesktopEvidenceRecorder>().Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null));

    private static DesktopTestReport SampleReport(string sessionDirectory)
    {
        var evidence = new DesktopTestEvidence("0001-action.png", "AB", new PixelBounds(0, 0, 10, 10), DateTimeOffset.UnixEpoch);
        var action = new DesktopActionResult(1, "8d1d7e35-0883-4a5d-a41e-e61019d2e6b5", DesktopOperationStatus.Completed, DesktopDispatchStatus.Complete, DesktopVerificationStatus.NotRequested, DesktopObservationStatus.NotRequested);
        return new DesktopTestReport(
            1,
            "session-1",
            "C:/fixture/fixture.exe",
            "SHA",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [new DesktopTestActionReport("action-1", "click", action, DateTimeOffset.UnixEpoch, evidence)],
            [
                new DesktopTestCheckReport("exists automationId=saveButton", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UnixEpoch, "C1", false, evidence, new DesktopCheckSpec("exists", "saveButton", null, null, null, null, 5)),
                new DesktopTestCheckReport("exists automationId=missing", DesktopVerificationStatus.Failed, false, "server-uia", null, DateTimeOffset.UnixEpoch),
            ],
            "passed",
            [new DesktopTestCriterion("C1", "Save exists", "passed")],
            "CRITERIA",
            Path.Combine(sessionDirectory, "evidence"),
            sessionDirectory,
            new DesktopProof(
                "sha256-chain+ecdsa-p256-sha256",
                [new DesktopProofEntry("header", 0, "H0"), new DesktopProofEntry("action", 0, "H1"), new DesktopProofEntry("summary", 0, "ROOT")],
                "ROOT",
                "KEY",
                "PUB",
                "SIG"));
    }

    private static McpCaptureResponse CaptureResponseFor(string path) =>
        new(
            1,
            true,
            Artifact: new McpArtifactDescriptor(
                1,
                "op-1",
                new McpImageArtifactMetadata(1, "artifact-1", "image", path, "SHA", 1, DateTimeOffset.UnixEpoch, "monitor", @"\.\DISPLAY1", 1, 1, new McpPixelBounds(0, 0, 1200, 800), new McpPixelBounds(0, 0, 1200, 800))));

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] CreateNoisyPng(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        var random = new Random(7);
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(Math.Clamp((x / 5) + random.Next(8), 0, 255), Math.Clamp((y / 4) + random.Next(8), 0, 255), 90 + random.Next(8)));
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static (int Width, int Height) Size(byte[] image)
    {
        using var stream = new MemoryStream(image);
        using var bitmap = new Bitmap(stream);
        return (bitmap.Width, bitmap.Height);
    }

    private sealed class FixedCaptureEngine(int width, int height) : IDisplayCaptureEngine
    {
        public Bitmap Capture(PixelBounds boundsPixels) => new(width, height);

        public CapturedMonitor CaptureMonitor(string monitorName) => throw new NotSupportedException();

        public IReadOnlyList<DisplayDescriptor> GetDisplays() => [];
    }
}
