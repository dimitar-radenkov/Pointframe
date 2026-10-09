namespace Pointframe.Services;

public sealed class UpdateStateService : IUpdateStateService
{
    public UpdateCheckResult? Current { get; private set; }

    public void Replace(UpdateCheckResult? result)
    {
        Current = result?.IsUpdateAvailable == true ? result : null;
    }
}
