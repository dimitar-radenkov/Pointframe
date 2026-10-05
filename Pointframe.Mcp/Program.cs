using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pointframe.Data;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Pointframe.Telemetry;

var hostOptions = DesktopTestingHostOptions.Parse(args);
if (hostOptions.WorkerMode)
{
    await DesktopAutomationWorkerHost.RunWorkerAsync(
        hostOptions.WorkerPipeName!,
        new DesktopAutomationWorkerProvider(),
        hostOptions.ParentProcessId,
        CancellationToken.None);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
Directory.CreateDirectory(PointframePaths.LocalAppDataDirectory);

// Standard output carries the JSON-RPC protocol, so the first-run telemetry notice goes to standard
// error and, when it is shown, into the server instructions the client hands to the model.
string? telemetryNotice = null;
var telemetry = OperationTelemetryFactory.Create(
    TelemetryHost.Mcp,
    OperationTelemetryFactory.NormalizeVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion),
    PointframePaths.LocalAppDataDirectory,
    notice =>
    {
        telemetryNotice = notice;
        Console.Error.WriteLine(notice);
    });
builder.Services.AddPointframeDataServices($"Data Source={PointframePaths.PointframeDatabasePath}");
builder.Services.AddSingleton(TimeProvider.System);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IDisplayCaptureEngine, DisplayCaptureEngine>();
builder.Services.AddSingleton<IOcrEngineService, WindowsOcrEngineService>();
builder.Services.AddSingleton<ICaptureCatalogService, CaptureCatalogService>();
builder.Services.AddSingleton<ICaptureLibrarySources, StandaloneCaptureLibrarySources>();
builder.Services.AddSingleton<ICaptureImportService, CaptureImportService>();
builder.Services.AddSingleton<ICaptureRegistrationService, CaptureRegistrationService>();
builder.Services.AddSingleton<ICaptureIndexWorker, CaptureIndexWorker>();
builder.Services.AddSingleton<IDirectCaptureService, DirectCaptureService>();
builder.Services.AddSingleton<IDirectVideoWriterFactory, FfmpegDirectVideoWriterFactory>();
builder.Services.AddSingleton<IDirectRecordingService, DirectRecordingService>();
builder.Services.AddSingleton<IDirectRecordingMcpService, DirectRecordingMcpService>();
builder.Services.AddDesktopTestingServices(hostOptions);
if (hostOptions.Enabled)
{
    builder.Services.AddSingleton<IDesktopObservationStore, DesktopObservationStore>();
    builder.Services.AddSingleton<IDesktopObservationService, DesktopObservationService>();
    // Checks read the session target's live element tree through the worker. This used to be wired to
    // a stub that reported every check inconclusive, which left every action tool's success
    // unverifiable.
    builder.Services.AddSingleton<IDesktopUiCheckSource>(serviceProvider =>
        new WorkerUiCheckSource(
            serviceProvider.GetRequiredService<IDesktopUiObservationProvider>(),
            serviceProvider.GetRequiredService<IDesktopProcessController>()));
    builder.Services.AddSingleton<IDesktopUiCheckService>(serviceProvider =>
        new DesktopUiCheckService(serviceProvider.GetRequiredService<IDesktopUiCheckSource>()));
    builder.Services.AddSingleton<IDesktopActionLedger, DesktopActionLedger>();
    builder.Services.AddSingleton<IDesktopProofSigner, CngDesktopProofSigner>();
    builder.Services.AddSingleton<IDesktopTestReportService>(serviceProvider =>
        new DesktopTestReportService(
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<IDesktopProofSigner>()));
    builder.Services.AddSingleton<IDesktopEvidenceRecorder>(serviceProvider =>
        new DesktopEvidenceRecorder(
            new WindowContentCapture(),
            new WindowDiscoveryService(),
            serviceProvider.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IDesktopActionCoordinator, DesktopActionCoordinator>();
}
var mcpServer = builder.Services
    .AddMcpServer(options => options.ServerInstructions = ServerInstructions(hostOptions.Enabled, telemetryNotice))
    .WithStdioServerTransport()
    .WithRequestFilters(filters => filters.AddCallToolFilter(McpTelemetryFilter.Create(telemetry)))
    .WithTools<PointframeMcpTools>()
    .WithResources<PointframeMcpResources>();
if (hostOptions.Enabled)
{
    mcpServer.WithTools<DesktopTestingMcpTools>();
}

var host = builder.Build();
_ = Task.Run(async () =>
{
    try
    {
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var reconciliation = ReconcileUntilStoppedAsync(
            host.Services.GetRequiredService<ICaptureImportService>(),
            host.Services.GetRequiredService<ICaptureRegistrationService>(),
            stopping);
        var indexing = host.Services.GetRequiredService<ICaptureIndexWorker>().RunUntilCancelledAsync(stopping);
        await Task.WhenAll(reconciliation, indexing);
    }
    catch (OperationCanceledException)
    {
        // Host shutdown cancels maintenance work.
    }
    catch (Exception exception)
    {
        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CaptureImport")
            .LogWarning(exception, "Capture library reconciliation could not start");
    }
});
await host.RunAsync();
telemetry.Flush();
return 0;

// Clients show these instructions to the agent at connect time, which is the one moment an agent that has
// never seen Pointframe learns that it can verify its own desktop work here, and where the steps are.
static string ServerInstructions(bool desktopTestingEnabled, string? telemetryNotice) =>
    "Pointframe captures, reads (OCR), and records the Windows desktop. " +
    (telemetryNotice is null ? string.Empty : telemetryNotice + " ") +
    (desktopTestingEnabled
        ? "Desktop testing is enabled: to verify a change in a running desktop app and return a signed proof, read the resource " +
          $"{PointframeMcpResources.VerifyDesktopWorkGuideUri} before calling any desktop_ tool."
        : "Desktop testing tools are disabled on this server. To learn how an agent verifies its desktop work once they are enabled, read the resource " +
          $"{PointframeMcpResources.VerifyDesktopWorkGuideUri}.");

static async Task ReconcileUntilStoppedAsync(
    ICaptureImportService importer,
    ICaptureRegistrationService registration,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await registration.ReplayPendingAsync(cancellationToken);
        await importer.RequestReconciliationAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }
}
