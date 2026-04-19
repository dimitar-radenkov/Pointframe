using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Moq;
using Pointframe.Services;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class UpdateDownloadWindowServiceTests : IDisposable
{
    private readonly string _destPath = Path.Combine(Path.GetTempPath(), $"snip-update-{Guid.NewGuid():N}.bin");

    [Fact]
    public void ShowAsync_WhenDownloadCompletes_ReturnsTrue()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var process = new Mock<IProcessService>();
            var sut = new UpdateDownloadWindowService(
                () => CreateViewModel(process.Object, "payload"),
                CreateWindow);

            var result = await sut.Show("https://example.invalid/update.exe", _destPath);

            Assert.True(result);
            Assert.True(File.Exists(_destPath));
            process.Verify(service => service.Start(It.IsAny<System.Diagnostics.ProcessStartInfo>()), Times.Once);
        });
    }

    [Fact]
    public void ShowAsync_WhenWindowClosesBeforeDownloadStarts_ReturnsFalse()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var process = new Mock<IProcessService>();
            var sut = new UpdateDownloadWindowService(
                () => CreateViewModel(process.Object, "payload"),
                vm =>
                {
                    var window = CreateWindow(vm);
                    window.Loaded += (_, _) => window.Close();
                    return window;
                });

            var result = await sut.Show("https://example.invalid/update.exe", _destPath);

            Assert.False(result);
            process.Verify(service => service.Start(It.IsAny<System.Diagnostics.ProcessStartInfo>()), Times.Never);
        });
    }

    public void Dispose()
    {
        if (File.Exists(_destPath))
        {
            File.Delete(_destPath);
        }
    }

    private static UpdateDownloadViewModel CreateViewModel(IProcessService process, string responseBody)
    {
        var http = new HttpClient(new FakeHttpMessageHandler(responseBody));
        return new UpdateDownloadViewModel(http, process);
    }

    private static UpdateDownloadWindow CreateWindow(UpdateDownloadViewModel vm)
    {
        return new UpdateDownloadWindow(vm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };
    }

    private sealed class FakeHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentLength = bytes.Length;
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}