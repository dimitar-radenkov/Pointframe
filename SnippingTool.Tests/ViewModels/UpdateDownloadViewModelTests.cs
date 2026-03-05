using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class UpdateDownloadViewModelTests : IDisposable
{
    private readonly string _destPath = Path.Combine(Path.GetTempPath(), $"snip-test-{Guid.NewGuid()}.bin");

    public void Dispose()
    {
        if (File.Exists(_destPath))
        {
            File.Delete(_destPath);
        }
    }

    private static UpdateDownloadViewModel CreateVm(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK, long? contentLength = null)
    {
        var handler = new FakeHttpMessageHandler(responseBody, statusCode, contentLength);
        var http = new HttpClient(handler);
        return UpdateDownloadViewModel.CreateForTesting(http);
    }

    [Fact]
    public async Task Download_Success_SetsProgressTo100()
    {
        var vm = CreateVm("hello world", contentLength: 11);

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.Equal(100, vm.ProgressPercent);
    }

    [Fact]
    public async Task Download_Success_IsDownloadingFalse()
    {
        var vm = CreateVm("data");

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task Download_Success_IsFailedFalse()
    {
        var vm = CreateVm("data");

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.False(vm.IsFailed);
    }

    [Fact]
    public async Task Download_Success_RaisesRequestClose()
    {
        var vm = CreateVm("data");
        var closed = false;
        vm.RequestClose += () => closed = true;

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.True(closed);
    }

    [Fact]
    public async Task Download_Success_WritesFileToDisk()
    {
        const string Content = "installer-bytes";
        var vm = CreateVm(Content);

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.True(File.Exists(_destPath));
        Assert.Equal(Content, await File.ReadAllTextAsync(_destPath));
    }

    [Fact]
    public async Task Download_WithKnownContentLength_StatusTextContainsPercent()
    {
        var vm = CreateVm("hello world", contentLength: 11);
        var statusSnapshots = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText))
            {
                statusSnapshots.Add(vm.StatusText);
            }
        };

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.Contains(statusSnapshots, s => s.Contains('%'));
    }

    [Fact]
    public async Task Download_WithUnknownContentLength_StatusTextContainsKb()
    {
        var vm = CreateVm("hello world", contentLength: null);
        var statusSnapshots = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusText))
            {
                statusSnapshots.Add(vm.StatusText);
            }
        };

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.Contains(statusSnapshots, s => s.Contains("KB"));
    }

    [Fact]
    public async Task Download_ServerError_SetsFailed()
    {
        var vm = CreateVm("{}", HttpStatusCode.InternalServerError);

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.True(vm.IsFailed);
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task Download_ServerError_StatusTextIndicatesFailure()
    {
        var vm = CreateVm("{}", HttpStatusCode.InternalServerError);

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath);

        Assert.Contains("failed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_Cancellation_SetsStatusTextCancelled()
    {
        var cts = new CancellationTokenSource();
        var handler = new SlowHttpMessageHandler(onStart: () => cts.Cancel());
        var vm = UpdateDownloadViewModel.CreateForTesting(new HttpClient(handler));

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath, cts.Token);

        Assert.Contains("cancel", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task Download_Cancellation_IsFailedFalse()
    {
        var cts = new CancellationTokenSource();
        var handler = new SlowHttpMessageHandler(onStart: () => cts.Cancel());
        var vm = UpdateDownloadViewModel.CreateForTesting(new HttpClient(handler));

        await vm.DownloadAndInstallAsync("https://github.com/fake/asset.exe", _destPath, cts.Token);

        Assert.False(vm.IsFailed);
    }

    [Fact]
    public void CancelCommand_RaisesRequestClose()
    {
        var vm = UpdateDownloadViewModel.CreateForTesting(new HttpClient());
        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void InitialState_IsDownloadingTrue()
    {
        var vm = UpdateDownloadViewModel.CreateForTesting(new HttpClient());

        Assert.True(vm.IsDownloading);
        Assert.False(vm.IsFailed);
        Assert.Equal(0, vm.ProgressPercent);
    }

    private sealed class FakeHttpMessageHandler(string body, HttpStatusCode statusCode, long? contentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            HttpContent content;
            if (contentLength.HasValue)
            {
                content = new ByteArrayContent(bytes);
                content.Headers.ContentLength = contentLength;
            }
            else
            {
                // Non-seekable stream ensures no Content-Length header is computed
                content = new StreamContent(new NonSeekableStream(new MemoryStream(bytes)));
            }

            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = content });
        }
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SlowHttpMessageHandler(Action onStart) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onStart();
            await Task.Delay(1, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data"),
            };
        }
    }
}
