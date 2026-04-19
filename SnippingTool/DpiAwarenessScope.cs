using System.Runtime.InteropServices;

namespace Pointframe;

internal static class DpiAwarenessScope
{
    private static readonly nint SystemAwareContext = new(-2);
    private static readonly nint PerMonitorV2Context = new(-4);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    internal static void RunSystemAware(Action action)
    {
        RunInContext(SystemAwareContext, action);
    }

    internal static void RunPerMonitorV2(Action action)
    {
        RunInContext(PerMonitorV2Context, action);
    }

    private static void RunInContext(nint dpiContext, Action action)
    {
        var previousContext = SetThreadDpiAwarenessContext(dpiContext);
        try
        {
            action();
        }
        finally
        {
            if (previousContext != 0)
            {
                SetThreadDpiAwarenessContext(previousContext);
            }
        }
    }
}
