namespace Pointframe.Services;

internal interface ICaptureLaunchService
{
    void StartRegionSnip(string source = "tray");

    void StartWholeScreenSnip(string source = "tray");

    bool StartMonitorSnip(string monitorName, string source = "agent", string? agentOperationId = null);

    void StartCleanWindowSnip(string source = "tray");

    void StartWholeScreenRecord();
}
