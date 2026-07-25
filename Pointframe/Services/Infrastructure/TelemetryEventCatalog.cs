namespace Pointframe.Services;

public enum TelemetryChannel
{
    Product,
    Diagnostic,
}

public static class TelemetryPropertyKeys
{
    public const string Action = "action";
    public const string AppVersion = "version";
    public const string AnnotationInputState = "annotation_input_state";
    public const string AnnotationTool = "annotation_tool";
    public const string AppSection = "app_section";
    public const string Canceled = "canceled";
    public const string CaptureType = "capture_type";
    public const string Context = "context";
    public const string DelaySeconds = "delay_seconds";
    public const string DisplayMode = "display_mode";
    public const string DurationMilliseconds = "duration_ms";
    public const string DurationSeconds = "duration_seconds";
    public const string ExceptionType = "exception_type";
    public const string FirstAction = "first_action";
    public const string LastAction = "last_action";
    public const string OsBuild = "os_build";
    public const string ScreenCount = "screen_count";
    public const string SelectionHeightPx = "selection_height_px";
    public const string SelectionWidthPx = "selection_width_px";
    public const string SessionMinutes = "session_minutes";
    public const string Source = "source";
    public const string State = "state";
    public const string Success = "success";
    public const string TimeFromInstallMinutes = "time_from_install_minutes";
    public const string Tool = "tool";
    public const string Type = "type";
    public const string UptimeMinutes = "uptime_minutes";
    public const string UrlHost = "url_host";
    public const string Version = "version";
    public const string WithAudio = "with_audio";
}

public static class TelemetryEvents
{
    public const string AnnotationCommitted = "annotation_committed";
    public const string AboutClosed = "about_closed";
    public const string AboutOpened = "about_opened";
    public const string AboutUrlOpened = "about_url_opened";
    public const string AppClosed = "app_closed";
    public const string AppHeartbeat = "app_heartbeat";
    public const string AppStarted = "app_started";
    public const string BeautifyOpened = "beautify_opened";
    public const string CaptureCompleted = "capture_completed";
    public const string CaptureDelayUsed = "capture_delay_used";
    public const string CapturePinned = "capture_pinned";
    public const string FirstCaptureCompleted = "first_capture_completed";
    public const string FirstRecordingCompleted = "first_recording_completed";
    public const string FfmpegMissing = "ffmpeg_missing";
    public const string GifExportCompleted = "gif_export_completed";
    public const string GifExportStarted = "gif_export_started";
    public const string LibraryOcrSearchUsed = "library_ocr_search_used";
    public const string LibraryOpenUsed = "library_open_used";
    public const string MicrophoneUnavailable = "microphone_unavailable";
    public const string OcrAttempted = "ocr_attempted";
    public const string OcrNoText = "ocr_no_text";
    public const string OcrUsed = "ocr_used";
    public const string OpenImageUsed = "open_image_used";
    public const string RecordingHudAnnotationInputToggled = "recording_hud_annotation_input_toggled";
    public const string RecordingHudClearAnnotations = "recording_hud_clear_annotations";
    public const string RecordingHudDisplayModeChanged = "recording_hud_display_mode_changed";
    public const string RecordingHudMicrophoneToggled = "recording_hud_microphone_toggled";
    public const string RecordingHudPauseToggled = "recording_hud_pause_toggled";
    public const string RecordingHudStopped = "recording_hud_stopped";
    public const string RecordingHudToolSelected = "recording_hud_tool_selected";
    public const string RecordingHudUndoAnnotations = "recording_hud_undo_annotations";
    public const string RecordingCompleted = "recording_completed";
    public const string RecordingStarted = "recording_started";
    public const string SettingsCanceled = "settings_canceled";
    public const string SettingsDefaultsRestored = "settings_defaults_restored";
    public const string SettingsOpened = "settings_opened";
    public const string SettingsSaved = "settings_saved";
    public const string SettingsSectionChanged = "settings_section_changed";
    public const string SettingsSectionReset = "settings_section_reset";
    public const string ScreenshotBeautified = "screenshot_beautified";
    public const string ScreenshotBeautifiedCopied = "screenshot_beautified_copied";
    public const string SnipCancelled = "snip_cancelled";
    public const string SnipStarted = "snip_started";
    public const string StartupCompleted = "startup_completed";
    public const string UnhandledException = "unhandled_exception";
    public const string UpdateAvailable = "update_available";
    public const string UpdateCheckManual = "update_check_manual";
    public const string UpdateConfirmed = "update_confirmed";
    public const string UpdateDismissed = "update_dismissed";
    public const string VideoTrimCompleted = "video_trim_completed";
    public const string VideoTrimOpened = "video_trim_opened";
    public const string VideoTrimStarted = "video_trim_started";
}

