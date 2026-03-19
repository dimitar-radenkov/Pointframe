using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class UserSettingsServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SnippingTool.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Update_ClonesCurrentAppliesMutationAndPersistsChanges()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);
        var original = sut.Current;
        var expectedTimestamp = new DateTime(2026, 3, 18, 12, 0, 0, DateTimeKind.Utc);

        sut.Update(settings =>
        {
            settings.LastAutoUpdateCheckUtc = expectedTimestamp;
            settings.AutoSaveScreenshots = true;
        });

        Assert.NotSame(original, sut.Current);
        Assert.Null(original.LastAutoUpdateCheckUtc);
        Assert.False(original.AutoSaveScreenshots);
        Assert.Equal(expectedTimestamp, sut.Current.LastAutoUpdateCheckUtc);
        Assert.True(sut.Current.AutoSaveScreenshots);

        var persisted = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath));

        Assert.NotNull(persisted);
        Assert.Equal(expectedTimestamp, persisted!.LastAutoUpdateCheckUtc);
        Assert.True(persisted.AutoSaveScreenshots);
    }

    [Fact]
    public void Update_WhenMutationThrows_DoesNotChangeCurrentOrPersistFile()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);
        var original = sut.Current;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sut.Update(_ => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", exception.Message);
        Assert.Same(original, sut.Current);
        Assert.False(File.Exists(settingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}