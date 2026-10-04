using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Pointframe.Services;
using Pointframe.ViewModels;
using Forms = System.Windows.Forms;

namespace Pointframe;

public partial class ScrollingCaptureProgressWindow : Window, IScrollingCaptureObserver
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private readonly Int32Rect _captureRegionPixels;

    internal ScrollingCaptureProgressWindow(
        ScrollingCaptureProgressViewModel viewModel,
        Int32Rect regionPixels)
    {
        _captureRegionPixels = regionPixels;
        InitializeComponent();
        DataContext = viewModel;
        ShowActivated = false;
        PlaceOnTargetMonitor(regionPixels);
    }

    internal static bool ShouldHideForCapture(Int32Rect windowBoundsPixels, Int32Rect captureRegionPixels) =>
        windowBoundsPixels.Width > 0 &&
        windowBoundsPixels.Height > 0 &&
        captureRegionPixels.Width > 0 &&
        captureRegionPixels.Height > 0 &&
        windowBoundsPixels.X < captureRegionPixels.X + captureRegionPixels.Width &&
        windowBoundsPixels.X + windowBoundsPixels.Width > captureRegionPixels.X &&
        windowBoundsPixels.Y < captureRegionPixels.Y + captureRegionPixels.Height &&
        windowBoundsPixels.Y + windowBoundsPixels.Height > captureRegionPixels.Y;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate));
    }

    async Task IScrollingCaptureObserver.HideForCaptureAsync(CancellationToken cancellationToken)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (NativeMethods.GetWindowRect(handle, out var bounds) && ShouldHideForCapture(
                new Int32Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                _captureRegionPixels))
        {
            await Dispatcher.InvokeAsync(Hide, DispatcherPriority.Normal, cancellationToken);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, cancellationToken);
        }
    }

    async Task IScrollingCaptureObserver.ReportProgressAsync(ScrollingCaptureProgress progress, CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (DataContext is ScrollingCaptureProgressViewModel viewModel)
            {
                viewModel.ReportProgress(progress);
            }

            if (!IsVisible)
            {
                Show();
            }
        }, DispatcherPriority.Normal, cancellationToken);
    }

    private void PlaceOnTargetMonitor(Int32Rect regionPixels)
    {
        var center = new System.Drawing.Point(
            regionPixels.X + (regionPixels.Width / 2),
            regionPixels.Y + (regionPixels.Height / 2));
        var screen = Forms.Screen.FromPoint(center);
        var scale = MonitorDpiHelper.GetMonitorScale(center);
        var monitorBounds = MonitorDpiHelper.CalculateWindowBounds(screen.Bounds, scale);
        Width = 340d;
        Left = monitorBounds.Right - Width - 20d;
        Top = monitorBounds.Top + 20d;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        internal static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
