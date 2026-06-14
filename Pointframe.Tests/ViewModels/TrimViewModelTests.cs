using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class TrimViewModelTests
{
    [Fact]
    public void SetMediaDuration_InitializesFullTrimRange()
    {
        var vm = CreateViewModel();

        vm.SetMediaDuration(TimeSpan.FromSeconds(42));

        Assert.Equal(42, vm.DurationSeconds);
        Assert.Equal(0, vm.StartSeconds);
        Assert.Equal(42, vm.EndSeconds);
    }

    [Fact]
    public void StartSeconds_PushedPastEnd_DragsEndAlong()
    {
        var vm = CreateViewModel();
        vm.SetMediaDuration(TimeSpan.FromSeconds(10));
        vm.EndSeconds = 5;

        vm.StartSeconds = 7;

        Assert.Equal(7, vm.EndSeconds);
    }

    [Fact]
    public void EndSeconds_PushedBeforeStart_DragsStartAlong()
    {
        var vm = CreateViewModel();
        vm.SetMediaDuration(TimeSpan.FromSeconds(10));
        vm.StartSeconds = 6;

        vm.EndSeconds = 4;

        Assert.Equal(4, vm.StartSeconds);
    }

    [Fact]
    public void SaveTrimCommand_DisabledUntilMediaLoadedAndRangeValid()
    {
        var vm = CreateViewModel();

        Assert.False(vm.SaveTrimCommand.CanExecute(null));

        vm.SetMediaDuration(TimeSpan.FromSeconds(10));
        Assert.True(vm.SaveTrimCommand.CanExecute(null));

        vm.StartSeconds = 5;
        vm.EndSeconds = 5;
        Assert.False(vm.SaveTrimCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveTrim_OnSuccess_RaisesCompletionAndClose()
    {
        var trimService = new Mock<IVideoTrimService>();
        var vm = CreateViewModel(trimService.Object, @"C:\videos\recording.mp4");
        vm.SetMediaDuration(TimeSpan.FromSeconds(10));
        vm.StartSeconds = 2;
        vm.EndSeconds = 6;

        string? completedPath = null;
        TimeSpan? completedDuration = null;
        var closed = false;
        vm.TrimCompleted += (path, duration) =>
        {
            completedPath = path;
            completedDuration = duration;
        };
        vm.RequestClose += () => closed = true;

        await vm.SaveTrimCommand.ExecuteAsync(null);

        trimService.Verify(s => s.Trim(
            @"C:\videos\recording.mp4",
            It.Is<string>(p => p.EndsWith(".trimmed.mp4", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(6),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(completedPath);
        Assert.Equal(TimeSpan.FromSeconds(4), completedDuration);
        Assert.True(closed);
    }

    [Fact]
    public async Task SaveTrim_OnFailure_ShowsErrorAndStaysOpen()
    {
        var trimService = new Mock<IVideoTrimService>();
        trimService
            .Setup(s => s.Trim(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("trim failed"));
        var vm = CreateViewModel(trimService.Object);
        vm.SetMediaDuration(TimeSpan.FromSeconds(10));

        var closed = false;
        vm.RequestClose += () => closed = true;

        await vm.SaveTrimCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.False(vm.IsTrimming);
        Assert.Equal("Trim failed. Please try again.", vm.StatusText);
    }

    [Fact]
    public void CancelCommand_RaisesRequestClose()
    {
        var vm = CreateViewModel();
        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public async Task CancelCommand_WhileTrimming_CancelsAndClosesAfterCancellation()
    {
        var trimService = new Mock<IVideoTrimService>();
        trimService
            .Setup(s => s.Trim(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan, TimeSpan, CancellationToken>(async (_, _, _, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        var vm = CreateViewModel(trimService.Object);
        vm.SetMediaDuration(TimeSpan.FromSeconds(10));

        var closed = false;
        vm.RequestClose += () => closed = true;

        var saveTask = vm.SaveTrimCommand.ExecuteAsync(null);

        while (!vm.IsTrimming)
        {
            await Task.Yield();
        }

        vm.CancelCommand.Execute(null);

        await saveTask;

        Assert.True(closed);
        Assert.False(vm.IsTrimming);
        Assert.Equal("Trim canceled.", vm.StatusText);
    }

    private static TrimViewModel CreateViewModel(IVideoTrimService? trimService = null, string? inputPath = null)
    {
        return new TrimViewModel(
            inputPath ?? Path.Combine(Path.GetTempPath(), "recording.mp4"),
            trimService ?? Mock.Of<IVideoTrimService>(),
            Mock.Of<ITelemetryService>(),
            NullLogger<TrimViewModel>.Instance);
    }
}
