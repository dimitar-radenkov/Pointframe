namespace Pointframe.Services;

public enum ShareFailure
{
    None,
    NotConfigured,
    InvalidUrl,
    HttpStatus,
    Timeout,
    Cancelled,
    UnparseableResponse,
    LinkNotHttps,
    RequestFailed,
}

public sealed record ShareResult(string? Link, ShareFailure Failure, int? StatusCode = null)
{
    public bool IsSuccess => Failure == ShareFailure.None && Link is not null;

    public static ShareResult Success(string link) => new(link, ShareFailure.None);

    public static ShareResult Failed(ShareFailure failure, int? statusCode = null) => new(null, failure, statusCode);
}

public interface IShareService
{
    Task<ShareResult> UploadAsync(byte[] pngBytes, CancellationToken cancellationToken = default);
}
