using System.IO;
using Moq;
using Pointframe.Engine;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class WelcomeEligibilityPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pointframe.Tests", Guid.NewGuid().ToString("N"));
    private readonly UserSettings _settings = new();
    private readonly Mock<ICaptureCatalogService> _catalog = new();

    private WelcomeEligibilityPolicy CreatePolicy()
    {
        var settings = Mock.Of<IUserSettingsService>(service => service.Current == _settings);
        return new WelcomeEligibilityPolicy(settings, _catalog.Object, new TestSources(_root));
    }

    [Fact]
    public async Task FreshInstallWithoutCaptureEvidence_IsEligible()
    {
        _catalog.Setup(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.True(await CreatePolicy().ShouldShowAsync(automationMode: false));
    }

    [Fact]
    public async Task ExistingCatalogCapture_IsNotEligible()
    {
        _catalog.Setup(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode: false));
    }

    [Fact]
    public async Task EmptyCatalogWithLegacyCaptureFile_IsNotEligible()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "legacy.png"), "image");
        _catalog.Setup(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode: false));
    }

    [Fact]
    public async Task CatalogLookupFailure_DefersWelcome()
    {
        _catalog.Setup(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("lookup failed"));

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExcludedSettings_AreNotEligible(bool automationMode, bool welcomeShown)
    {
        _settings.WelcomeShown = welcomeShown;
        _catalog.Setup(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode));
        _catalog.Verify(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstCaptureAlreadyTracked_IsNotEligible()
    {
        _settings.FirstCaptureCompletedTracked = true;

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode: false));
        _catalog.Verify(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstRecordingAlreadyTracked_IsNotEligible()
    {
        _settings.FirstRecordingCompletedTracked = true;

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode: false));
        _catalog.Verify(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyCatalogWithLegacyRecordingFile_IsNotEligible()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "legacy.mp4"), "video");
        _catalog.Setup(service => service.HasAnyCaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.False(await CreatePolicy().ShouldShowAsync(automationMode: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestSources(string root) : ICaptureLibrarySources
    {
        public IReadOnlyList<string> GetImportRoots() => [root];
    }
}
