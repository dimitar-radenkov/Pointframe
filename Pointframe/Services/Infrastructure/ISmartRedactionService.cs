namespace Pointframe.Services;

internal interface ISmartRedactionService
{
    Task<IReadOnlyList<SmartRedactionSuggestion>> DetectAsync(
        BitmapSource bitmap,
        CancellationToken cancellationToken = default);
}
