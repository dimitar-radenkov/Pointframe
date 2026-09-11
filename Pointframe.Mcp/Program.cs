using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pointframe.Data;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;

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
    builder.Services.AddSingleton<IDesktopUiCheckService>(serviceProvider =>
        new DesktopUiCheckService(new UnavailableDesktopUiCheckSource()));
    builder.Services.AddSingleton<IDesktopActionLedger, DesktopActionLedger>();
    builder.Services.AddSingleton<IDesktopTestReportService, DesktopTestReportService>();
    builder.Services.AddSingleton<IDesktopActionCoordinator, DesktopActionCoordinator>();
}
var mcpServer = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
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
return 0;

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

internal sealed class UnavailableDesktopUiCheckSource : IDesktopUiCheckSource
{
    public Task<DesktopUiCheckEvaluation> EvaluateAsync(DesktopUiCheckCondition condition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DesktopUiCheckEvaluation(false, false, 0, "ProviderUnavailable"));
    }
}
