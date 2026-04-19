using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Pointframe.Automation;

internal static class AutomationSampleFactory
{
    private const string AutomationOutputDirectoryEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OUTPUT_DIRECTORY";
    private const string SampleOverlayFileName = "automation-sample-overlay.png";

    public static (BitmapSource Bitmap, string SourcePath) CreateOpenedImageSample()
    {
        var bitmap = CreateSampleBitmap(width: 960, height: 540);
        var sourcePath = Path.Combine(GetOutputDirectory(), SampleOverlayFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        SaveBitmap(bitmap, sourcePath);
        return (bitmap, sourcePath);
    }

    public static SelectionSessionResult CreateRecordingSelectionSample()
    {
        var targetScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var monitorScale = MonitorDpiHelper.GetMonitorScale(targetScreen.Bounds.Location);
        var hostBoundsDips = MonitorDpiHelper.CalculateWindowBounds(targetScreen.Bounds, monitorScale);
        var hostBoundsPixels = new Int32Rect(
            targetScreen.Bounds.X,
            targetScreen.Bounds.Y,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height);
        var monitorSnapshot = CreateSampleBitmap(targetScreen.Bounds.Width, targetScreen.Bounds.Height);

        var selectionWidth = CalculateSelectionDimension(targetScreen.Bounds.Width, 0.45d, 320, 960);
        var selectionHeight = CalculateSelectionDimension(targetScreen.Bounds.Height, 0.35d, 240, 540);
        var selectionBoundsPixels = new Int32Rect(
            targetScreen.Bounds.X + ((targetScreen.Bounds.Width - selectionWidth) / 2),
            targetScreen.Bounds.Y + ((targetScreen.Bounds.Height - selectionHeight) / 2),
            selectionWidth,
            selectionHeight);
        var selectionRectDips = new Rect(
            (selectionBoundsPixels.X - hostBoundsPixels.X) / monitorScale,
            (selectionBoundsPixels.Y - hostBoundsPixels.Y) / monitorScale,
            selectionBoundsPixels.Width / monitorScale,
            selectionBoundsPixels.Height / monitorScale);
        var selectionBackground = CreateSelectionBackground(monitorSnapshot, hostBoundsPixels, selectionBoundsPixels);

        return new SelectionSessionResult(
            targetScreen.DeviceName,
            monitorSnapshot,
            selectionBackground,
            hostBoundsDips,
            hostBoundsPixels,
            selectionRectDips,
            selectionBoundsPixels,
            monitorScale,
            monitorScale);
    }

    private static string GetOutputDirectory()
    {
        var automationOutputDirectory = Environment.GetEnvironmentVariable(AutomationOutputDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(automationOutputDirectory))
        {
            return automationOutputDirectory;
        }

        return Path.Combine(Path.GetTempPath(), "SnippingTool.Automation");
    }

    private static int CalculateSelectionDimension(int availablePixels, double ratio, int minimumPixels, int maximumPixels)
    {
        var preferred = (int)Math.Round(availablePixels * ratio);
        var constrained = Math.Clamp(preferred, minimumPixels, maximumPixels);
        return Math.Max(1, Math.Min(Math.Max(1, availablePixels - 96), constrained));
    }

    private static BitmapSource CreateSelectionBackground(
        BitmapSource monitorSnapshot,
        Int32Rect hostBoundsPixels,
        Int32Rect selectionBoundsPixels)
    {
        var cropRect = new Int32Rect(
            selectionBoundsPixels.X - hostBoundsPixels.X,
            selectionBoundsPixels.Y - hostBoundsPixels.Y,
            selectionBoundsPixels.Width,
            selectionBoundsPixels.Height);
        var croppedBitmap = new CroppedBitmap(monitorSnapshot, cropRect);
        croppedBitmap.Freeze();
        return croppedBitmap;
    }

    private static BitmapSource CreateSampleBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * stride) + (x * 4);
                var diagonalBand = ((x / 80) + (y / 60)) % 2 == 0;

                pixels[index] = (byte)(48 + ((x * 96) / Math.Max(1, width - 1)));
                pixels[index + 1] = (byte)(72 + ((y * 104) / Math.Max(1, height - 1)));
                pixels[index + 2] = diagonalBand ? (byte)196 : (byte)128;
                pixels[index + 3] = byte.MaxValue;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
