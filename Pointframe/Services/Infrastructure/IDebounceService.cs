namespace Pointframe.Services;

public interface IDebounceService
{
    Task DebounceAsync(
        string key,
        TimeSpan delay,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}
