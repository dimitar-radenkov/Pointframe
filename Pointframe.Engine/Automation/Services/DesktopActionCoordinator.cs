using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public sealed record DesktopActionExecution(
    DesktopDispatchStatus Dispatch,
    DesktopVerificationStatus Verification = DesktopVerificationStatus.NotRequested,
    DesktopObservationStatus ObservationStatus = DesktopObservationStatus.NotRequested,
    DesktopOperationError? Error = null);

public interface IDesktopActionCoordinator
{
    Task<DesktopActionResult> ExecuteAsync(
        string sessionRef,
        string actionId,
        object? canonicalArguments,
        Func<CancellationToken, Task<DesktopActionExecution>> dispatch,
        IReadOnlyList<string>? observationsToInvalidate = null,
        CancellationToken cancellationToken = default);

    DesktopActionResult GetResult(string actionId);

    void RecordCheck(string sessionRef, DesktopTestCheckReport check);

    Task<DesktopTestReport> FinalizeAsync(string sessionRef, CancellationToken cancellationToken = default);
}

public sealed class DesktopActionCoordinator : IDesktopActionCoordinator
{
    private readonly IDesktopActionLedger _ledger;
    private readonly IDesktopTestReportService _reports;
    private readonly IDesktopObservationStore? _observations;
    private readonly SemaphoreSlim _queue = new(DesktopTestingLimits.MaxQueueCapacity, DesktopTestingLimits.MaxQueueCapacity);
    private readonly Dictionary<string, DesktopActionResult> _results = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public DesktopActionCoordinator(
        IDesktopActionLedger ledger,
        IDesktopTestReportService reports,
        IDesktopObservationStore? observations = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _observations = observations;
    }

    public async Task<DesktopActionResult> ExecuteAsync(
        string sessionRef,
        string actionId,
        object? canonicalArguments,
        Func<CancellationToken, Task<DesktopActionExecution>> dispatch,
        IReadOnlyList<string>? observationsToInvalidate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentNullException.ThrowIfNull(dispatch);
        var canonical = DesktopActionLedger.Canonicalize(canonicalArguments);

        if (_ledger.TryGet(actionId, out var existing))
        {
            if (existing!.ArgumentsHash != Hash(canonical))
            {
                return Store(sessionRef, actionId, new DesktopActionResult(
                    DesktopTestingLimits.SchemaVersion,
                    actionId,
                    DesktopOperationStatus.Completed,
                    DesktopDispatchStatus.NotStarted,
                    DesktopVerificationStatus.Failed,
                    DesktopObservationStatus.NotRequested,
                    new DesktopOperationError("ActionIdConflict", "The action ID was already used with different arguments.")));
            }

            return existing.Result;
        }

        if (!await _queue.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return Store(sessionRef, actionId, Failure(actionId, "QueueFull", "The desktop action queue is full."));
        }

        try
        {
            var running = new DesktopActionResult(
                DesktopTestingLimits.SchemaVersion,
                actionId,
                DesktopOperationStatus.Running,
                DesktopDispatchStatus.NotStarted,
                DesktopVerificationStatus.NotRequested,
                DesktopObservationStatus.NotRequested);
            _ledger.Record(actionId, canonical, running);
            await _ledger.FlushAsync(cancellationToken).ConfigureAwait(false);
            DesktopActionResult result;
            try
            {
                var execution = await dispatch(cancellationToken).ConfigureAwait(false);
                result = new DesktopActionResult(
                    DesktopTestingLimits.SchemaVersion,
                    actionId,
                    DesktopOperationStatus.Completed,
                    execution.Dispatch,
                    execution.Verification,
                    execution.ObservationStatus,
                    execution.Error);
            }
            catch (OperationCanceledException)
            {
                result = Failure(actionId, "DispatchTimeout", "The action may have been delivered; it was not replayed.", DesktopDispatchStatus.Unknown);
            }
            catch (Exception exception)
            {
                result = Failure(actionId, "DispatchFailed", exception.Message, DesktopDispatchStatus.Partial);
            }

            if (observationsToInvalidate is not null && result.Dispatch != DesktopDispatchStatus.NotStarted)
            {
                foreach (var observation in observationsToInvalidate)
                {
                    _observations?.Invalidate(observation);
                }
            }

            return Store(sessionRef, actionId, result, canonical);
        }
        finally
        {
            _queue.Release();
        }
    }

    public DesktopActionResult GetResult(string actionId)
    {
        lock (_sync)
        {
            return _results.TryGetValue(actionId, out var result)
                ? result
                : throw new KeyNotFoundException($"Action '{actionId}' was not found.");
        }
    }

    public void RecordCheck(string sessionRef, DesktopTestCheckReport check) => _reports.RecordCheck(sessionRef, check);

    public Task<DesktopTestReport> FinalizeAsync(string sessionRef, CancellationToken cancellationToken = default) =>
        _reports.FinalizeAsync(sessionRef, cancellationToken);

    private DesktopActionResult Store(string sessionRef, string actionId, DesktopActionResult result, string? canonical = null)
    {
        lock (_sync)
        {
            _results[actionId] = result;
        }

        if (canonical is not null)
        {
            _ledger.Record(actionId, canonical, result);
        }
        _reports.RecordAction(sessionRef, new DesktopTestActionReport(actionId, "desktop action", result, DateTimeOffset.UtcNow));
        return result;
    }

    private static DesktopActionResult Failure(string actionId, string code, string message, DesktopDispatchStatus dispatch = DesktopDispatchStatus.NotStarted) =>
        new(
            DesktopTestingLimits.SchemaVersion,
            actionId,
            DesktopOperationStatus.Completed,
            dispatch,
            DesktopVerificationStatus.Inconclusive,
            DesktopObservationStatus.NotRequested,
            new DesktopOperationError(code, message));

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
