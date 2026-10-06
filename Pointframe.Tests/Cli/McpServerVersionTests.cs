using System.IO;
using System.Net.Http;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class McpServerVersionTests : IDisposable
{
    private readonly VerificationFixture _fixture = new();
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        _fixture.Dispose();
        foreach (var path in _paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_OlderServer_UpdatesToCliVersion()
    {
        var installedPath = CreateFile();
        var updatedPath = CreateFile();
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.GetCurrent()).Returns(new McpInstallation("1.2.3", installedPath, "install"));
        installer.Setup(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpInstallation("1.2.4", updatedPath, "install"));
        var error = new StringWriter();
        var application = CreateApplication(installer, error);

        var resolution = await application.ResolveMcpExecutableAsync(null);

        Assert.Equal(Path.GetFullPath(updatedPath), resolution.Path);
        installer.Verify(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("updated the MCP server from 1.2.3 to 1.2.4", error.ToString());
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_OlderServerUpdateFails_ReturnsVersionMismatch()
    {
        var installedPath = CreateFile();
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.GetCurrent()).Returns(new McpInstallation("1.2.3", installedPath, "install"));
        installer.Setup(item => item.InstallLatestAsync(false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var application = CreateApplication(installer, TextWriter.Null);

        var resolution = await application.ResolveMcpExecutableAsync(null);

        Assert.Null(resolution.Path);
        Assert.Equal("mcp_version_mismatch", resolution.ErrorCode);
        Assert.Contains("1.2.3", resolution.Error);
        Assert.Contains("1.2.4", resolution.Error);
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_MatchingVersion_ReturnsInstalledPathWithoutUpdate()
    {
        var installedPath = CreateFile();
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.GetCurrent()).Returns(new McpInstallation("1.2.4", installedPath, "install"));
        var application = CreateApplication(installer, TextWriter.Null);

        var resolution = await application.ResolveMcpExecutableAsync(null);

        Assert.Equal(Path.GetFullPath(installedPath), resolution.Path);
        installer.Verify(item => item.InstallLatestAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_NewerServer_ReturnsInstalledPathAndWarns()
    {
        var installedPath = CreateFile();
        var installer = new Mock<IMcpPackageInstaller>();
        installer.Setup(item => item.GetCurrent()).Returns(new McpInstallation("1.3.0", installedPath, "install"));
        var error = new StringWriter();
        var application = CreateApplication(installer, error);

        var resolution = await application.ResolveMcpExecutableAsync(null);

        Assert.Equal(Path.GetFullPath(installedPath), resolution.Path);
        installer.Verify(item => item.InstallLatestAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("newer", error.ToString());
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_ExplicitPath_ReturnsItWithoutQueryingInstaller()
    {
        var explicitPath = CreateFile();
        var installer = new Mock<IMcpPackageInstaller>();
        var application = CreateApplication(installer, TextWriter.Null);

        var resolution = await application.ResolveMcpExecutableAsync(explicitPath);

        Assert.Equal(Path.GetFullPath(explicitPath), resolution.Path);
        installer.Verify(item => item.GetCurrent(), Times.Never);
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_EnvironmentPathOlderThanCli_WarnsAndStillUsesIt()
    {
        var versionedPath = typeof(VerificationApplication).Assembly.Location;
        var installer = new Mock<IMcpPackageInstaller>();
        var services = CreateServices(installer);
        services.Environment[VerificationApplication.McpExecutableVariable] = versionedPath;
        var error = new StringWriter();
        var application = new VerificationApplication(services.Build(_fixture.Store, installer: installer.Object, verifierVersion: "Pointframe CLI 999.0.0"), TextWriter.Null, error);

        var resolution = await application.ResolveMcpExecutableAsync(null);

        Assert.Equal(Path.GetFullPath(versionedPath), resolution.Path);
        Assert.Contains($"warning: the MCP server from {VerificationApplication.McpExecutableVariable}", error.ToString());
        Assert.Contains("older than CLI 999.0.0", error.ToString());
    }

    [Fact]
    public async Task ResolveMcpExecutableAsync_EnvironmentPath_ReturnsItWithoutQueryingInstaller()
    {
        var configuredPath = CreateFile();
        var installer = new Mock<IMcpPackageInstaller>();
        var services = CreateServices(installer);
        services.Environment[VerificationApplication.McpExecutableVariable] = configuredPath;
        var application = new VerificationApplication(services.Build(_fixture.Store, installer: installer.Object, verifierVersion: "1.2.4"), TextWriter.Null, TextWriter.Null);

        var resolution = await application.ResolveMcpExecutableAsync(null);

        Assert.Equal(Path.GetFullPath(configuredPath), resolution.Path);
        installer.Verify(item => item.GetCurrent(), Times.Never);
    }

    private string CreateFile()
    {
        var path = Path.GetTempFileName();
        _paths.Add(path);
        return path;
    }

    private VerificationApplication CreateApplication(Mock<IMcpPackageInstaller> installer, TextWriter error)
    {
        var services = CreateServices(installer);
        return new VerificationApplication(services.Build(_fixture.Store, installer: installer.Object, verifierVersion: "1.2.4"), TextWriter.Null, error);
    }

    private VerificationFixture.Services CreateServices(Mock<IMcpPackageInstaller> installer)
    {
        var services = new VerificationFixture.Services();
        services.Environment.Clear();
        return services;
    }
}
