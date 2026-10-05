using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ShareServiceTests
{
    private sealed class CapturingLogger : ILogger<ShareService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private static UserSettings Settings(string url = "https://upload.example/api", string path = "url") => new()
    {
        ShareDestinationUrl = url,
        ShareFileFieldName = "capture",
        ShareResponseLinkPath = path,
        ShareTimeoutSeconds = 30,
    };

    private static ShareService Service(
        UserSettings settings,
        FakeHandler handler,
        CapturingLogger? logger = null)
    {
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(settings);
        return new ShareService(new HttpClient(handler), settingsMock.Object, logger ?? new CapturingLogger());
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static bool ContainsSequence(byte[] source, byte[] value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.AsSpan(index, value.Length).SequenceEqual(value))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task UploadAsync_SendsPngBytesFieldNameAndProtectedHeaders()
    {
        var pngBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 4, 5, 6 };
        byte[]? requestBody = null;
        string? contentType = null;
        string? authorization = null;
        var settings = Settings();
        settings.ShareHeaders = [ShareHeaderProtection.Protect("Authorization", "Bearer do-not-log-this")];
        using var http = new HttpClient(new FakeHandler(async (request, _) =>
        {
            requestBody = await request.Content!.ReadAsByteArrayAsync();
            contentType = request.Content.Headers.ContentType?.MediaType;
            authorization = request.Headers.Authorization?.ToString();
            return Response(HttpStatusCode.OK, "{\"url\":\"https://share.example/capture/1\"}");
        }));
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(settings);
        var service = new ShareService(http, settingsMock.Object, new CapturingLogger());

        var result = await service.UploadAsync(pngBytes);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://share.example/capture/1", result.Link);
        Assert.Equal("multipart/form-data", contentType);
        Assert.Equal("Bearer do-not-log-this", authorization);
        var multipartText = Encoding.UTF8.GetString(requestBody!);
        Assert.Contains("name=capture", multipartText);
        Assert.Contains("capture.png", multipartText);
        Assert.True(ContainsSequence(requestBody!, pngBytes));
    }

    [Fact]
    public async Task UploadAsync_ExtractsTopLevelJsonLink()
    {
        var service = Service(Settings(), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"url\":\"https://share.example/top\"}"))));

        var result = await service.UploadAsync([1, 2, 3]);

        Assert.Equal("https://share.example/top", result.Link);
    }

    [Fact]
    public async Task UploadAsync_ExtractsNestedJsonLink()
    {
        var service = Service(Settings(path: "data.link"), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"data\":{\"link\":\"https://share.example/nested\"}}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal("https://share.example/nested", result.Link);
    }

    [Fact]
    public async Task UploadAsync_ExtractsLinkThroughNestedArrayPath()
    {
        var service = Service(Settings(path: "files.0.url"), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"files\":[{\"url\":\"https://share.example/array\"}]}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal("https://share.example/array", result.Link);
    }

    [Fact]
    public async Task UploadAsync_ArrayIndexOutOfRange_IsUnparseable()
    {
        var service = Service(Settings(path: "files.1.url"), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"files\":[{\"url\":\"https://share.example/array\"}]}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.UnparseableResponse, result.Failure);
    }

    [Fact]
    public async Task UploadAsync_NumericSegmentOnObject_IsPropertyName()
    {
        var service = Service(Settings(path: "files.0.url"), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"files\":{\"0\":{\"url\":\"https://share.example/object\"}}}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal("https://share.example/object", result.Link);
    }

    [Fact]
    public async Task UploadAsync_ArrayAtEndOfPath_IsUnparseable()
    {
        var service = Service(Settings(path: "files"), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"files\":[{\"url\":\"https://share.example/array\"}]}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.UnparseableResponse, result.Failure);
    }

    [Fact]
    public async Task UploadAsync_AcceptsPlainHttpsUrlBody()
    {
        var service = Service(Settings(), new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("https://share.example/plain"),
        })));

        var result = await service.UploadAsync([1]);

        Assert.Equal("https://share.example/plain", result.Link);
    }

    [Fact]
    public async Task UploadAsync_RejectsNonHttpsReturnedLink()
    {
        var service = Service(Settings(), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"url\":\"http://share.example/plain\"}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.LinkNotHttps, result.Failure);
    }

    [Fact]
    public async Task UploadAsync_InvalidDestination_ReturnsInvalidUrlWithoutSending()
    {
        var sent = false;
        var service = Service(Settings("http://uploads.example/share"), new FakeHandler((_, _) =>
        {
            sent = true;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.InvalidUrl, result.Failure);
        Assert.False(sent);
    }

    [Theory]
    [InlineData("http://localhost:5050/share")]
    [InlineData("http://127.0.0.1:5050/share")]
    [InlineData("http://[::1]:5050/share")]
    public async Task UploadAsync_AllowsHttpLoopbackDestination(string destination)
    {
        var service = Service(Settings(destination), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"url\":\"https://share.example/loopback\"}"))));

        var result = await service.UploadAsync([1]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task UploadAsync_JsonLinkThatIsNotAnAbsoluteUrl_IsUnparseable()
    {
        var service = Service(Settings(), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{\"url\":\"relative/path\"}"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.UnparseableResponse, result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UploadAsync_HttpError_ReturnsStatusFailure(HttpStatusCode statusCode)
    {
        var service = Service(Settings(), new FakeHandler((_, _) => Task.FromResult(Response(statusCode, "private response body"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.HttpStatus, result.Failure);
        Assert.Equal((int)statusCode, result.StatusCode);
    }

    [Fact]
    public async Task UploadAsync_Timeout_ReturnsTimeoutFailure()
    {
        var settings = Settings();
        settings.ShareTimeoutSeconds = 1;
        var service = Service(settings, new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response(HttpStatusCode.OK, "{}");
        }));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task UploadAsync_CallerCancellation_ReturnsCancelledFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = Service(Settings(), new FakeHandler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));

        var result = await service.UploadAsync([1], cancellation.Token);

        Assert.Equal(ShareFailure.Cancelled, result.Failure);
    }

    [Fact]
    public async Task UploadAsync_MalformedJson_ReturnsUnparseableResponse()
    {
        var service = Service(Settings(), new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{not json"))));

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.UnparseableResponse, result.Failure);
    }

    [Fact]
    public async Task UploadAsync_FailureLogDoesNotContainDestinationQueryHeaderOrBody()
    {
        var settings = Settings("https://upload.example/api?query-secret=private");
        settings.ShareHeaders = [ShareHeaderProtection.Protect("Authorization", "header-secret")];
        var logger = new CapturingLogger();
        var service = Service(settings, new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden, "body-secret"))), logger);

        var result = await service.UploadAsync([1]);

        Assert.Equal(ShareFailure.HttpStatus, result.Failure);
        var loggedText = string.Join("\n", logger.Messages);
        Assert.DoesNotContain("query-secret", loggedText);
        Assert.DoesNotContain("header-secret", loggedText);
        Assert.DoesNotContain("body-secret", loggedText);
        Assert.Contains("403", loggedText);
    }
}
