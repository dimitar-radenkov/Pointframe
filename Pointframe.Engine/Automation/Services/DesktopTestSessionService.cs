using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopProcessController
{
    Task<DesktopProcessIdentity> LaunchAsync(
        DesktopLaunchRequest request,
        CancellationToken cancellationToken = default);

    Task<DesktopTargetState> GetStateAsync(
        DesktopProcessIdentity process,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        DesktopProcessIdentity process,
        CancellationToken cancellationToken = default);
}

public interface IDesktopTestSessionService
{
    Task<DesktopSessionOperationResult> StartAsync(
        string sessionRef,
        string profileId,
        DesktopLaunchRequest launchRequest,
        CancellationToken cancellationToken = default);

    Task<DesktopSessionOperationResult> RestartAsync(
        string sessionRef,
        DesktopLaunchRequest launchRequest,
        CancellationToken cancellationToken = default);

    Task<DesktopSessionOperationResult> EndAsync(
        string sessionRef,
        CancellationToken cancellationToken = default);

    Task<DesktopTestSessionSnapshot?> GetAsync(
        string sessionRef,
        CancellationToken cancellationToken = default);

    DesktopTestSessionSnapshot? GetSnapshot(string sessionRef);
}

public sealed class DesktopTestSessionService : IDesktopTestSessionService
{
    private readonly IDesktopProcessController _processController;
    private readonly IDesktopTargetRegistry _targetRegistry;
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopTestSessionService(
        IDesktopProcessController processController,
        IDesktopTargetRegistry? targetRegistry = null)
    {
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _targetRegistry = targetRegistry ?? new DesktopTargetRegistry();
    }

    public async Task<DesktopSessionOperationResult> StartAsync(
        string sessionRef,
        string profileId,
        DesktopLaunchRequest launchRequest,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(sessionRef, nameof(sessionRef));
        ValidateReference(profileId, nameof(profileId));
        ArgumentNullException.ThrowIfNull(launchRequest);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.ContainsKey(sessionRef))
            {
                return Failure("SessionAlreadyExists", "The session reference is already in use.");
            }

            var process = await _processController.LaunchAsync(launchRequest, cancellationToken).ConfigureAwait(false);
            var target = CreateTarget(profileId, process, generation: 1);
            _targetRegistry.Add(target);
            var state = new SessionState(sessionRef, profileId, target);
            _sessions.Add(sessionRef, state);
            return Success(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesktopSessionOperationResult> RestartAsync(
        string sessionRef,
        DesktopLaunchRequest launchRequest,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(sessionRef, nameof(sessionRef));
        ArgumentNullException.ThrowIfNull(launchRequest);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(sessionRef, out var state))
            {
                return Failure("SessionNotFound", "The session reference was not found.");
            }

            var currentState = await _processController
                .GetStateAsync(state.Target.Process, cancellationToken)
                .ConfigureAwait(false);

            state.Target = state.Target with { State = currentState };
            _targetRegistry.Update(state.Target);
            if (currentState == DesktopTargetState.Running)
            {
                return Failure("TargetStillRunning", "The previous target must exit normally before restart.", state);
            }

            if (currentState == DesktopTargetState.Unavailable)
            {
                return Failure("TargetUnavailable", "The previous target identity could not be verified.", state);
            }

            await _processController.ReleaseAsync(state.Target.Process, cancellationToken).ConfigureAwait(false);
            _targetRegistry.Remove(state.Target.TargetRef);
            var process = await _processController.LaunchAsync(launchRequest, cancellationToken).ConfigureAwait(false);
            state.Target = CreateTarget(state.ProfileId, process, state.Target.Generation + 1);
            _targetRegistry.Add(state.Target);
            state.State = DesktopSessionState.Active;
            return Success(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesktopSessionOperationResult> EndAsync(
        string sessionRef,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(sessionRef, nameof(sessionRef));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(sessionRef, out var state))
            {
                return Failure("SessionNotFound", "The session reference was not found.");
            }

            var currentState = await _processController
                .GetStateAsync(state.Target.Process, cancellationToken)
                .ConfigureAwait(false);
            state.Target = state.Target with { State = currentState };
            _targetRegistry.Update(state.Target);
            await _processController.ReleaseAsync(state.Target.Process, cancellationToken).ConfigureAwait(false);
            _targetRegistry.Remove(state.Target.TargetRef);
            state.State = DesktopSessionState.Closed;

            return currentState == DesktopTargetState.Running
                ? Failure("CleanupIncomplete", "The launched target is still running; it was not terminated.", state)
                : Success(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesktopTestSessionSnapshot?> GetAsync(
        string sessionRef,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(sessionRef, nameof(sessionRef));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _sessions.TryGetValue(sessionRef, out var state)
                ? state.ToSnapshot()
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DesktopTestSessionSnapshot? GetSnapshot(string sessionRef)
    {
        ValidateReference(sessionRef, nameof(sessionRef));
        return _sessions.TryGetValue(sessionRef, out var state) ? state.ToSnapshot() : null;
    }

    private static DesktopTargetReference CreateTarget(
        string profileId,
        DesktopProcessIdentity process,
        int generation)
    {
        return new DesktopTargetReference(
            $"target-{Guid.NewGuid():N}",
            profileId,
            generation,
            process,
            DesktopTargetState.Running,
            true);
    }

    private static DesktopSessionOperationResult Success(SessionState state)
    {
        return new DesktopSessionOperationResult(true, "Ok", "The operation completed.", state.ToSnapshot());
    }

    private static DesktopSessionOperationResult Failure(
        string code,
        string message,
        SessionState? state = null)
    {
        return new DesktopSessionOperationResult(false, code, message, state?.ToSnapshot());
    }

    private static void ValidateReference(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty reference is required.", parameterName);
        }
    }

    private sealed class SessionState(
        string sessionRef,
        string profileId,
        DesktopTargetReference target)
    {
        public string SessionRef { get; } = sessionRef;

        public string ProfileId { get; } = profileId;

        public DesktopSessionState State { get; set; } = DesktopSessionState.Active;

        public DesktopTargetReference Target { get; set; } = target;

        public DesktopTestSessionSnapshot ToSnapshot()
        {
            return new DesktopTestSessionSnapshot(SessionRef, ProfileId, State, Target);
        }
    }
}
