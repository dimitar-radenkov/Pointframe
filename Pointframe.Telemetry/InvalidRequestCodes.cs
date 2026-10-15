namespace Pointframe.Telemetry;

// Typed error codes that mean the caller's input was wrong, as opposed to the environment or Pointframe failing.
// Both hosts classify from these codes, never from message text or an exit code alone.
public static class InvalidRequestCodes
{
    private static readonly HashSet<string> _codes = new(StringComparer.Ordinal)
    {
        "invalid_query",
        "invalid_cursor",
        "invalid_monitor_name",
        "invalid_text_range",
        "invalid_redaction_region",
        "invalid_frames_per_second",
        "invalid_output_path",
        "invalid_output_directory",
        "invalid_region",
        "redaction_regions_required",
        "InvalidCriteria",
        "InvalidDetail",
        "InvalidImageOptions",
        "InvalidScenarioId",
        "InvalidTextLength",
        "InvalidScrollDetents",
        "InvalidKeyCount",
        "InvalidDragPoints",
        "InvalidDragDuration",
        "InvalidClickCount",
        "ElementRequired",
        "TargetRequired",
    };

    public static bool Contains(string? code) => code is not null && _codes.Contains(code);
}
