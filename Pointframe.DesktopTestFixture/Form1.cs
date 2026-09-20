using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Pointframe.DesktopTestFixture;

public partial class Form1 : Form
{
    public const string StatePathVariable = "POINTFRAME_FIXTURE_STATE_PATH";
    public const string MonitorVariable = "POINTFRAME_FIXTURE_MONITOR";

    private readonly string? _statePath = Environment.GetEnvironmentVariable(StatePathVariable);
    private readonly object _stateGate = new();
    private readonly System.Windows.Forms.Timer _foregroundTimer = new();

    private int _clickCount;
    private Point _lastClickPoint = Point.Empty;
    private int _dragDownCount;
    private int _dragMoveWhileDownCount;
    private int _dragUpCount;
    private Point _dragDownPoint = Point.Empty;
    private Point _dragUpPoint = Point.Empty;
    private bool _dragButtonDown;
    private int _wheelCount;
    private int _wheelDeltaTotal;
    private Point _lastWheelPoint = Point.Empty;
    private string _lastEvent = "none";

    public Form1()
    {
        InitializeComponent();
        StartPosition = FormStartPosition.Manual;

        _clickTarget.MouseClick += OnClickTargetClicked;
        _dragSurface.MouseDown += OnDragSurfaceMouseDown;
        _dragSurface.MouseMove += OnDragSurfaceMouseMove;
        _dragSurface.MouseUp += OnDragSurfaceMouseUp;
        _textBox.TextChanged += (_, _) =>
        {
            _lastEvent = "text";
            WriteState();
        };
    }

    private const int WmMouseWheel = 0x020A;

    protected override void WndProc(ref Message m)
    {
        // Windows delivers WM_MOUSEWHEEL to the focused control, not the one under the pointer, and a
        // Panel cannot take focus. Subscribing to _scrollSurface.MouseWheel therefore only fired when
        // focus happened to sit somewhere convenient, which read as an intermittent "the wheel never
        // arrived" failure in the driver's tests. Catch the message on the form and attribute it by
        // cursor position instead, which is what the wheel's routing actually means.
        if (m.Msg == WmMouseWheel)
        {
            var delta = (short)((ulong)m.WParam >> 16);
            var cursor = Cursor.Position;
            if (_scrollSurface.RectangleToScreen(_scrollSurface.ClientRectangle).Contains(cursor))
            {
                _wheelCount++;
                _wheelDeltaTotal += delta;
                _lastWheelPoint = _scrollSurface.PointToClient(cursor);
                _lastEvent = "wheel";
                WriteState();
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MoveToRequestedMonitor();
        if (!string.IsNullOrWhiteSpace(_statePath))
        {
            // Only when a harness is driving: the input preflight refuses to dispatch unless the
            // approved process is foreground, and a process launched by a background MCP server does
            // not inherit the right to take foreground. Re-assert it for the fixture's short life.
            TakeForeground();
            _foregroundTimer.Interval = 250;
            _foregroundTimer.Tick += (_, _) => TakeForeground();
            _foregroundTimer.Start();
        }

        WriteState();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _foregroundTimer.Stop();
        _foregroundTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void TakeForeground()
    {
        var handle = Handle;

        // Do nothing at all while this window is already foreground. Re-asserting topmost on a timer
        // is a z-order change, and one landing between a synthesized pointer move and the wheel or
        // button that follows it makes that input disappear. With a 250 ms timer against gestures a
        // few milliseconds wide, that showed up as a roughly one-in-four flake in the driver's own
        // tests -- a fault in the fixture, misread as a fault in the driver.
        if (NativeMethods.GetForegroundWindow() == handle)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

        // Windows only lets the foreground thread hand activation over, so borrow its input queue
        // for the duration of the call.
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        NativeMethods.SetForegroundWindow(handle);
        if (attached)
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = ClientRectangle;
        using var brush = new SolidBrush(Color.FromArgb(235, 242, 255));
        e.Graphics.FillRectangle(brush, bounds);

        using var pen = new Pen(Color.MidnightBlue, 4);
        e.Graphics.DrawRectangle(pen, 24, 24, bounds.Width - 48, bounds.Height - 48);
        e.Graphics.DrawLine(pen, 24, 24, bounds.Width - 24, bounds.Height - 48);
        e.Graphics.DrawLine(pen, bounds.Width - 24, 24, 24, bounds.Height - 48);

        using var font = new Font(Font.FontFamily, 18, FontStyle.Bold);
        e.Graphics.DrawString("Pointframe external desktop fixture", font, Brushes.MidnightBlue, 48, 48);
    }

    private void MoveToRequestedMonitor()
    {
        var requested = Environment.GetEnvironmentVariable(MonitorVariable);
        var screen = ResolveScreen(requested);
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        Location = new Point(
            area.X + Math.Max(0, (area.Width - Width) / 2),
            area.Y + Math.Max(0, (area.Height - Height) / 2));
    }

    private static Screen? ResolveScreen(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Screen.PrimaryScreen;
        }

        var screens = Screen.AllScreens;
        if (int.TryParse(requested, out var index))
        {
            return index >= 0 && index < screens.Length ? screens[index] : Screen.PrimaryScreen;
        }

        return Array.Find(
            screens,
            item => string.Equals(item.DeviceName, requested, StringComparison.OrdinalIgnoreCase));
    }

    private void OnClickTargetClicked(object? sender, MouseEventArgs e)
    {
        _clickCount++;
        _lastClickPoint = e.Location;
        _lastEvent = $"click({e.Button})";
        WriteState();
    }

    private void OnDragSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        _dragDownCount++;
        _dragDownPoint = e.Location;
        _dragMoveWhileDownCount = 0;
        _dragButtonDown = true;
        _lastEvent = "drag_down";
        WriteState();
    }

    private void OnDragSurfaceMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragButtonDown || e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragMoveWhileDownCount++;
        _lastEvent = "drag_move";
        WriteState();
    }

