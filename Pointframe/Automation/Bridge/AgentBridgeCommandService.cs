using Pointframe.Services;
using Pointframe.Services.Messaging;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Pointframe.Automation.Bridge;

internal interface IAgentBridgeDispatcher
{
    Task InvokeAsync(Action action);
}

internal sealed class AgentBridgeDispatcher : IAgentBridgeDispatcher
{
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return WpfApplication.Current.Dispatcher.InvokeAsync(action).Task;
    }
}

internal interface IAgentBridgeCommandService
{
    Task<IReadOnlyList<DisplayDescriptor>> ListDisplaysAsync(CancellationToken cancellationToken = default);

    Task<AgentBridgeState> CaptureMonitorAsync(string monitorName, CancellationToken cancellationToken = default);

    Task<ArtifactDescriptor> SaveOverlayAsync(CancellationToken cancellationToken = default);

    AgentBridgeState GetState();

    ValueTask HandleCaptureCompletedAsync(CaptureCompletedMessage message, CancellationToken cancellationToken = default);
}

internal sealed class AgentBridgeCommandService : IAgentBridgeCommandService
{
    private readonly IAgentBridgeDispatcher _dispatcher;
    private readonly ICaptureLaunchService _captureLaunchService;
    private readonly IAgentBridgeSessionCoordinator _sessionCoordinator;
    private readonly IArtifactMetadataService _artifactMetadataService;
    private readonly ILogger<AgentBridgeCommandService> _logger;
    private readonly object _sync = new();
    private TaskCompletionSource<ArtifactDescriptor>? _saveCompletion;

    public AgentBridgeCommandService(
        IAgentBridgeDispatcher dispatcher,
        ICaptureLaunchService captureLaunchService,
        IAgentBridgeSessionCoordinator sessionCoordinator,
        IArtifactMetadataService artifactMetadataService,
        ILogger<AgentBridgeCommandService> logger)
    {
        _dispatcher = dispatcher;
        _captureLaunchService = captureLaunchService;
        _sessionCoordinator = sessionCoordinator;
        _artifactMetadataService = artifactMetadataService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DisplayDescriptor>> ListDisplaysAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Agent bridge listing displays on the WPF dispatcher");
        IReadOnlyList<DisplayDescriptor>? displays = null;
        await _dispatcher.InvokeAsync(() =>
        {
            displays = Forms.Screen.AllScreens.Select(screen =>
            {
                var scale = MonitorDpiHelper.GetMonitorScale(screen.Bounds.Location);
                return new DisplayDescriptor(
                    SchemaVersion: 1,
                    MonitorName: screen.DeviceName,
                    DpiScaleX: scale,
                    DpiScaleY: scale,
                    BoundsPixels: new PixelBounds(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height));
            }).ToArray();
        });
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("Agent bridge listed {DisplayCount} displays", displays?.Count ?? 0);

        return displays ?? [];
    }

    public async Task<AgentBridgeState> CaptureMonitorAsync(string monitorName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorName);
        _logger.LogDebug("Agent bridge starting monitor capture for {MonitorName}", monitorName);
        if (!_sessionCoordinator.TryStartCapture(monitorName, out var operationId) || operationId is null)
        {
            throw new InvalidOperationException("An agent capture operation is already active.");
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_captureLaunchService.StartMonitorSnip(monitorName, agentOperationId: operationId))
                {
                    throw new ArgumentException($"Unknown monitor '{monitorName}'.", nameof(monitorName));
                }
            });
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Agent bridge started capture operation {OperationId}", operationId);
            return _sessionCoordinator.GetState();
        }
        catch
        {
            _sessionCoordinator.TryFail(operationId);
            throw;
        }
    }

    public async Task<ArtifactDescriptor> SaveOverlayAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Agent bridge saving the active overlay");
        var completion = new TaskCompletionSource<ArtifactDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentBridgeActiveSession? session = null;

        await _dispatcher.InvokeAsync(() =>
        {
            if (!_sessionCoordinator.TryBeginSaving(out session) || session is null)
            {
                throw new InvalidOperationException("No saveable agent capture session is active.");
            }

            lock (_sync)
            {
                _saveCompletion = completion;
            }

            session.SaveCommand.Execute(null);
        });

        return await completion.Task.WaitAsync(cancellationToken);
    }

    public AgentBridgeState GetState()
    {
        return _sessionCoordinator.GetState();
    }

    public async ValueTask HandleCaptureCompletedAsync(CaptureCompletedMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Agent bridge observed capture completion with action {CaptureAction}", message.CaptureAction);
        if (string.IsNullOrWhiteSpace(message.OutputPath) || message.CaptureAction != "save")
        {
            return;
        }

        var state = _sessionCoordinator.GetState();
        if (state.Status is not AgentBridgeOperationStatus.Saving || state.OperationId is null)
        {
            return;
        }

        if (!_sessionCoordinator.TryComplete(state.OperationId, out var session) || session is null)
        {
            return;
        }

        try
        {
            var metadata = await _artifactMetadataService.WriteImageMetadataAsync(new ImageArtifactMetadataRequest(
                message.OutputPath,
                Source: "agent",
                session.MonitorName,
                session.DpiScaleX,
                session.DpiScaleY,
                session.CaptureBoundsPixels), cancellationToken);
            _logger.LogDebug("Agent bridge wrote metadata for capture operation {OperationId}", session.OperationId);
            CompleteSave(new ArtifactDescriptor(1, session.OperationId, metadata));
        }
        catch (Exception exception)
        {
            FailSave(exception);
        }
    }

    private void CompleteSave(ArtifactDescriptor artifact)
    {
        lock (_sync)
        {
            _saveCompletion?.TrySetResult(artifact);
            _saveCompletion = null;
        }
    }

    private void FailSave(Exception exception)
    {
        lock (_sync)
        {
            _saveCompletion?.TrySetException(exception);
            _saveCompletion = null;
        }
    }
}
