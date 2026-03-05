using Moq;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class AboutViewModelTests
{
    private static AboutViewModel CreateVm(Version? version = null) =>
        new(new FakeAppVersionService(version ?? new Version(1, 2, 3)), new Mock<IProcessService>().Object);

    [Fact]
    public void Version_FormatsAsMajorMinorPatch()
    {
        var vm = CreateVm(new Version(2, 1, 5));

        Assert.Equal("Version 2.1.5", vm.Version);
    }

    [Fact]
    public void CloseCommand_RaisesRequestClose()
    {
        var vm = CreateVm();
        var raised = false;
        vm.RequestClose += () => raised = true;

        vm.CloseCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void CloseCommand_NoSubscribers_DoesNotThrow()
    {
        var vm = CreateVm();

        var ex = Record.Exception(() => vm.CloseCommand.Execute(null));

        Assert.Null(ex);
    }

    [Fact]
    public void OpenUrlCommand_CanExecute_WithValidUrl()
    {
        var vm = CreateVm();

        Assert.True(vm.OpenUrlCommand.CanExecute("https://github.com"));
    }

    private sealed class FakeAppVersionService(Version version) : IAppVersionService
    {
        public Version Current { get; } = version;
    }
}