public sealed class TelemetryEventDefinition
{
    public TelemetryEventDefinition(
        string name,
        TelemetryChannel channel,
        params string[] requiredProperties)
    {
        Name = name;
        Channel = channel;
        RequiredProperties = requiredProperties;
    }

    public string Name { get; }

    public TelemetryChannel Channel { get; }

    public IReadOnlyList<string> RequiredProperties { get; }
}

public static class TelemetryEventCatalog
{
    private static readonly IReadOnlyDictionary<string, TelemetryEventDefinition> Definitions =
        new Dictionary<string, TelemetryEventDefinition>(StringComparer.Ordinal)
        {
            [TelemetryEvents.AnnotationCommitted] = Product(TelemetryEvents.AnnotationCommitted, TelemetryPropertyKeys.Tool),
            [TelemetryEvents.AboutClosed] = Product(TelemetryEvents.AboutClosed),
            [TelemetryEvents.AboutOpened] = Product(TelemetryEvents.AboutOpened),
            [TelemetryEvents.AboutUrlOpened] = Product(TelemetryEvents.AboutUrlOpened, TelemetryPropertyKeys.UrlHost),
            [TelemetryEvents.AppClosed] = Product(TelemetryEvents.AppClosed, TelemetryPropertyKeys.SessionMinutes),
            [TelemetryEvents.AppHeartbeat] = Diagnostic(TelemetryEvents.AppHeartbeat, TelemetryPropertyKeys.UptimeMinutes),
            [TelemetryEvents.AppStarted] = Product(TelemetryEvents.AppStarted, TelemetryPropertyKeys.OsBuild, TelemetryPropertyKeys.ScreenCount),
            [TelemetryEvents.BeautifyOpened] = Product(TelemetryEvents.BeautifyOpened),
            [TelemetryEvents.CaptureCompleted] = Product(TelemetryEvents.CaptureCompleted, TelemetryPropertyKeys.Action),
            [TelemetryEvents.CaptureDelayUsed] = Product(TelemetryEvents.CaptureDelayUsed, TelemetryPropertyKeys.DelaySeconds),
            [TelemetryEvents.CapturePinned] = Product(TelemetryEvents.CapturePinned),
            [TelemetryEvents.FirstCaptureCompleted] = Product(TelemetryEvents.FirstCaptureCompleted, TelemetryPropertyKeys.CaptureType, TelemetryPropertyKeys.FirstAction),
            [TelemetryEvents.FirstRecordingCompleted] = Product(TelemetryEvents.FirstRecordingCompleted, TelemetryPropertyKeys.WithAudio),
            [TelemetryEvents.FfmpegMissing] = Diagnostic(TelemetryEvents.FfmpegMissing),
            [TelemetryEvents.GifExportCompleted] = Product(TelemetryEvents.GifExportCompleted, TelemetryPropertyKeys.Success, TelemetryPropertyKeys.DurationSeconds),
            [TelemetryEvents.GifExportStarted] = Product(TelemetryEvents.GifExportStarted),
            [TelemetryEvents.LibraryOcrSearchUsed] = Product(TelemetryEvents.LibraryOcrSearchUsed),
            [TelemetryEvents.LibraryOpenUsed] = Product(TelemetryEvents.LibraryOpenUsed),
            [TelemetryEvents.MicrophoneUnavailable] = Diagnostic(TelemetryEvents.MicrophoneUnavailable),
            [TelemetryEvents.OcrAttempted] = Product(TelemetryEvents.OcrAttempted, TelemetryPropertyKeys.SelectionWidthPx, TelemetryPropertyKeys.SelectionHeightPx),
            [TelemetryEvents.OcrNoText] = Product(TelemetryEvents.OcrNoText, TelemetryPropertyKeys.SelectionWidthPx, TelemetryPropertyKeys.SelectionHeightPx),
            [TelemetryEvents.OcrUsed] = Product(TelemetryEvents.OcrUsed, TelemetryPropertyKeys.SelectionWidthPx, TelemetryPropertyKeys.SelectionHeightPx),
            [TelemetryEvents.OpenImageUsed] = Product(TelemetryEvents.OpenImageUsed),
            [TelemetryEvents.RecordingHudAnnotationInputToggled] = Product(TelemetryEvents.RecordingHudAnnotationInputToggled, TelemetryPropertyKeys.AnnotationInputState),
            [TelemetryEvents.RecordingHudClearAnnotations] = Product(TelemetryEvents.RecordingHudClearAnnotations),
            [TelemetryEvents.RecordingHudDisplayModeChanged] = Product(TelemetryEvents.RecordingHudDisplayModeChanged, TelemetryPropertyKeys.DisplayMode),
            [TelemetryEvents.RecordingHudMicrophoneToggled] = Product(TelemetryEvents.RecordingHudMicrophoneToggled, TelemetryPropertyKeys.State),
            [TelemetryEvents.RecordingHudPauseToggled] = Product(TelemetryEvents.RecordingHudPauseToggled, TelemetryPropertyKeys.State),
            [TelemetryEvents.RecordingHudStopped] = Product(TelemetryEvents.RecordingHudStopped, TelemetryPropertyKeys.DurationSeconds),
            [TelemetryEvents.RecordingHudToolSelected] = Product(TelemetryEvents.RecordingHudToolSelected, TelemetryPropertyKeys.AnnotationTool),
            [TelemetryEvents.RecordingHudUndoAnnotations] = Product(TelemetryEvents.RecordingHudUndoAnnotations),
            [TelemetryEvents.RecordingCompleted] = Product(TelemetryEvents.RecordingCompleted),
            [TelemetryEvents.RecordingStarted] = Product(TelemetryEvents.RecordingStarted, TelemetryPropertyKeys.Type),
            [TelemetryEvents.SettingsCanceled] = Product(TelemetryEvents.SettingsCanceled),
            [TelemetryEvents.SettingsDefaultsRestored] = Product(TelemetryEvents.SettingsDefaultsRestored),
            [TelemetryEvents.SettingsOpened] = Product(TelemetryEvents.SettingsOpened, TelemetryPropertyKeys.AppSection),
            [TelemetryEvents.SettingsSaved] = Product(TelemetryEvents.SettingsSaved, TelemetryPropertyKeys.AppSection),
            [TelemetryEvents.SettingsSectionChanged] = Product(TelemetryEvents.SettingsSectionChanged, TelemetryPropertyKeys.AppSection),
            [TelemetryEvents.SettingsSectionReset] = Product(TelemetryEvents.SettingsSectionReset, TelemetryPropertyKeys.AppSection),
            [TelemetryEvents.ScreenshotBeautified] = Product(TelemetryEvents.ScreenshotBeautified),
            [TelemetryEvents.ScreenshotBeautifiedCopied] = Product(TelemetryEvents.ScreenshotBeautifiedCopied),
            [TelemetryEvents.SnipCancelled] = Product(TelemetryEvents.SnipCancelled, TelemetryPropertyKeys.Type),
            [TelemetryEvents.SnipStarted] = Product(TelemetryEvents.SnipStarted, TelemetryPropertyKeys.Type, TelemetryPropertyKeys.Source),
            [TelemetryEvents.StartupCompleted] = Diagnostic(TelemetryEvents.StartupCompleted, TelemetryPropertyKeys.DurationMilliseconds),
            [TelemetryEvents.UnhandledException] = Diagnostic(TelemetryEvents.UnhandledException, TelemetryPropertyKeys.ExceptionType),
            [TelemetryEvents.UpdateAvailable] = Product(TelemetryEvents.UpdateAvailable, TelemetryPropertyKeys.Version),
            [TelemetryEvents.UpdateCheckManual] = Product(TelemetryEvents.UpdateCheckManual),
            [TelemetryEvents.UpdateConfirmed] = Product(TelemetryEvents.UpdateConfirmed, TelemetryPropertyKeys.Version),
            [TelemetryEvents.UpdateDismissed] = Product(TelemetryEvents.UpdateDismissed, TelemetryPropertyKeys.Version),
            [TelemetryEvents.VideoTrimCompleted] = Product(TelemetryEvents.VideoTrimCompleted, TelemetryPropertyKeys.Success, TelemetryPropertyKeys.Canceled),
            [TelemetryEvents.VideoTrimOpened] = Product(TelemetryEvents.VideoTrimOpened),
            [TelemetryEvents.VideoTrimStarted] = Product(TelemetryEvents.VideoTrimStarted),
        };

