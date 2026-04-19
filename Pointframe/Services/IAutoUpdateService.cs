using Microsoft.Extensions.Hosting;
using Pointframe.Models;

namespace Pointframe.Services;

public interface IAutoUpdateService : IHostedService
{
    Task ConfirmAndInstall(UpdateCheckResult result);
}
