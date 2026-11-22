using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class AppLoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pf-logging-" + Guid.NewGuid().ToString("N"));

    public AppLoggingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IConfiguration ShippedConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
    }

    private string[] Files() => Directory.GetFiles(_directory, "log-*.txt");

    [Fact]
    public void ShippedConfiguration_FiltersFrameworkInformationAndKeepsPointframeInformation()
    {
        var path = Path.Combine(_directory, "log-.txt");
        using (var logger = AppLogging.CreateConfiguration(ShippedConfiguration(), path, LogEventLevel.Information).CreateLogger())
        {
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Information("ef-noise");
            logger.ForContext("SourceContext", "Microsoft.Hosting.Lifetime").Information("host-noise");
            logger.ForContext("SourceContext", "System.Net.Http.HttpClient.Foo").Information("http-noise");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Warning("ef-warning");
            logger.ForContext("SourceContext", "Pointframe.Services.RecordingService").Information("pointframe-info");
        }

        var text = string.Concat(Files().Select(File.ReadAllText));
        Assert.DoesNotContain("ef-noise", text);
        Assert.DoesNotContain("host-noise", text);
        Assert.DoesNotContain("http-noise", text);
        Assert.Contains("ef-warning", text);
        Assert.Contains("pointframe-info", text);
    }

    [Fact]
    public void ShippedConfiguration_CapsFileSizeAndRetainedCount()
    {
        var config = ShippedConfiguration();

        var size = config.GetValue<long>("Logging:FileSizeLimitBytes");
        var retained = config.GetValue<int>("Logging:RetainedFileCountLimit");

        Assert.Equal(20L * 1024 * 1024, size);
        Assert.Equal(10, retained);
        Assert.True(size * retained <= 200L * 1024 * 1024);
    }

    [Fact]
    public void FileSizeLimit_RollsToNewFileInsteadOfGrowing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:FileSizeLimitBytes"] = "2000",
                ["Logging:RetainedFileCountLimit"] = "3",
            })
            .Build();
        var path = Path.Combine(_directory, "log-.txt");

        using (var logger = AppLogging.CreateConfiguration(config, path, LogEventLevel.Information).CreateLogger())
        {
            for (var i = 0; i < 200; i++)
            {
                logger.Information("line {Index} {Padding}", i, new string('x', 100));
            }
        }

        var files = Files();
        Assert.True(files.Length <= 3);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 2000 + 300));
    }

    [Theory]
    [InlineData(null, 7L)]
    [InlineData("", 7L)]
    [InlineData("20MB", 7L)]
    [InlineData("0", 7L)]
    [InlineData("-5", 7L)]
    [InlineData("1048576", 1048576L)]
    public void PositiveOrDefault_FallsBackForMissingMalformedOrNonPositiveValues(string? value, long expected)
    {
        Assert.Equal(expected, AppLogging.PositiveOrDefault(value, 7));
    }

    [Fact]
    public void PositiveOrDefault_FallsBackWhenAboveTheMaximum()
    {
        Assert.Equal(10L, AppLogging.PositiveOrDefault("3000000000", 10, int.MaxValue));
    }

    [Fact]
    public void CreateConfiguration_MalformedSizeSettings_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:FileSizeLimitBytes"] = "20MB",
                ["Logging:RetainedFileCountLimit"] = "0",
            })
            .Build();

        using var logger = AppLogging.CreateConfiguration(config, Path.Combine(_directory, "malformed-.log"), LogEventLevel.Information).CreateLogger();
        logger.Information("still logging");
    }
}