    public static IReadOnlyCollection<TelemetryEventDefinition> All => Definitions.Values.ToArray();

    public static bool TryGetDefinition(string eventName, out TelemetryEventDefinition definition)
    {
        return Definitions.TryGetValue(eventName, out definition!);
    }

    public static TelemetrySchemaValidationResult Validate(
        string eventName,
        IReadOnlyDictionary<string, string>? properties)
    {
        if (!TryGetDefinition(eventName, out var definition))
        {
            return TelemetrySchemaValidationResult.UnknownEvent(eventName);
        }

        if (definition.RequiredProperties.Count == 0)
        {
            return TelemetrySchemaValidationResult.Valid(definition);
        }

        if (properties is null)
        {
            return TelemetrySchemaValidationResult.MissingRequiredProperties(definition, definition.RequiredProperties);
        }

        var missing = definition.RequiredProperties
            .Where(requiredProperty => !properties.ContainsKey(requiredProperty))
            .ToArray();
        return missing.Length == 0
            ? TelemetrySchemaValidationResult.Valid(definition)
            : TelemetrySchemaValidationResult.MissingRequiredProperties(definition, missing);
    }

    private static TelemetryEventDefinition Product(string name, params string[] requiredProperties)
    {
        return new TelemetryEventDefinition(name, TelemetryChannel.Product, requiredProperties);
    }

