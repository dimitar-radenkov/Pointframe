using Microsoft.Extensions.Hosting;
using SnippingTool.Models;

namespace SnippingTool.Services;

public interface IAutoUpdateService : IHostedService
{
    Task ConfirmAndInstall(UpdateCheckResult result);
}
