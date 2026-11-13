using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public class ImageEncodersTests
{
    [Fact]
    public void Png_HasPngFormatAndMimeType()
    {
        Assert.Equal(ImageFormat.Png.Guid, ImageEncoders.Png.FormatID);
        Assert.Equal("image/png", ImageEncoders.Png.MimeType);
    }

    [Fact]
    public void Jpeg_HasJpegFormatAndMimeType()
    {
        Assert.Equal(ImageFormat.Jpeg.Guid, ImageEncoders.Jpeg.FormatID);
        Assert.Equal("image/jpeg", ImageEncoders.Jpeg.MimeType);
    }

    // Smoke coverage only: the race is in the first concurrent lookup of a process, and other tests in this process may
    // already have filled the codec cache. The fresh-process repro is recorded in lessons.md.
    [Fact]
    public void SavePng_ConcurrentCallers_AllProduceDecodablePngs()
    {
        const int threadCount = 32;
        var results = new byte[threadCount][];
        var errors = new Exception?[threadCount];
        using var ready = new CountdownEvent(threadCount);
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, threadCount).Select(index => new Thread(() =>
        {
            try
            {
                using var bitmap = new Bitmap(8, 8);
                using var stream = new MemoryStream();
                ready.Signal();
                start.Wait();
                ImageEncoders.SavePng(bitmap, stream);
                results[index] = stream.ToArray();
            }
            catch (Exception ex)
            {
                errors[index] = ex;
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "Not every worker reached the start barrier.");
        start.Set();
        threads.ForEach(thread => thread.Join());

        Assert.All(errors, Assert.Null);
        foreach (var bytes in results)
        {
            using var stream = new MemoryStream(bytes);
            using var decoded = Image.FromStream(stream);
            Assert.Equal(ImageFormat.Png.Guid, decoded.RawFormat.Guid);
        }
    }
}
