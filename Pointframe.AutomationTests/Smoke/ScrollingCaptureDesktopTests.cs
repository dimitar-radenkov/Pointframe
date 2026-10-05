using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class ScrollingCaptureDesktopTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public ScrollingCaptureDesktopTests(DesktopAutomationFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void ScrollingSnip_CapturesAllFixtureRowsInOrder()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        RunCapture(cancel: false);
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void ScrollingSnip_EscapeSavesPartialCaptureAndShowsCancellationNotice()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        RunCapture(cancel: true);
    }

    private void RunCapture(bool cancel)
    {
        _fixture.SeedSettings(autoSaveScreenshots: true);
        var statePath = Path.Combine(_fixture.OutputDirectory, "scroll-fixture-state.json");
        using var fixtureProcess = new FixtureProcessLease(StartFixture(statePath));
        var region = WaitForFixtureRegion(statePath);
        var appPath = Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE")!;
        var regionArgument = string.Join(',', region.X, region.Y, region.Width, region.Height);

        using var app = AutomationApp.LaunchExecutable(
            appPath,
            $"--automation-start-scrolling-snip={regionArgument}",
            _fixture.CreateEnvironmentVariables());

        if (cancel)
        {
            WaitForProgressFrame(app);
            Keyboard.Press(VirtualKeyShort.ESC);
        }

        app.SwitchToTopLevelWindow(AutomationIds.OverlayWindowRoot);
        if (cancel)
        {
            var notice = app.FindRequiredElement("OverlayWindow.CaptureNotice");
            Assert.Contains("Capture canceled", notice.Name, StringComparison.OrdinalIgnoreCase);
        }

        app.ClickFirstButton(AutomationIds.OverlayWindowCopy, AutomationIds.OverlayWindowCompactCopy);
        var imagePath = WaitForPng(_fixture.ScreenshotOutputPath);
        using var image = new Bitmap(imagePath);

        var fixtureState = File.ReadAllText(statePath);
        Assert.True(image.Height > region.Height, $"Expected stitched height > {region.Height}, got {image.Height}. Fixture state: {fixtureState}");
        var rows = ReadEncodedRows(image);
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Order().Distinct(), rows);
        if (!cancel)
        {
            Assert.True(Enumerable.Range(0, 40).SequenceEqual(rows), $"Rows: {string.Join(',', rows)}. Fixture: {fixtureState}; output size: {image.Width}x{image.Height}");
        }
        else
        {
            Assert.InRange(rows.Count, 1, 39);
        }
    }

    private static void WaitForProgressFrame(AutomationApp app)
    {
        using var automation = new UIA3Automation();
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                var progressWindow = automation.GetDesktop()
                    .FindAllChildren(criteria => criteria.ByProcessId(app.Application.ProcessId))
                    .FirstOrDefault(window => window.AutomationId == "ScrollingCaptureProgressWindow.Root");
                var status = progressWindow?.FindFirstDescendant(
                    criteria => criteria.ByAutomationId("ScrollingCaptureProgressWindow.Status"));
                if (status is not null && Regex.IsMatch(status.Name, @"^Captured (?:[2-9]|[1-9]\d+) frames"))
                {
                    return;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("Scrolling capture did not report at least two captured frames within 15 seconds.");
    }

    private static Process StartFixture(string statePath)
    {
        var fixturePath = ResolveFixtureExecutable();
        var startInfo = new ProcessStartInfo(fixturePath, "--scroll-fixture")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(fixturePath)!,
        };
        startInfo.Environment["POINTFRAME_FIXTURE_STATE_PATH"] = statePath;
        startInfo.Environment["POINTFRAME_FIXTURE_MONITOR"] = "0";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the scrolling fixture.");
    }

    private sealed class FixtureProcessLease(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string ResolveFixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Pointframe.DesktopTestFixture", "bin", "Release", "net10.0-windows10.0.18362.0", "Pointframe.DesktopTestFixture.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Build Pointframe.DesktopTestFixture in Release before running this desktop test.");
    }

    private static Rectangle WaitForFixtureRegion(string statePath)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(statePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                var values = document.RootElement.GetProperty("scrollSurfaceScreen").GetString()!.Split(',');
                if (values.Length == 4)
                {
                    return new Rectangle(
                        int.Parse(values[0], CultureInfo.InvariantCulture),
                        int.Parse(values[1], CultureInfo.InvariantCulture),
                        int.Parse(values[2], CultureInfo.InvariantCulture),
                        int.Parse(values[3], CultureInfo.InvariantCulture));
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("The scrolling fixture did not report its viewport bounds.");
    }

    private static string WaitForPng(string directory)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var image = Directory.GetFiles(directory, "*.png").FirstOrDefault();
            if (image is not null)
            {
                try
                {
                    using var stream = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.None);
                    return image;
                }
                catch (IOException)
                {
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("The scrolling capture was not saved to the automation output directory.");
    }

    private static List<int> ReadEncodedRows(Bitmap image)
    {
        var rows = new List<int>();
        int? previous = null;
        var encodedColors = Enumerable.Range(0, 40)
            .ToDictionary(
                index => Color.FromArgb((index * 37) % 256, (index * 71) % 256, (index * 113) % 256).ToArgb(),
                index => index);
        for (var y = 0; y < image.Height; y++)
        {
            var pixel = image.GetPixel(image.Width / 2, y).ToArgb();
            if (!encodedColors.TryGetValue(pixel, out var index))
            {
                continue;
            }

            if (index != previous)
            {
                rows.Add(index);
                previous = index;
            }
        }

        return rows;
    }
}