    private static TelemetryEventDefinition Diagnostic(string name, params string[] requiredProperties)
    {
        return new TelemetryEventDefinition(name, TelemetryChannel.Diagnostic, requiredProperties);
    }
}

public sealed class TelemetrySchemaValidationResult
{
    private TelemetrySchemaValidationResult(
        bool isValid,
        bool isKnownEvent,
        TelemetryEventDefinition? definition,
        IReadOnlyList<string> missingProperties,
        string eventName)
    {
        IsValid = isValid;
        IsKnownEvent = isKnownEvent;
        Definition = definition;
        MissingProperties = missingProperties;
        EventName = eventName;
    }

    public bool IsValid { get; }

    public bool IsKnownEvent { get; }

    public TelemetryEventDefinition? Definition { get; }

    public IReadOnlyList<string> MissingProperties { get; }

    public string EventName { get; }

    public static TelemetrySchemaValidationResult Valid(TelemetryEventDefinition definition)
    {
        return new TelemetrySchemaValidationResult(true, true, definition, Array.Empty<string>(), definition.Name);
    }

    public static TelemetrySchemaValidationResult UnknownEvent(string eventName)
    {
        return new TelemetrySchemaValidationResult(false, false, null, Array.Empty<string>(), eventName);
    }

    public static TelemetrySchemaValidationResult MissingRequiredProperties(
        TelemetryEventDefinition definition,
        IReadOnlyList<string> missingProperties)
    {
        return new TelemetrySchemaValidationResult(false, true, definition, missingProperties, definition.Name);
    }
}
