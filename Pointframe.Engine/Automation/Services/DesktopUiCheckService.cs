using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopUiCheckSource
{
    Task<DesktopUiCheckEvaluation> EvaluateAsync(DesktopUiCheckCondition condition, CancellationToken cancellationToken);
}

public interface IDesktopUiCheckService
{
    Task<DesktopUiCheckEvaluation> CheckAsync(DesktopUiCheckCondition condition, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class DesktopUiCheckService(IDesktopUiCheckSource source, TimeProvider? timeProvider = null) : IDesktopUiCheckService
{
    private readonly IDesktopUiCheckSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<DesktopUiCheckEvaluation> CheckAsync(
        DesktopUiCheckCondition condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(DesktopTestingLimits.MaxUiCheckTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = _timeProvider.GetUtcNow().Add(timeout);
        DesktopUiCheckEvaluation? last = null;
        while (_timeProvider.GetUtcNow() <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _source.EvaluateAsync(condition, cancellationToken).ConfigureAwait(false);
            if (!last.StateAvailable)
            {
                return last with { Matches = false, ErrorCode = last.ErrorCode ?? "ProviderUnavailable" };
            }

            var validCardinality = condition is DesktopUiCheckCondition.Exists or DesktopUiCheckCondition.Absent
                ? true
                : last.MatchCount == 1;
            if (!validCardinality && last.MatchCount > 1)
            {
                return last with
                {
                    Matches = false,
                    ErrorCode = "AmbiguousTarget",
                    Message = "The condition matched more than one element.",
                };
            }

            if (validCardinality && last.Matches)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(DesktopTestingLimits.UiCheckPollingMilliseconds), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        return last ?? new DesktopUiCheckEvaluation(false, false, 0, "ProviderUnavailable");
    }
}
