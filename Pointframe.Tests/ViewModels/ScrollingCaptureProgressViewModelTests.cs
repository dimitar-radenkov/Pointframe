using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class ScrollingCaptureProgressViewModelTests
{
    [Fact]
    public void ReportProgress_UpdatesStatusWithFramesAndHeight()
    {
        using var cancellation = new CancellationTokenSource();
        var viewModel = new ScrollingCaptureProgressViewModel(cancellation);

        viewModel.ReportProgress(new ScrollingCaptureProgress(4, 1250));

        Assert.Equal("Captured 4 frames · 1,250 px", viewModel.StatusText);
    }

    [Fact]
    public void CancelCaptureCommand_CancelsCaptureToken()
    {
        using var cancellation = new CancellationTokenSource();
        var viewModel = new ScrollingCaptureProgressViewModel(cancellation);

        viewModel.CancelCaptureCommand.Execute(null);

        Assert.True(viewModel.CancellationToken.IsCancellationRequested);
    }
}
