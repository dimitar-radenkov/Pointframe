using System.Windows;
using System.Windows.Threading;

namespace SnippingTool;

public partial class CountdownWindow : Window
{
    private readonly Action _onComplete;
    private readonly DispatcherTimer _timer;
    private int _remaining;

    public CountdownWindow(int seconds, Action onComplete)
    {
        InitializeComponent();
        _onComplete = onComplete;
        _remaining = seconds;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        Loaded += (_, _) =>
        {
            CountText.Text = _remaining.ToString();
            _timer.Start();
        };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            Close();
            _onComplete();
            return;
        }

        CountText.Text = _remaining.ToString();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            _timer.Stop();
            Close();
            e.Handled = true;
        }
    }
}
