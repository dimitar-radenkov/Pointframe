using Pointframe.Services;

namespace Pointframe.ViewModels;

internal partial class ScrollingCaptureProgressViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellationTokenSource;

    [ObservableProperty]
    private string _statusText = "Preparing scrolling capture…";

    public ScrollingCaptureProgressViewModel(CancellationTokenSource cancellationTokenSource)
    {
        _cancellationTokenSource = cancellationTokenSource;
    }

    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    public void ReportProgress(ScrollingCaptureProgress progress)
    {
        StatusText = $"Captured {progress.FrameCount} frames · {progress.HeightPixels:N0} px";
    }

    [RelayCommand]
    private void CancelCapture()
    {
        _cancellationTokenSource.Cancel();
    }
}
