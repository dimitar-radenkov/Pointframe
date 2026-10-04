using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class PointframeCommandResolverTests
{
    [Fact]
    public void Resolve_MissingCommandReportsNoPath()
    {
        var probe = new FakeProbe(null, new PointframeCommandProbeResult(0, "Pointframe CLI 1", string.Empty));

        var result = new PointframeCommandResolver(probe).Resolve("C:\\empty");

        Assert.False(result.Ok);
        Assert.Null(result.Path);
        Assert.Contains("not found", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "Other CLI 1", false)]
    [InlineData(1, "Pointframe CLI 1", false)]
    [InlineData(0, "Pointframe CLI 1", true)]
    public void Resolve_ReportsWrongCommandExitAndTimeout(int? exitCode, string versionOutput, bool timedOut)
    {
        var probe = new FakeProbe("C:\\tools\\pointframe.exe", new PointframeCommandProbeResult(exitCode, versionOutput, "failure", timedOut));

        var result = new PointframeCommandResolver(probe).Resolve("C:\\tools");

        Assert.False(result.Ok);
        Assert.Equal("C:\\tools\\pointframe.exe", result.Path);
        if (timedOut)
        {
            Assert.Contains("timed out", result.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolve_ReportsValidVersion()
    {
        var probe = new FakeProbe("C:\\tools\\pointframe.exe", new PointframeCommandProbeResult(0, "Pointframe CLI 1.2.3", string.Empty));

        var result = new PointframeCommandResolver(probe).Resolve("C:\\tools");

        Assert.True(result.Ok);
        Assert.Equal("Pointframe CLI 1.2.3", result.Version);
    }

    private sealed class FakeProbe(string? path, PointframeCommandProbeResult result) : IPointframeCommandProbe
    {
        public string? Find(string? searchPath) => path;
        public PointframeCommandProbeResult RunVersion(string executable, TimeSpan timeout) => result;
    }
}
