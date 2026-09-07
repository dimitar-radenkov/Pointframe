namespace Pointframe.Mcp;

public static class PointframeCommandCatalog
{
    public static readonly IReadOnlyList<string> DirectTools =
    [
        "list_displays",
        "capture_monitor",
        "read_text_from_monitor",
        "start_recording",
        "stop_recording",
    ];

    public static readonly IReadOnlyList<string> DesktopTestingTools =
    [
        "list_apps",
        "start_test_session",
        "restart_app",
        "observe_app",
        "focus_window",
        "click",
        "press_keys",
        "drag",
        "enter_text",
        "invoke",
        "check_ui",
        "get_action_result",
        "get_test_report",
        "end_test_session",
        "scroll",
    ];

    public static IReadOnlyList<string> Create(bool desktopTestingEnabled)
    {
        return desktopTestingEnabled
            ? DirectTools.Concat(DesktopTestingTools).ToArray()
            : DirectTools;
    }
}
