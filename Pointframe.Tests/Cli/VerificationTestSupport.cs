using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Cli;

internal sealed class VerificationFixture : IDisposable
{
    internal const string ValidScenario = """
        {
          "id": "save-text",
          "criteria": ["The text box shows the typed text."],
          "steps": [
            { "enterText": { "automationId": "textBox", "text": "hello" } },
            { "invoke": { "automationId": "saveButton" } },
            { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "hello", "criterion": "C1" } },
            { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "not-hello", "expectFailure": true, "timeoutSeconds": 1 } }
          ]
        }
        """;

    internal const string DefaultApp = """{ "id": "fixture", "executable": "bin/App.exe" }""";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Pointframe.VerificationTests", Guid.NewGuid().ToString("N"));

    internal string McpPath => Path.Combine(Root, "mcp", "Pointframe.Mcp.exe");

    internal string SpecPath => Path.Combine(Root, ".pointframe", "verify.json");

    internal VerificationStore Store => new(Path.Combine(Root, "store"));

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(Root))
        {
            // git writes its object files read-only, and Directory.Delete refuses those.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }

    internal string WriteSpec(params string[] scenarios) =>
        WriteSpecWith(app: DefaultApp, gates: null, scenarios);

    internal string WriteScenarioExport(string id, string steps)
    {
        var path = Path.Combine(Root, "scenario-export.json");
        Directory.CreateDirectory(Root);
        File.WriteAllText(path, $$"""{ "scenario": { "id": "{{id}}", "steps": {{steps}} }, "unsupported": [], "complete": true }""");
        return path;
    }

    // Written specs are approved (verify trust) unless a test is about approval: most tests are about what a
    // run does once a person has approved the spec's commands.
    internal string WriteSpecWith(string? app, string? gates, params string[] scenarios) =>
        WriteSpecWith(app, gates, trusted: true, scenarios);

    internal string WriteUntrustedSpec(string? app, string? gates, params string[] scenarios) =>
        WriteSpecWith(app, gates, trusted: false, scenarios);

    internal void Trust()
    {
        var spec = VerificationSpecLoader.Load(SpecPath);
        Store.WriteTrust(new SpecTrust(1, Root, SpecDigests.CommandsSha256(spec), SpecDigests.Commands(spec), DateTimeOffset.UtcNow));
    }

    private string WriteSpecWith(string? app, string? gates, bool trusted, string[] scenarios)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SpecPath)!);
        var parts = new List<string> { "\"schemaVersion\": 1" };
        if (app is not null)
        {
            parts.Add($"\"app\": {app}");
        }

        if (gates is not null)
        {
            parts.Add($"\"gates\": {gates}");
        }

        parts.Add($"\"scenarios\": [ {string.Join(", ", scenarios)} ]");
        File.WriteAllText(SpecPath, $"{{ {string.Join(", ", parts)} }}");
        if (trusted)
        {
            try
            {
                Trust();
            }
            catch (VerificationSpecException)
            {
                // An invalid spec has nothing to approve; the test is about the rejection.
            }
        }

        return SpecPath;
    }

    internal void CreateAppAndMcp()
    {
        Directory.CreateDirectory(Path.Combine(Root, "bin"));
        File.WriteAllBytes(Path.Combine(Root, "bin", "App.exe"), [0]);
        Directory.CreateDirectory(Path.GetDirectoryName(McpPath)!);
        File.WriteAllBytes(McpPath, [0]);
    }

    internal JsonElement ReadVerdict()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "artifacts", "pointframe-verify", "verdict.json")));
        return document.RootElement.Clone();
    }

    internal async Task<string> WriteSealedBundleAsync(bool criterionPassed)
    {
        var bundle = Path.Combine(Root, "bundle", Guid.NewGuid().ToString("N"));
        var evidence = Path.Combine(bundle, DesktopProofBundle.EvidenceFolderName);
        Directory.CreateDirectory(evidence);
        var service = new DesktopTestReportService(signer: new Signer(_key));
        service.Initialize("session-1", "app.exe", "ABC", ["The text box shows the typed text."], evidence, bundle);
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "textEquals", criterionPassed ? DesktopVerificationStatus.Passed : DesktopVerificationStatus.Failed,
            false, "server-uia", null, DateTimeOffset.UtcNow, "C1"));
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "not textEquals", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UtcNow, NegativeControl: true));
        var report = await service.FinalizeAsync("session-1");
        await DesktopProofBundle.WriteAsync(report, bundle);
        return bundle;
    }

    internal sealed class Services
    {
        internal Mock<IMcpToolClientFactory> Factory { get; } = new();

        internal Mock<ICommandRunner> Commands { get; } = new();

        internal Mock<IWorkingTreeReader> WorkingTree { get; } = new();

        internal Mock<IConfirmation> Confirmation { get; } = new();

        internal Mock<IExaminer> Examiner { get; } = new();

        internal List<(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)> Launches { get; } = [];

        internal string LockName { get; } = $"Pointframe.VerificationTests.{Guid.NewGuid():N}";

        internal Services(FakeMcp? mcp = null, string treeHash = "TREE1")
        {
            WorkingTree.Setup(item => item.Read(It.IsAny<string>())).Returns(() => new WorkingTreeState("HEAD1", CurrentTree));
            WorkingTree.Setup(item => item.Diff(It.IsAny<string>(), It.IsAny<string>())).Returns("diff --git a/Form1.cs b/Form1.cs");
            CurrentTree = treeHash;
            CommandResolver.Setup(item => item.Resolve(It.IsAny<string?>())).Returns(new PointframeCommandInfo(null, null, false));
            Commands.Setup(item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CommandResult(0, false, ["ok"]));
            Examiner.SetupGet(item => item.Name).Returns("fake-examiner");
            if (mcp is not null)
            {
                Factory.Setup(item => item.LaunchAsync(
                        It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                    .Callback<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken>(
                        (_, arguments, environment, _) => Launches.Add((arguments, new Dictionary<string, string>(environment))))
                    .ReturnsAsync(mcp.Mock.Object);
            }
        }

        internal string CurrentTree { get; set; }

        internal Mock<IReviewer> Reviewer { get; } = new();

        internal Mock<IPointframeCommandResolver> CommandResolver { get; } = new();

        // Null: no approver agent, so unapproved commands need a person, as in a host without Claude Code.
        internal Mock<ICommandApprover>? Approver { get; set; }

        internal VerificationServices Build(
            VerificationStore store,
            string? hookInput = null,
            IMcpServerHost? serverHost = null,
            IMcpPackageInstaller? installer = null,
            Func<string?>? installedMcp = null,
            string? verifierVersion = null,
            Func<string, IReadOnlyList<int>>? runningAppProcessIds = null) => new(
            Factory.Object, installedMcp ?? (() => null), Commands.Object, WorkingTree.Object, store, Confirmation.Object, Examiner.Object, LockName, verifierVersion ?? "test-1.0",
            Reviewer.Object, hookInput is null ? null : new StringReader(hookInput), Approver?.Object, CommandResolver.Object, serverHost, installer,
            EnvironmentVariable: name => Environment.TryGetValue(name, out var value) ? value : null,
            RunningAppProcessIds: runningAppProcessIds);

        // The machine's own variables (POINTFRAME_MCP_EXECUTABLE) must not reach a test.
        internal Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal VerificationApplication Application(VerificationStore store, TextWriter output, string? hookInput = null) => new(
            Build(store, hookInput),
            output,
            TextWriter.Null);
    }

    private sealed class Signer(ECDsa key) : IDesktopProofSigner
    {
        public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);
    }
}

