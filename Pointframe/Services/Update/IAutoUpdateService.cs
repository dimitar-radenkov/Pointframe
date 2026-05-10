using Microsoft.Extensions.Hosting;

namespace Pointframe.Services;

public interface IAutoUpdateService : IHostedService
{
    Task ConfirmAndInstall(UpdateCheckResult result);
}
