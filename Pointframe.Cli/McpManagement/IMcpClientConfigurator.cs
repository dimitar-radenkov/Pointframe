namespace Pointframe.Cli;

internal interface IMcpClientConfigurator
{
    string ConfigurationPath { get; }

    void Configure(string executablePath, bool dryRun);

    bool IsConfiguredFor(string executablePath);
}
