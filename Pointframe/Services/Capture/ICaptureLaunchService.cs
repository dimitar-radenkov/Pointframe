using System.Windows;

namespace Pointframe.Services;

internal interface ICaptureLaunchService
{
    void StartRegionSnip(string source = "tray");

    void StartWholeScreenSnip(string source = "tray");

    void StartCleanWindowSnip(string source = "tray");

    void StartScrollingSnip(string source = "tray");

    void StartAutomationScrollingSnip(Int32Rect regionPixels);

    void StartWholeScreenRecord();
}
