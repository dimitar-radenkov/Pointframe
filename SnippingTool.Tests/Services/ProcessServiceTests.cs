using System.Diagnostics;
using System.IO;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class ProcessServiceTests : IDisposable
{
    private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"snip-process-{Guid.NewGuid():N}.txt");

    [Fact]
    public void Start_LaunchesConfiguredProcess()
    {
        var sut = new ProcessService();
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c echo coverage> \"{_outputPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        sut.Start(startInfo);

        SpinWait.SpinUntil(() => File.Exists(_outputPath), millisecondsTimeout: 3000);
        Assert.True(File.Exists(_outputPath));
    }

    public void Dispose()
    {
        if (File.Exists(_outputPath))
        {
            File.Delete(_outputPath);
        }
    }
}