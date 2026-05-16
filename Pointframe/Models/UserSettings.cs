namespace Pointframe.Models;

public sealed class UserSettings
{
    public string ScreenshotSavePath { get; set; } =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe", "Screenshots");

    public bool AutoSaveScreenshots { get; set; } = false;

    public string RecordingOutputPath { get; set; } =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe", "Videos");

    public bool RecordMicrophone { get; set; } = true;
    public string? RecordingMicrophoneDeviceName { get; set; }
    public int RecordingFps { get; set; } = 20;
    public int GifFps { get; set; } = 10;
    public int HudGapPixels { get; set; } = 8;
    public bool RecordingCursorHighlightEnabled { get; set; } = true;
    public bool RecordingClickRippleEnabled { get; set; } = true;
    public double RecordingCursorHighlightSize { get; set; } = 28d;

    public string DefaultAnnotationColor { get; set; } = "#FFFF0000";
    public double DefaultStrokeThickness { get; set; } = 2.5;
    public int CaptureDelaySeconds { get; set; } = 0;

    public uint RegionCaptureHotkey { get; set; } = 0x2C; // VK_SNAPSHOT (Print Screen)

    public HotkeyModifiers RegionCaptureHotkeyModifiers { get; set; } = HotkeyModifiers.None;

    public uint WholeScreenRecordHotkey { get; set; } = 0x52; // VK_R

    public HotkeyModifiers WholeScreenRecordHotkeyModifiers { get; set; } = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift; // Ctrl+Shift+R

    public UpdateCheckInterval AutoUpdateCheckInterval { get; set; } = UpdateCheckInterval.EveryTwoHours;
    public DateTime? LastAutoUpdateCheckUtc { get; set; } = null;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public List<AnnotationStylePreset> StylePresets { get; set; } =
    [
        new() { Name = "Red",   Color = "#FFFF0000", StrokeThickness = 2.5 },
        new() { Name = "Blue",  Color = "#FF1E90FF", StrokeThickness = 2.5 },
        new() { Name = "Black", Color = "#FF1A1A1A", StrokeThickness = 3.5 },
    ];

    /// <summary>
    /// Anonymous install identifier generated once on first run.
    /// Used for telemetry to count unique installs without tracking identity.
    /// </summary>
    public string? InstallId { get; set; }

    public DateTime? InstallCreatedUtc { get; set; }

    public bool FirstCaptureCompletedTracked { get; set; }

    public bool FirstRecordingCompletedTracked { get; set; }
}
