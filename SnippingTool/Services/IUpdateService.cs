using SnippingTool.Models;

namespace SnippingTool.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdates(CancellationToken cancellationToken = default);
}
