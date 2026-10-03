namespace Pointframe.Services;

internal interface IScrollInputService
{
    IDisposable BeginScrolling(int screenX, int screenY);

    void ScrollDown(int wheelNotches);
}
