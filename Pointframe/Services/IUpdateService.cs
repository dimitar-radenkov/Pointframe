using Pointframe.Models;

namespace Pointframe.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdates(CancellationToken cancellationToken = default);
}
