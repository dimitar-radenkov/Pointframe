using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.Services.Recording;
using Pointframe.ViewModels;

namespace Pointframe;

internal static class AppServiceRegistration
{
    internal static IServiceCollection AddPointframeAppServices(this IServiceCollection services)
    {
        var dataSourceDirectory = Path.GetDirectoryName(AppPaths.PointframeDatabasePath);
        if (!string.IsNullOrWhiteSpace(dataSourceDirectory))
        {
            Directory.CreateDirectory(dataSourceDirectory);
        }

        services.AddPointframeDataServices($"Data Source={AppPaths.PointframeDatabasePath}");

        services.AddSingleton<ITelemetryService, TelemetryService>();
        services.AddSingleton<IActivationTelemetryService, ActivationTelemetryService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IImageFileService, ImageFileService>();
        services.AddSingleton<IEventAggregator, DefaultEventAggregator>();
        services.AddSingleton<IDebounceService, DebounceService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IMouseHookService, MouseHookService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IMicrophoneDeviceService, MicrophoneDeviceService>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IAppErrorHandler, AppErrorHandler>();
        services.AddSingleton<ITrayIconManager, TrayIconManager>();
        services.AddSingleton<ICaptureLaunchService, CaptureLaunchService>();
        services.AddSingleton<ICaptureLibraryService, CaptureLibraryService>();
        services.AddSingleton<ICaptureTextLookupService, CaptureTextLookupService>();
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddTransient<IWindowCaptureService, WindowCaptureService>();
        services.AddTransient<IVideoWriterFactory, VideoWriterFactory>();
        services.AddTransient<IScreenRecordingService, ScreenRecordingService>();
        services.AddSingleton<IGifExportService, GifExportService>();
        services.AddSingleton<IVideoTrimService, VideoTrimService>();
        services.AddTransient<Func<string, TrimViewModel>>(sp => inputPath => new TrimViewModel(
            inputPath,
            sp.GetRequiredService<IVideoTrimService>(),
            sp.GetRequiredService<ITelemetryService>(),
            sp.GetRequiredService<ILogger<TrimViewModel>>()));
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddSingleton<IOcrService, WindowsOcrService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<RecordingAnnotationViewModel>();
        services.AddTransient<OverlayWindow>(CreateOverlayWindow);
        services.AddTransient<BeautifierViewModel>();
        services.AddSingleton<BeautifierRenderService>();
        services.AddSingleton<IScreenshotWatermarkService, ScreenshotWatermarkService>();
        services.AddTransient<Func<BitmapSource, BeautifierWindow>>(sp => bitmap =>
        {
            var window = new BeautifierWindow(sp.GetRequiredService<BeautifierViewModel>());
            window.Initialize(bitmap);
            return window;
        });
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<Func<IScreenRecordingService, string, RecordingHudViewModel>>(sp =>
            (screenRecordingService, outputPath) => new RecordingHudViewModel(
                screenRecordingService,
                outputPath,
                sp.GetRequiredService<IEventAggregator>(),
                sp.GetRequiredService<ILogger<RecordingHudViewModel>>()));
        services.AddTransient<AboutViewModel>();
        services.AddTransient<AboutWindow>();
        services.AddTransient<LibraryWindow>();
        services.AddTransient<UpdateDownloadViewModel>(sp =>
            new UpdateDownloadViewModel(
                UpdateDownloadViewModel.SharedHttp,
                sp.GetRequiredService<IProcessService>(),
                sp.GetService<ILogger<UpdateDownloadViewModel>>()));
        services.AddTransient<Func<UpdateDownloadViewModel>>(sp => () => sp.GetRequiredService<UpdateDownloadViewModel>());
        services.AddTransient<Func<UpdateDownloadViewModel, UpdateDownloadWindow>>(_ => vm => new UpdateDownloadWindow(vm));
        services.AddTransient<IUpdateDownloadService, UpdateDownloadWindowService>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        services.AddSingleton<AutoUpdateService>();
        services.AddSingleton<IAutoUpdateService>(sp => sp.GetRequiredService<AutoUpdateService>());
        services.AddHostedService(sp => sp.GetRequiredService<AutoUpdateService>());
        services.AddHostedService<TelemetryHeartbeatService>();

        return services;
    }

    private static OverlayWindow CreateOverlayWindow(IServiceProvider sp) => new(
        sp.GetRequiredService<OverlayViewModel>(),
        sp.GetRequiredService<IScreenCaptureService>(),
        sp.GetRequiredService<IScreenRecordingService>(),
        sp.GetRequiredService<IMouseHookService>(),
        sp.GetRequiredService<Func<IScreenRecordingService, string, RecordingHudViewModel>>(),
        sp.GetRequiredService<IEventAggregator>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IUserSettingsService>(),
        sp.GetRequiredService<IMessageBoxService>(),
        sp.GetRequiredService<IFileSystemService>(),
        sp.GetRequiredService<IOcrService>(),
        sp.GetRequiredService<ITelemetryService>(),
        sp.GetRequiredService<RecordingAnnotationViewModel>(),
        sp.GetRequiredService<Func<BitmapSource, BeautifierWindow>>());
}
