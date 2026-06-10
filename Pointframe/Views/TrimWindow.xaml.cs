using System.Windows;
using System.Windows.Threading;
using Pointframe.ViewModels;

namespace Pointframe;

public partial class TrimWindow : Window
{
    private readonly TrimViewModel _vm;
    private readonly DispatcherTimer _positionTimer;
    private bool _isPlaying;
    private bool _isDraggingPosition;

    public TrimWindow(TrimViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.RequestClose += () =>
        {
            if (Dispatcher.CheckAccess())
            {
                Close();
            }
            else
            {
                Dispatcher.Invoke(Close);
            }
        };

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _positionTimer.Tick += PositionTimer_Tick;

        Player.Source = new Uri(vm.InputPath);
        Closed += (_, _) =>
        {
            _positionTimer.Stop();
            Player.Close();
        };
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            _vm.SetMediaDuration(Player.NaturalDuration.TimeSpan);
            PositionSlider.Maximum = _vm.DurationSeconds;
        }

        // Render the first frame without starting playback. Setting the trim range above
        // scrubbed the preview to the end handle, so seek back to the start.
        Player.Play();
        Player.Pause();
        SeekTo(_vm.StartSeconds);
        _positionTimer.Start();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _vm.StatusText = "Preview unavailable for this file.";
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDraggingPosition)
        {
            return;
        }

        var position = Player.Position.TotalSeconds;

        // Loop playback within the selected trim range so the preview reflects the result.
        if (_isPlaying && position >= _vm.EndSeconds)
        {
            Player.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            SeekTo(_vm.StartSeconds);
            return;
        }

        PositionSlider.Value = position;
        PositionText.Text = TimeSpan.FromSeconds(position).ToString(@"mm\:ss\.f");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            return;
        }

        var position = Player.Position.TotalSeconds;
        if (position < _vm.StartSeconds || position >= _vm.EndSeconds)
        {
            SeekTo(_vm.StartSeconds);
        }

        Player.Play();
        _isPlaying = true;
        PlayPauseButton.Content = "⏸";
    }

    private void PositionSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _isDraggingPosition = true;
    }

    private void PositionSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _isDraggingPosition = false;
        SeekTo(PositionSlider.Value);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingPosition)
        {
            PositionText.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"mm\:ss\.f");
        }
    }

    private void StartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PreviewHandlePosition(e.NewValue);
    }

    private void EndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PreviewHandlePosition(e.NewValue);
    }

    private void SetStartFromPlayhead_Click(object sender, RoutedEventArgs e)
    {
        _vm.StartSeconds = Player.Position.TotalSeconds;
    }

    private void SetEndFromPlayhead_Click(object sender, RoutedEventArgs e)
    {
        _vm.EndSeconds = Player.Position.TotalSeconds;
    }

    private void PreviewHandlePosition(double seconds)
    {
        // While paused, scrub the preview to the handle being moved so the user
        // sees the exact frame the trim will cut at.
        if (!_isPlaying)
        {
            SeekTo(seconds);
        }
    }

    private void SeekTo(double seconds)
    {
        if (!Player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        Player.Position = TimeSpan.FromSeconds(seconds);
        PositionSlider.Value = seconds;
        PositionText.Text = TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f");
    }
}
