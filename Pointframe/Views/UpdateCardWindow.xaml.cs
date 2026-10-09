using System.Windows;
using System.Windows.Media;

namespace Pointframe;

public partial class UpdateCardWindow : Window
{
    private readonly Action _updateNow;
    private bool _closed;
    private bool _updateChosen;

    public event Action? Dismissed;

    public UpdateCardWindow(Version version, Action updateNow)
    {
        InitializeComponent();
        _updateNow = updateNow;
        VersionText.Text = $"Pointframe v{version.Major}.{version.Minor}.{version.Build} is available";
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += PositionInPrimaryWorkArea;
    }

    private void PositionInPrimaryWorkArea(object sender, RoutedEventArgs e)
    {
        Loaded -= PositionInPrimaryWorkArea;
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var right = workArea.Right / dpi.DpiScaleX;
        var bottom = workArea.Bottom / dpi.DpiScaleY;
        Left = right - ActualWidth - 16;
        Top = bottom - ActualHeight - 16;
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _updateChosen = true;
        Close();
        _updateNow();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (!_updateChosen)
        {
            Dismissed?.Invoke();
        }
        base.OnClosed(e);
    }
}
