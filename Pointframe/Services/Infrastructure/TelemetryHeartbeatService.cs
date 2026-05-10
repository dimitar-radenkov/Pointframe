using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pointframe.Automation;

namespace Pointframe.Services;

public sealed class TelemetryHeartbeatService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(30);

    private readonly ITelemetryService _telemetry;
    private readonly ILogger<TelemetryHeartbeatService> _logger;
    private readonly bool _isAutomationMode;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public TelemetryHeartbeatService(
        ITelemetryService telemetry,
        ILogger<TelemetryHeartbeatService> logger)
    {
        _telemetry = telemetry;
        _logger = logger;

        var launchOptions = AutomationLaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1));
        _isAutomationMode = launchOptions.IsAutomationMode;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_isAutomationMode)
        {
            _logger.LogDebug("Telemetry heartbeat skipped in automation mode");
            return;
        }

        _logger.LogInformation("Telemetry heartbeat started with interval {Interval}", HeartbeatInterval);

        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var uptimeMinutes = ((int)(DateTime.UtcNow - _startedAtUtc).TotalMinutes).ToString();
                _telemetry.TrackEvent("app_heartbeat", new Dictionary<string, string>
                {
                    ["uptime_minutes"] = uptimeMinutes,
                });
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Telemetry heartbeat cancelled");
        }
    }
}
