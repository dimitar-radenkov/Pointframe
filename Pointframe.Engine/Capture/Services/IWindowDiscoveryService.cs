namespace Pointframe.Engine;

public interface IWindowDiscoveryService
{
    IReadOnlyList<WindowDescriptor> GetWindows();

    WindowDescriptor? GetWindow(long hwnd);
}
