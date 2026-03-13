using Microsoft.Extensions.Hosting;
using SnippingTool.Models;

namespace SnippingTool.Services;

public interface IAutoUpdateService : IHostedService
{
    event Action<UpdateCheckResult>? UpdateAvailable;
    Task ConfirmAndInstallAsync(UpdateCheckResult result);
}
