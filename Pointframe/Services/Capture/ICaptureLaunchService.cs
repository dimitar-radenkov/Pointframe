namespace Pointframe.Services;

internal interface ICaptureLaunchService
{
    void StartRegionSnip(string source = "tray");

    void StartWholeScreenSnip(string source = "tray");

    void StartWholeScreenRecord();
}
