namespace Pointframe.Services;

public interface IUpdateStateService
{
    UpdateCheckResult? Current { get; }
    void Replace(UpdateCheckResult? result);
}
