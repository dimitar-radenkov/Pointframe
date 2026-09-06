using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Pointframe.Mcp;
using Pointframe.Engine;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IDisplayCaptureEngine, DisplayCaptureEngine>();
builder.Services.AddSingleton<IDirectCaptureService, DirectCaptureService>();
builder.Services.AddSingleton<IDirectVideoWriterFactory, FfmpegDirectVideoWriterFactory>();
builder.Services.AddSingleton<IDirectRecordingService, DirectRecordingService>();
builder.Services.AddSingleton<IDirectRecordingMcpService, DirectRecordingMcpService>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();