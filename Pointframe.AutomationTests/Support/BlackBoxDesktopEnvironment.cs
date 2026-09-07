using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.AutomationTests.Support;

public sealed class BlackBoxDesktopEnvironment
{
    private readonly Pointframe.Engine.Automation.Services.BlackBoxDesktopEnvironment _environment;

    public BlackBoxDesktopEnvironment(IBlackBoxDesktopEnvironmentAdapter adapter)
    {
        _environment = new Pointframe.Engine.Automation.Services.BlackBoxDesktopEnvironment(adapter);
    }

    public BlackBoxEnvironmentValidationResult ValidatePrerequisites(BlackBoxDesktopEnvironmentOptions options)
    {
        return _environment.ValidatePrerequisites(options);
    }

    public BlackBoxDesktopEnvironmentSnapshot CaptureEnvironment(BlackBoxDesktopEnvironmentOptions options)
    {
        return _environment.CaptureEnvironment(options);
    }
}
