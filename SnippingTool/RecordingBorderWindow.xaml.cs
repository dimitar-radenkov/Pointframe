using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SnippingTool;

public partial class RecordingBorderWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    public RecordingBorderWindow(double left, double top, double width, double height)
    {
        InitializeComponent();
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Make the window completely click-through so the user can interact
        // with whatever is underneath the recording region.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
    }
}
