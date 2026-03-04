using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private static SettingsViewModel CreateVm(UserSettings? settings = null)
    {
        var fake = new ConfigurableFakeSettingsService(settings ?? new UserSettings());
        return new SettingsViewModel(fake);
    }

    [Fact]
    public void UserSettings_Default_CaptureDelaySeconds_IsZero()
    {
        // Arrange
        var settings = new UserSettings();

        // Act — default value, nothing to act on

        // Assert
        Assert.Equal(0, settings.CaptureDelaySeconds);
    }

    [Fact]
    public void LoadsFromSettings_CaptureDelaySeconds()
    {
        // Arrange
        var vm = CreateVm(new UserSettings { CaptureDelaySeconds = 5 });

        // Act — value loaded during construction, nothing further to act on

        // Assert
        Assert.Equal(5, vm.CaptureDelaySeconds);
    }

    [Fact]
    public void Save_PersistsCaptureDelaySeconds()
    {
        // Arrange
        var fake = new ConfigurableFakeSettingsService(new UserSettings());
        var vm = new SettingsViewModel(fake);
        vm.CaptureDelaySeconds = 10;

        // Act
        vm.SaveCommand.Execute(null);

        // Assert
        Assert.Equal(10, fake.Saved?.CaptureDelaySeconds);
    }

    [Fact]
    public void CaptureDelaySeconds_PropertyChanged_Fired()
    {
        // Arrange
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.CaptureDelaySeconds = 3;

        // Assert
        Assert.Contains(nameof(vm.CaptureDelaySeconds), raised);
    }

    private sealed class ConfigurableFakeSettingsService(UserSettings settings) : IUserSettingsService
    {
        public UserSettings Current { get; } = settings;
        public UserSettings? Saved { get; private set; }
        public void Save(UserSettings s) => Saved = s;
    }
}