    private void OnDragSurfaceMouseUp(object? sender, MouseEventArgs e)
    {
        _dragUpCount++;
        _dragUpPoint = e.Location;
        _dragButtonDown = false;
        _lastEvent = "drag_up";
        WriteState();
    }

    private void WriteState()
    {
        var clickScreen = _clickTarget.RectangleToScreen(_clickTarget.ClientRectangle);
        var dragScreen = _dragSurface.RectangleToScreen(_dragSurface.ClientRectangle);
        var scrollScreen = _scrollSurface.RectangleToScreen(_scrollSurface.ClientRectangle);
        var textBoxScreen = _textBox.RectangleToScreen(_textBox.ClientRectangle);
        var builder = new StringBuilder();
        builder.Append("{\n");
        Append(builder, "lastEvent", $"\"{_lastEvent}\"");
        Append(builder, "clickCount", _clickCount);
        Append(builder, "lastClickPoint", Serialize(_lastClickPoint));
        Append(builder, "clickTargetSize", Serialize(new Point(_clickTarget.Width, _clickTarget.Height)));
        Append(builder, "clickTargetScreen", Serialize(clickScreen));
        Append(builder, "dragDownCount", _dragDownCount);
        Append(builder, "dragMoveWhileDownCount", _dragMoveWhileDownCount);
        Append(builder, "dragUpCount", _dragUpCount);
        Append(builder, "dragDownPoint", Serialize(_dragDownPoint));
        Append(builder, "dragUpPoint", Serialize(_dragUpPoint));
        Append(builder, "dragSurfaceScreen", Serialize(dragScreen));
        Append(builder, "wheelCount", _wheelCount);
        Append(builder, "wheelDeltaTotal", _wheelDeltaTotal);
        Append(builder, "lastWheelPoint", Serialize(_lastWheelPoint));
        Append(builder, "scrollSurfaceScreen", Serialize(scrollScreen));
        // Reported so text entry can be verified from the application's own state rather than by
        // reading pixels back with OCR.
        Append(builder, "textBoxScreen", Serialize(textBoxScreen));
        Append(builder, "textBoxText", $"\"{_textBox.Text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        Append(builder, "windowScreen", Serialize(Bounds));
        Append(builder, "cursorScreen", Serialize(Cursor.Position), last: true);
        builder.Append("}\n");

        _statusLabel.Text = builder.ToString();
        if (string.IsNullOrWhiteSpace(_statePath))
        {
            return;
        }

        lock (_stateGate)
        {
            try
            {
                // Replace through a sibling temp file so a reader never observes a half-written
                // document; the harness polls this file while the fixture keeps updating it.
                var temporaryPath = _statePath + ".tmp";
                File.WriteAllText(temporaryPath, builder.ToString());
                File.Move(temporaryPath, _statePath, overwrite: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Append(StringBuilder builder, string name, object value, bool last = false)
    {
        builder.Append("  \"").Append(name).Append("\": ").Append(value);
        builder.Append(last ? "\n" : ",\n");
    }

    private static string Serialize(Point point) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{point.X},{point.Y}\"");

    private static string Serialize(Rectangle rectangle) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"\"{rectangle.X},{rectangle.Y},{rectangle.Width},{rectangle.Height}\"");

    private static class NativeMethods
    {
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoActivate = 0x0010;
        internal static readonly IntPtr HwndTopmost = new(-1);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
    }
}
