using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingRecoveryTests
{
    private static RecordingRecovery CreateSut(FakeRemuxer remuxer) =>
        new(remuxer, NullLogger<RecordingRecovery>.Instance);

    [Fact]
    public void GetIntermediatePath_IsHiddenStyleNameNextToOutput()
    {
        var path = RecordingRecovery.GetIntermediatePath(@"C:\Videos\SnipRec-1.mp4");

        Assert.Equal(@"C:\Videos\.SnipRec-1.recording.mp4", path);
        Assert.True(RecordingRecovery.TryGetOutputPath(path, out var output));
        Assert.Equal(@"C:\Videos\SnipRec-1.mp4", output);
    }

    [Theory]
    [InlineData(@"C:\Videos\SnipRec-1.mp4")]
    [InlineData(@"C:\Videos\SnipRec-1.recorded.mp4")]
    [InlineData(@"C:\Videos\SnipRec-1.recording.mp4")]
    [InlineData(@"C:\Videos\..recording.mp4")]
    public void TryGetOutputPath_RejectsNamesThatAreNotIntermediates(string path)
    {
        Assert.False(RecordingRecovery.TryGetOutputPath(path, out _));
    }

    [Fact]
    public void ContainsCompleteFragment_RequiresMoofFollowedByWholeMdat()
    {
        using var folder = new TempFolder();
        var headerOnly = folder.File("header.mp4");
        var complete = folder.File("complete.mp4");
        var truncated = folder.File("truncated.mp4");
        FragmentedMp4Fixture.Write(headerOnly, withFragment: false);
        FragmentedMp4Fixture.Write(complete, withFragment: true);
        FragmentedMp4Fixture.Write(truncated, withFragment: true, truncateLastBox: true);

        Assert.False(RecordingRecovery.ContainsCompleteFragment(headerOnly));
        Assert.True(RecordingRecovery.ContainsCompleteFragment(complete));
        Assert.False(RecordingRecovery.ContainsCompleteFragment(truncated));
        Assert.False(RecordingRecovery.ContainsCompleteFragment(folder.File("missing.mp4")));
    }

    [Fact]
    public void Recover_WhenRemuxSucceeds_ProducesOutputAndRemovesIntermediate()
    {
        using var folder = new TempFolder();
        var output = folder.File("a.mp4");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);
        var remuxer = new FakeRemuxer { RemuxResult = TimeSpan.FromSeconds(12) };

        var recovered = CreateSut(remuxer).Recover(intermediate, output);

        Assert.NotNull(recovered);
        Assert.Equal(output, recovered.FilePath);
        Assert.Equal(TimeSpan.FromSeconds(12), recovered.Duration);
        Assert.True(recovered.Remuxed);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(intermediate));
    }

    [Fact]
    public void Recover_WhenRemuxFails_RenamesIntermediateToVisibleRecoveredFile()
    {
        using var folder = new TempFolder();
        var output = folder.File("a.mp4");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);
        var remuxer = new FakeRemuxer { RemuxResult = null, ReadResult = TimeSpan.FromSeconds(7) };

        var recovered = CreateSut(remuxer).Recover(intermediate, output);

        Assert.NotNull(recovered);
        Assert.Equal(folder.File("a.recovered.mp4"), recovered.FilePath);
        Assert.Equal(TimeSpan.FromSeconds(7), recovered.Duration);
        Assert.False(recovered.Remuxed);
        Assert.True(File.Exists(recovered.FilePath));
        Assert.False(File.Exists(intermediate));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Recover_WhenUnreadableDuration_ReportsZeroDurationButKeepsFile()
    {
        using var folder = new TempFolder();
        var output = folder.File("a.mp4");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);

        var recovered = CreateSut(new FakeRemuxer { RemuxResult = null, ReadResult = null }).Recover(intermediate, output);

        Assert.NotNull(recovered);
        Assert.Equal(TimeSpan.Zero, recovered.Duration);
        Assert.True(File.Exists(recovered.FilePath));
    }

    [Fact]
    public void Recover_WithoutCompleteFragment_ReturnsNullAndLeavesNothingBehind()
    {
        using var folder = new TempFolder();
        var output = folder.File("a.mp4");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        FragmentedMp4Fixture.Write(intermediate, withFragment: false);
        var remuxer = new FakeRemuxer();

        var recovered = CreateSut(remuxer).Recover(intermediate, output);

        Assert.Null(recovered);
        Assert.Empty(remuxer.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public void Recover_WhenOutputAlreadyExists_DoesNotOverwriteIt()
    {
        using var folder = new TempFolder();
        var output = folder.File("a.mp4");
        File.WriteAllText(output, "existing");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);
        var remuxer = new FakeRemuxer();

        var recovered = CreateSut(remuxer).Recover(intermediate, output);

        Assert.NotNull(recovered);
        Assert.Equal(folder.File("a.recovered.mp4"), recovered.FilePath);
        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Empty(remuxer.Calls);
    }

    [Fact]
    public void RecoverLeftovers_RecoversPreviousSessionIntermediateOnce()
    {
        using var folder = new TempFolder();
        var output = folder.File("old.mp4");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);
        File.SetLastWriteTimeUtc(intermediate, DateTime.UtcNow.AddHours(-1));
        var remuxer = new FakeRemuxer();
        var sut = CreateSut(remuxer);

        var first = sut.RecoverLeftovers(folder.Path, DateTime.UtcNow);
        var second = sut.RecoverLeftovers(folder.Path, DateTime.UtcNow);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(remuxer.Calls);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(intermediate));
    }

    [Fact]
    public void RecoverLeftovers_KeepsFailedRemuxAsRecoveredFileAndDoesNotRetry()
    {
        using var folder = new TempFolder();
        var intermediate = RecordingRecovery.GetIntermediatePath(folder.File("old.mp4"));
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);
        File.SetLastWriteTimeUtc(intermediate, DateTime.UtcNow.AddHours(-1));
        var remuxer = new FakeRemuxer { RemuxResult = null };
        var sut = CreateSut(remuxer);

        sut.RecoverLeftovers(folder.Path, DateTime.UtcNow);
        sut.RecoverLeftovers(folder.Path, DateTime.UtcNow);

        Assert.Single(remuxer.Calls);
        Assert.True(File.Exists(folder.File("old.recovered.mp4")));
        Assert.False(File.Exists(intermediate));
    }

    [Fact]
    public void RecoverLeftovers_IgnoresCurrentSessionAndOpenFiles()
    {
        using var folder = new TempFolder();
        var current = RecordingRecovery.GetIntermediatePath(folder.File("current.mp4"));
        var open = RecordingRecovery.GetIntermediatePath(folder.File("open.mp4"));
        FragmentedMp4Fixture.Write(current, withFragment: true);
        FragmentedMp4Fixture.Write(open, withFragment: true);
        File.SetLastWriteTimeUtc(open, DateTime.UtcNow.AddHours(-1));
        var remuxer = new FakeRemuxer();

        int recovered;
        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            recovered = CreateSut(remuxer).RecoverLeftovers(folder.Path, DateTime.UtcNow.AddMinutes(-5));
        }

        Assert.Equal(0, recovered);
        Assert.Empty(remuxer.Calls);
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(open));
    }

    [Fact]
    public void RecoverLeftovers_RemovesLeftoverWithNoCompleteFragment()
    {
        using var folder = new TempFolder();
        var intermediate = RecordingRecovery.GetIntermediatePath(folder.File("empty.mp4"));
        FragmentedMp4Fixture.Write(intermediate, withFragment: false);
        File.SetLastWriteTimeUtc(intermediate, DateTime.UtcNow.AddHours(-1));

        var recovered = CreateSut(new FakeRemuxer()).RecoverLeftovers(folder.Path, DateTime.UtcNow);

        Assert.Equal(0, recovered);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public void RecoverLeftovers_WhenFolderIsMissing_DoesNothing()
    {
        var recovered = CreateSut(new FakeRemuxer()).RecoverLeftovers(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), DateTime.UtcNow);

        Assert.Equal(0, recovered);
    }
}
