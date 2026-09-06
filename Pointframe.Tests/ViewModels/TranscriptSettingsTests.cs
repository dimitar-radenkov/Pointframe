using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class TranscriptSettingsTests
{
    private static SettingsViewModel CreateVm(
        bool recordMicrophone,
        ITranscriptModelService modelService)
    {
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(new UserSettings
        {
            RecordMicrophone = recordMicrophone,
        });

        return new SettingsViewModel(
            settingsService.Object,
            Mock.Of<IThemeService>(),
            Mock.Of<IDialogService>(),
            Mock.Of<IMicrophoneDeviceService>(service =>
                service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic" } &&
                service.GetDefaultCaptureDeviceName() == "Studio Mic"),
            Mock.Of<ITelemetryService>(),
            modelService);
    }

    private static ITranscriptModelService ModelService(bool installed)
    {
        return Mock.Of<ITranscriptModelService>(service => service.IsModelInstalled == installed);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void CanEnableTranscript_RequiresBothMicrophoneAndModel(
        bool recordMicrophone,
        bool modelInstalled,
        bool expected)
    {
        var vm = CreateVm(recordMicrophone, ModelService(modelInstalled));

        Assert.Equal(expected, vm.CanEnableTranscript);
    }

    [Fact]
    public void TurningOffMicrophone_DisablesTranscript()
    {
        var vm = CreateVm(recordMicrophone: true, ModelService(installed: true));
        Assert.True(vm.CanEnableTranscript);

        vm.RecordMicrophone = false;

        Assert.False(vm.CanEnableTranscript);
    }

    [Fact]
    public void TranscriptCheckbox_CanStillBeTurnedOff_WhenPrerequisitesAreMissing()
    {
        var settings = new UserSettings
        {
            RecordMicrophone = true,
            RecordingTranscriptEnabled = true,
        };
        var service = new Mock<IUserSettingsService>();
        service.SetupGet(s => s.Current).Returns(settings);

        var vm = new SettingsViewModel(
            service.Object,
            Mock.Of<IThemeService>(),
            Mock.Of<IDialogService>(),
            Mock.Of<IMicrophoneDeviceService>(s =>
                s.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic" } &&
                s.GetDefaultCaptureDeviceName() == "Studio Mic"),
            Mock.Of<ITelemetryService>(),
            ModelService(installed: false));

        Assert.True(vm.RecordingTranscriptEnabled);
        Assert.True(vm.CanToggleTranscript);
        Assert.False(vm.CanEnableTranscript);

        vm.RecordingTranscriptEnabled = false;

        Assert.False(vm.RecordingTranscriptEnabled);
    }

    [Fact]
    public void DownloadPrompt_IsShownOnlyWhenModelIsMissing()
    {
        Assert.True(CreateVm(true, ModelService(installed: false)).ShowTranscriptModelDownload);
        Assert.False(CreateVm(true, ModelService(installed: true)).ShowTranscriptModelDownload);
    }

    [Fact]
    public void StatusText_ExplainsWhyTranscriptIsUnavailable()
    {
        Assert.Contains("not installed", CreateVm(true, ModelService(false)).TranscriptStatusText);
        Assert.Contains("microphone", CreateVm(false, ModelService(true)).TranscriptStatusText);
        Assert.Contains("Ready", CreateVm(true, ModelService(true)).TranscriptStatusText);
    }

    [Fact]
    public async Task SuccessfulDownload_EnablesTheTranscriptSetting()
    {
        var installed = false;
        var modelService = new Mock<ITranscriptModelService>();
        modelService.SetupGet(s => s.IsModelInstalled).Returns(() => installed);
        modelService
            .Setup(s => s.DownloadModel(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                installed = true;
                return true;
            });

        var vm = CreateVm(recordMicrophone: true, modelService.Object);
        Assert.False(vm.CanEnableTranscript);

        await vm.DownloadTranscriptModelCommand.ExecuteAsync(null);

        Assert.True(vm.TranscriptModelInstalled);
        Assert.True(vm.CanEnableTranscript);
        Assert.False(vm.ShowTranscriptModelDownload);
        Assert.False(vm.IsDownloadingTranscriptModel);
        Assert.False(vm.TranscriptModelDownloadFailed);
    }

    [Fact]
    public async Task FailedDownload_ReportsFailureAndLeavesTranscriptDisabled()
    {
        var modelService = new Mock<ITranscriptModelService>();
        modelService.SetupGet(s => s.IsModelInstalled).Returns(false);
        modelService
            .Setup(s => s.DownloadModel(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var vm = CreateVm(recordMicrophone: true, modelService.Object);

        await vm.DownloadTranscriptModelCommand.ExecuteAsync(null);

        Assert.True(vm.TranscriptModelDownloadFailed);
        Assert.False(vm.CanEnableTranscript);
        Assert.True(vm.ShowTranscriptModelDownload);
        Assert.False(vm.IsDownloadingTranscriptModel);
        Assert.Contains("failed", vm.TranscriptStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DownloadCommand_IsDisabledWhenModelIsAlreadyInstalled()
    {
        var vm = CreateVm(recordMicrophone: true, ModelService(installed: true));

        Assert.False(vm.DownloadTranscriptModelCommand.CanExecute(null));
    }
}
