using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IDisplayCaptureEngine, DisplayCaptureEngine>();
builder.Services.AddSingleton<IOcrEngineService, WindowsOcrEngineService>();
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

await builder.Build().RunAsync();
return 0;

internal sealed class UnavailableDesktopUiCheckSource : IDesktopUiCheckSource
{
    public Task<DesktopUiCheckEvaluation> EvaluateAsync(DesktopUiCheckCondition condition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DesktopUiCheckEvaluation(false, false, 0, "ProviderUnavailable"));
    }
}
