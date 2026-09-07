using Microsoft.Extensions.DependencyInjection;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp.Configuration;

namespace Pointframe.Mcp.Automation;

public static class DesktopTestingServiceRegistration
{
    public static IServiceCollection AddDesktopTestingServices(
        this IServiceCollection services,
        DesktopTestingHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        if (!options.Enabled)
        {
            return services;
        }

        services.AddSingleton<DesktopTestingPolicyLoader>();
        services.AddSingleton<IDesktopProcessController, WindowsProcessController>();
        services.AddSingleton<IDesktopTargetRegistry, DesktopTargetRegistry>();
        services.AddSingleton<IDesktopTestSessionService, DesktopTestSessionService>();
        services.AddSingleton<WindowsDesktopInputService>();
        services.AddSingleton<DesktopControlGuard>(serviceProvider =>
            new DesktopControlGuard(
                parentReleaseFallback: serviceProvider
                    .GetRequiredService<WindowsDesktopInputService>()
                    .TryReleaseOwnedInput));
        services.AddSingleton<WindowsUiAutomationProvider>();
        services.AddSingleton<IDesktopUiObservationProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsUiAutomationProvider>());
        services.AddSingleton<WorkerDesktopAutomationService>();
        services.AddSingleton<IWindowsDesktopInputService>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkerDesktopAutomationService>());
        services.AddSingleton<IWindowsUiAutomationActionProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkerDesktopAutomationService>());
        services.AddSingleton<IDesktopOcrObservationProvider, DesktopOcrObservationProvider>();
        return services;
    }
}
