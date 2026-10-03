using System.Text.Json;

namespace Pointframe.Cli;

internal sealed class McpManagementApplication(
    IMcpPackageInstaller packageInstaller,
    IMcpClientConfigurator clientConfigurator,
    IMcpHealthChecker healthChecker,
    TextWriter standardOutput,
    TextWriter standardError)
{
    private const int SchemaVersion = 1;

    internal async Task<int> RunAsync(CliCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var response = command.McpAction switch
            {
                "install" => await InstallAsync(command, cancellationToken),
                "status" => Status(command),
                "doctor" => await DoctorAsync(command, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported MCP action '{command.McpAction}'."),
            };
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(response));
            return response.Success ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            await standardError.WriteLineAsync($"Pointframe MCP management failed: {exception.Message}");
            var response = new McpCommandResponse(
                SchemaVersion, false, command.McpAction ?? "unknown", command.McpClient ?? "unknown",
                ToErrorCode(exception), exception.Message, DryRun: command.DryRun);
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(response));
            return 1;
        }
    }

    private async Task<McpCommandResponse> InstallAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var installation = await packageInstaller.InstallLatestAsync(command.DryRun, cancellationToken);
        clientConfigurator.Configure(installation.ExecutablePath, command.DryRun);
        if (command.DryRun)
        {
            return new McpCommandResponse(
                SchemaVersion, true, "install", command.McpClient!, null,
                "Dry run succeeded; the package checksum and VS Code configuration change were validated.",
                installation.Version, installation.ExecutablePath, clientConfigurator.ConfigurationPath, DryRun: true);
        }

        var health = await healthChecker.CheckAsync(installation.ExecutablePath, cancellationToken);
        return new McpCommandResponse(
            SchemaVersion, health.Success, "install", command.McpClient!, health.Success ? null : health.Code,
            health.Success ? "Pointframe MCP was installed, configured, and verified." : health.Message,
            installation.Version, installation.ExecutablePath, clientConfigurator.ConfigurationPath, health.Tools);
    }

    private McpCommandResponse Status(CliCommand command)
    {
        var installation = packageInstaller.GetCurrent();
        if (installation is null)
        {
            return new McpCommandResponse(
                SchemaVersion, false, "status", command.McpClient!, "package_not_installed",
                "Pointframe MCP is not installed by the CLI.", ConfigurationPath: clientConfigurator.ConfigurationPath);
        }

        var configured = clientConfigurator.IsConfiguredFor(installation.ExecutablePath);
        return new McpCommandResponse(
            SchemaVersion, configured, "status", command.McpClient!, configured ? null : "client_not_configured",
            configured ? "Pointframe MCP is installed and configured for VS Code." : "Pointframe MCP is installed but VS Code is not configured for it.",
            installation.Version, installation.ExecutablePath, clientConfigurator.ConfigurationPath);
    }

    private async Task<McpCommandResponse> DoctorAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var installation = packageInstaller.GetCurrent();
        if (installation is null)
        {
            return new McpCommandResponse(
                SchemaVersion, false, "doctor", command.McpClient!, "package_not_installed",
                "Pointframe MCP is not installed by the CLI.", ConfigurationPath: clientConfigurator.ConfigurationPath);
        }

        if (!clientConfigurator.IsConfiguredFor(installation.ExecutablePath))
        {
            return new McpCommandResponse(
                SchemaVersion, false, "doctor", command.McpClient!, "client_not_configured",
                "VS Code is not configured to use the installed Pointframe MCP executable.",
                installation.Version, installation.ExecutablePath, clientConfigurator.ConfigurationPath);
        }

        var health = await healthChecker.CheckAsync(installation.ExecutablePath, cancellationToken);
        return new McpCommandResponse(
            SchemaVersion, health.Success, "doctor", command.McpClient!, health.Success ? null : health.Code,
            health.Message, installation.Version, installation.ExecutablePath, clientConfigurator.ConfigurationPath, health.Tools);
    }

    private static string ToErrorCode(Exception exception) => exception switch
    {
        HttpRequestException => "download_failed",
        InvalidDataException => "package_verification_failed",
        JsonException => "client_config_invalid",
        UnauthorizedAccessException => "access_denied",
        IOException => "io_failed",
        _ => "mcp_management_failed",
    };
}
