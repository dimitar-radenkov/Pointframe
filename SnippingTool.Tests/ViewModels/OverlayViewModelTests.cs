using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class OverlayViewModelTests
{
    private static BitmapSource CreateBitmap() => BitmapSource.Create(
        1,
        1,
        96,
        96,
        System.Windows.Media.PixelFormats.Bgra32,
        null,
        new byte[] { 0, 0, 0, 255 },
        4);

    private static OverlayViewModel Vm(UserSettings? settings = null, IDialogService? dialogService = null)
    {
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(s => s.Current).Returns(settings ?? new UserSettings());
        return new OverlayViewModel(
            new AnnotationGeometryService(),
            NullLogger<OverlayViewModel>.Instance,
            settingsMock.Object,
            dialogService ?? Mock.Of<IDialogService>(),
            Mock.Of<IClipboardService>(),
            Mock.Of<IFileSystemService>(),
            Mock.Of<IEventAggregator>());
    }

    private static OverlayViewModel Vm(
        Mock<IUserSettingsService> settingsMock,
        Mock<IDialogService>? dialogMock = null,
        Mock<IClipboardService>? clipboardMock = null,
        Mock<IFileSystemService>? fileSystemMock = null)
    {
        return new OverlayViewModel(
            new AnnotationGeometryService(),
            NullLogger<OverlayViewModel>.Instance,
            settingsMock.Object,
            dialogMock?.Object ?? Mock.Of<IDialogService>(),
            clipboardMock?.Object ?? Mock.Of<IClipboardService>(),
            fileSystemMock?.Object ?? Mock.Of<IFileSystemService>(),
            Mock.Of<IEventAggregator>());
    }

    [Fact]
    public void InitialPhase_IsSelecting()
    {
        // Arrange
        var vm = Vm();

        // Assert
        Assert.Equal(OverlayViewModel.Phase.Selecting, vm.CurrentPhase);
    }

    [Fact]
    public void InitialSelectionRect_IsEmpty()
    {
        // Arrange
        var vm = Vm();

        // Assert
        Assert.Equal(Rect.Empty, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_SetsSelectionRect()
    {
        // Arrange
        var vm = Vm();
        var rect = new Rect(10, 20, 300, 200);

        // Act
        vm.CommitSelection(rect);

        // Assert
        Assert.Equal(rect, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_TransitionsToAnnotating()
    {
        // Arrange
        var vm = Vm();

        // Act
        vm.CommitSelection(new Rect(0, 0, 100, 100));

        // Assert
        Assert.Equal(OverlayViewModel.Phase.Annotating, vm.CurrentPhase);
    }

    [Fact]
    public void CommitSelection_WithScreenBounds_StoresNativeBoundsAndScale()
    {
        // Arrange
        var vm = Vm();
        var rect = new Rect(10, 20, 300, 200);
        var screenBounds = new Int32Rect(150, 250, 900, 600);

        // Act
        vm.CommitSelection(rect, screenBounds);

        // Assert
        Assert.Equal(rect, vm.SelectionRect);
        Assert.Equal(screenBounds, vm.SelectionScreenBoundsPixels);
        Assert.Equal(3.0, vm.DpiX);
        Assert.Equal(3.0, vm.DpiY);
        Assert.Equal(OverlayViewModel.Phase.Annotating, vm.CurrentPhase);
    }

    [Fact]
    public void InitializeAnnotatingSession_SetsSelectionPhaseAndPixelScale()
    {
        // Arrange
        var vm = Vm();
        var rect = new Rect(50, 60, 400, 300);

        // Act
        vm.InitializeAnnotatingSession(rect, 2.5, 1.75);

        // Assert
        Assert.Equal(rect, vm.SelectionRect);
        Assert.Equal(OverlayViewModel.Phase.Annotating, vm.CurrentPhase);
        Assert.Equal(2.5, vm.DpiX);
        Assert.Equal(1.75, vm.DpiY);
    }

    [Fact]
    public void UpdateSizeLabel_FormatsWithDpi()
    {
        // Arrange
        var vm = Vm();
        vm.DpiX = 2.0;
        vm.DpiY = 2.0;

        // Act
        vm.UpdateSizeLabel(100, 50);

        // Assert
        Assert.Equal("200×100", vm.SizeLabel);
    }

    [Fact]
    public void UpdateSizeLabel_DefaultDpi_FormatsCorrectly()
    {
        // Arrange
        var vm = Vm();

        // Act
        vm.UpdateSizeLabel(640, 480);

        // Assert
        Assert.Equal("640×480", vm.SizeLabel);
    }

    [Fact]
    public void CopyCommand_SetsClipboardImage_AndRequestsClose()
    {
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(s => s.Current).Returns(new UserSettings { AutoSaveScreenshots = false });
        var clipboardMock = new Mock<IClipboardService>();
        var captureMock = new Mock<IOverlayBitmapCapture>();
        var bitmap = CreateBitmap();
        captureMock.Setup(c => c.ComposeBitmap()).Returns(bitmap);
        var vm = Vm(settingsMock, clipboardMock: clipboardMock);
        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.SetBitmapCapture(captureMock.Object);

        // Act
        vm.CopyCommand.Execute(null);

        clipboardMock.Verify(c => c.SetImage(bitmap), Times.Once);
        captureMock.Verify(c => c.ComposeBitmap(), Times.Once);
        Assert.True(closed);
    }

    [Fact]
    public void PickColorCommand_WhenDialogReturnsColor_UpdatesActiveColor()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.PickColor(Colors.Red)).Returns(Colors.LimeGreen);
        var vm = Vm(new UserSettings { DefaultAnnotationColor = "#FFFF0000" }, dialogMock.Object);

        vm.PickColorCommand.Execute(null);

        Assert.Equal(Colors.LimeGreen, vm.ActiveColor);
        dialogMock.VerifyAll();
    }

    [Fact]
    public void PickColorCommand_WhenCancelled_KeepsExistingColor()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock.Setup(d => d.PickColor(Colors.DodgerBlue)).Returns((System.Windows.Media.Color?)null);
        var vm = Vm(new UserSettings { DefaultAnnotationColor = "#FF1E90FF" }, dialogMock.Object);

        vm.PickColorCommand.Execute(null);

        Assert.Equal(Colors.DodgerBlue, vm.ActiveColor);
        dialogMock.VerifyAll();
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested()
    {
        // Arrange
        var vm = Vm();
        var fired = false;
        vm.CloseRequested += () => fired = true;

        // Act
        vm.CloseCommand.Execute(null);

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void PinCommand_FiresPinRequested_WithBitmap()
    {
        var captureMock = new Mock<IOverlayBitmapCapture>();
        var bitmap = CreateBitmap();
        captureMock.Setup(c => c.ComposeBitmap(false)).Returns(bitmap);
        var vm = Vm();
        BitmapSource? pinnedBitmap = null;
        vm.PinRequested += b => pinnedBitmap = b;
        vm.SetBitmapCapture(captureMock.Object);

        // Act
        vm.PinCommand.Execute(null);

        Assert.Same(bitmap, pinnedBitmap);
        captureMock.Verify(c => c.ComposeBitmap(false), Times.Once);
    }

    [Fact]
    public void CopyCommand_WhenAutoSaveEnabled_WritesPngFile()
    {
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(s => s.Current).Returns(new UserSettings
        {
            AutoSaveScreenshots = true,
            ScreenshotSavePath = @"C:\Shots"
        });
        var clipboardMock = new Mock<IClipboardService>();
        var fileSystemMock = new Mock<IFileSystemService>();
        using var outputStream = new MemoryStream();
        fileSystemMock.Setup(f => f.CombinePath(@"C:\Shots", It.IsRegex(@"^Snip_\d{8}_\d{6}\.png$"))).Returns(@"C:\Shots\Snip_20260318_123456.png");
        fileSystemMock.Setup(f => f.OpenWrite(@"C:\Shots\Snip_20260318_123456.png")).Returns(outputStream);
        var captureMock = new Mock<IOverlayBitmapCapture>();
        captureMock.Setup(c => c.ComposeBitmap()).Returns(CreateBitmap());
        var vm = Vm(settingsMock, clipboardMock: clipboardMock, fileSystemMock: fileSystemMock);
        vm.SetBitmapCapture(captureMock.Object);

        vm.CopyCommand.Execute(null);

        fileSystemMock.Verify(f => f.CreateDirectory(@"C:\Shots"), Times.Once);
        fileSystemMock.Verify(f => f.CombinePath(@"C:\Shots", It.IsRegex(@"^Snip_\d{8}_\d{6}\.png$")), Times.Once);
        fileSystemMock.Verify(f => f.OpenWrite(@"C:\Shots\Snip_20260318_123456.png"), Times.Once);
        clipboardMock.Verify(c => c.SetImage(It.IsAny<BitmapSource>()), Times.Once);
    }

    [Fact]
    public void CopyTextCommand_TogglesIsTextLassoActive()
    {
        // Arrange
        var vm = Vm();
        Assert.False(vm.IsTextLassoActive);

        // Act — toggle on
        vm.CopyTextCommand.Execute(null);

        // Assert
        Assert.True(vm.IsTextLassoActive);

        // Act — toggle off
        vm.CopyTextCommand.Execute(null);

        // Assert
        Assert.False(vm.IsTextLassoActive);
    }

    [Fact]
    public void CurrentPhase_PropertyChanged_FiredOnCommit()
    {
        // Arrange
        var vm = Vm();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CurrentPhase))
            {
                raised = true;
            }
        };

        // Act
        vm.CommitSelection(new Rect(0, 0, 50, 50));

        // Assert
        Assert.True(raised);
    }
}
