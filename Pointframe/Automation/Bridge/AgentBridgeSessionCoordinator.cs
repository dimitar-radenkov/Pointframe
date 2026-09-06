using System.Windows.Input;

namespace Pointframe.Automation.Bridge;

internal enum AgentBridgeOperationStatus
{
    Idle,
    Starting,
    Annotating,
    Saving,
    Completed,
    Failed,
}

internal sealed record AgentBridgeState(
    int SchemaVersion,
    AgentBridgeOperationStatus Status,
    string? OperationId,
    string? MonitorName,
    double? DpiScaleX,
    double? DpiScaleY,
    PixelBounds? CaptureBoundsPixels,
    bool CanSave);

internal sealed record AgentBridgeActiveSession(
    string OperationId,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds CaptureBoundsPixels,
    ICommand SaveCommand);

internal interface IAgentBridgeSessionCoordinator
{
    bool TryStartCapture(string monitorName, out string? operationId);

    void RegisterActiveSession(AgentBridgeActiveSession session);

    bool TryBeginSaving(out AgentBridgeActiveSession? session);

    AgentBridgeState GetState();

    void ClearSession(string operationId);

    bool TryComplete(string operationId);

    bool TryFail(string operationId);

    bool TryComplete(string operationId, out AgentBridgeActiveSession? session);
}

internal sealed class AgentBridgeSessionCoordinator : IAgentBridgeSessionCoordinator
{
    private readonly object _sync = new();
    private AgentBridgeActiveSession? _session;
    private AgentBridgeOperationStatus _status;
    private string? _operationId;
    private string? _monitorName;

    public bool TryStartCapture(string monitorName, out string? operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorName);

        lock (_sync)
        {
            if (_status is not AgentBridgeOperationStatus.Idle)
            {
                operationId = null;
                return false;
            }

            operationId = $"cap_{Guid.NewGuid():N}";
            _operationId = operationId;
            _monitorName = monitorName;
            _status = AgentBridgeOperationStatus.Starting;
            return true;
        }
    }

    public void RegisterActiveSession(AgentBridgeActiveSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_sync)
        {
            if (_status is not AgentBridgeOperationStatus.Starting || session.OperationId != _operationId)
            {
                return;
            }

            _session = session;
            _status = AgentBridgeOperationStatus.Annotating;
        }
    }

    public bool TryBeginSaving(out AgentBridgeActiveSession? session)
    {
        lock (_sync)
        {
            if (_status is not AgentBridgeOperationStatus.Annotating
                || _session is null
                || !_session.SaveCommand.CanExecute(null))
            {
                session = null;
                return false;
            }

            session = _session;
            _status = AgentBridgeOperationStatus.Saving;
            return true;
        }
    }

    public AgentBridgeState GetState()
    {
        lock (_sync)
        {
            return new AgentBridgeState(
                SchemaVersion: 1,
                Status: _status,
                OperationId: _operationId,
                MonitorName: _session?.MonitorName ?? _monitorName,
                DpiScaleX: _session?.DpiScaleX,
                DpiScaleY: _session?.DpiScaleY,
                CaptureBoundsPixels: _session?.CaptureBoundsPixels,
                CanSave: _status is AgentBridgeOperationStatus.Annotating
                    && _session is not null
                    && _session.SaveCommand.CanExecute(null));
        }
    }

    public void ClearSession(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        lock (_sync)
        {
            if (operationId != _operationId)
            {
                return;
            }

            if (_status is AgentBridgeOperationStatus.Saving)
            {
                return;
            }

            _session = null;
            Reset();
        }
    }

    public bool TryComplete(string operationId)
    {
        return TryComplete(operationId, out _);
    }

    public bool TryFail(string operationId)
    {
        return TryFinish(operationId, AgentBridgeOperationStatus.Failed);
    }

    public bool TryComplete(string operationId, out AgentBridgeActiveSession? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        lock (_sync)
        {
            if (_status is not AgentBridgeOperationStatus.Saving || operationId != _operationId)
            {
                session = null;
                return false;
            }

            session = _session;
            _session = null;
            _status = AgentBridgeOperationStatus.Completed;
            return true;
        }
    }

    private bool TryFinish(string operationId, AgentBridgeOperationStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        lock (_sync)
        {
            if (_status is not AgentBridgeOperationStatus.Saving || operationId != _operationId)
            {
                return false;
            }

            _session = null;
            _status = status;
            return true;
        }
    }

    private void Reset()
    {
        _operationId = null;
        _monitorName = null;
        _status = AgentBridgeOperationStatus.Idle;
    }
}