internal sealed class FakeMcp
{
    private static readonly object Done = new { operationStatus = "Completed", dispatch = "Complete", verification = "NotRequested", observationStatus = "NotRequested" };
    private readonly List<(string Name, JsonElement Arguments)> _calls = [];

    internal FakeMcp(string bundleDirectory)
    {
        Mock.Setup(item => item.CallToolAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<string, object, TimeSpan, CancellationToken>((name, arguments, _, _) =>
            {
                var json = JsonSerializer.SerializeToElement(arguments);
                _calls.Add((name, json));
                if (name == "desktop_start_test_session" && RawStartResult is not null)
                {
                    return Task.FromResult(RawStartResult.Value);
                }

                return Task.FromResult(Wrap(Respond(name, json, bundleDirectory)));
            });
        Mock.As<IAsyncDisposable>().Setup(item => item.DisposeAsync()).Returns(ValueTask.CompletedTask);
    }

    internal Mock<IMcpToolClient> Mock { get; } = new();

    internal object StartResponse { get; set; } = new { operationStatus = "Completed", dispatch = "Complete", sessionRef = "session-1", targetRef = "target-1" };

    internal JsonElement? RawStartResult { get; set; }

    internal static object NoWindow => new { observationRef = "obs-0", images = Array.Empty<object>(), elements = Array.Empty<object>() };

    internal object EndResponse { get; set; } = Done;

    internal Func<JsonElement, object> CheckResponse { get; set; } = _ => Passed("hello");

    internal object InvokeResponse { get; set; } = Done;

    internal string[] ElementIds { get; set; } = ["textBox", "saveButton"];

    internal string?[] ElementNames { get; set; } = [null, null];

    internal (string Role, string Name)[] NamedElementsWithoutIds { get; set; } = [];

    internal Func<int, object> RestartResponse { get; set; } = _ => Done;

    internal Func<int, object>? ObserveResponse { get; set; }

    internal static object Passed(string actual) => new { verification = "passed", stateAvailable = true, matches = true, matchCount = 1, actualValue = actual };

    internal static object Failed(string actual) => new { verification = "failed", stateAvailable = true, matches = false, matchCount = 1, actualValue = actual };

    internal void Verify(string tool, Func<JsonElement, bool> predicate) =>
        Assert.Contains(_calls, call => call.Name == tool && predicate(call.Arguments));

    internal int Count(string tool) => _calls.Count(call => call.Name == tool);

    private object Respond(string name, JsonElement arguments, string bundleDirectory) => name switch
    {
        "list_displays" => new { displays = new[] { new { boundsPixels = new { x = 0, y = 0, width = 1920, height = 1080 } } } },
        "desktop_start_test_session" => StartResponse,
        "desktop_observe_app" => Observe(Count("desktop_observe_app")),
        "desktop_invoke" => InvokeResponse,
        "desktop_check_ui" => CheckResponse(arguments),
        "desktop_get_test_report" => JsonSerializer.SerializeToElement(
            JsonSerializer.Deserialize<DesktopTestReport>(
                File.ReadAllText(Path.Combine(bundleDirectory, DesktopProofBundle.ReportFileName)), DesktopProofService.CanonicalJson)!
                with
            { SessionDirectory = bundleDirectory, EvidenceDirectory = Path.Combine(bundleDirectory, DesktopProofBundle.EvidenceFolderName) },
            DesktopProofService.CanonicalJson),
        "desktop_end_test_session" => EndResponse,
        "desktop_restart_app" => RestartResponse(Count("desktop_restart_app")),
        _ => Done,
    };

    private object Observe(int observationNumber) => ObserveResponse?.Invoke(observationNumber) ?? new
    {
        observationRef = "obs-1",
        images = new[] { new { imageRef = "img-1" } },
        elements = ElementIds.Length == 0
            ? NamedElementsWithoutIds.Select((element, index) => (object)new { elementRef = $"named-{index}", windowRef = "win-1", role = element.Role, name = element.Name }).ToArray()
            : ElementIds.Select((id, index) => (object)new { elementRef = $"el-{id}", windowRef = "win-1", role = "Edit", automationId = id, name = index < ElementNames.Length ? ElementNames[index] : null }).ToArray(),
    };

    private static JsonElement Wrap(object structured) =>
        JsonSerializer.SerializeToElement(new { content = Array.Empty<object>(), structuredContent = structured });
}
