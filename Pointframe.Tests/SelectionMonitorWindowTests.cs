using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests;

[Collection("OverlayWindowUi")]
public sealed class SelectionMonitorWindowTests
{
    [Fact]
    public void Constructor_InitializesWindowAndSelectionUi()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                Assert.Equal(nameof(SelectionMonitorWindow), window.Title);
                Assert.Equal(10d, window.Left);
                Assert.Equal(20d, window.Top);
                Assert.Equal(320d, window.Width);
                Assert.Equal(180d, window.Height);
                Assert.Equal(Cursors.Cross, window.Cursor);

                var root = Assert.IsType<Canvas>(window.Content);
                Assert.Equal(3, root.Children.Count);

                var selectionBorder = GetPrivateField<System.Windows.Shapes.Rectangle>(window, "_selectionBorder");
                var sizeLabelBorder = GetPrivateField<Border>(window, "_sizeLabelBorder");
                Assert.Equal(Visibility.Collapsed, selectionBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, sizeLabelBorder.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CreateSelectionRect_UsesMinAndAbsoluteDimensions()
    {
        var rect = (Rect)InvokePrivateStatic(
            typeof(SelectionMonitorWindow),
            "CreateSelectionRect",
            new Point(30, 40),
            new Point(10, 5));

        Assert.Equal(10d, rect.X);
        Assert.Equal(5d, rect.Y);
        Assert.Equal(20d, rect.Width);
        Assert.Equal(35d, rect.Height);
    }

    [Fact]
    public void GetScreenPixelBounds_AppliesHostOffsetAndScale()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                var bounds = (Int32Rect)InvokePrivate(window, "GetScreenPixelBounds", new Rect(12.5, 8.5, 20, 10));

                Assert.Equal(125, bounds.X);
                Assert.Equal(217, bounds.Y);
                Assert.Equal(40, bounds.Width);
                Assert.Equal(20, bounds.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GetScreenPixelBounds_EnforcesMinimumPixelSize()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                var bounds = (Int32Rect)InvokePrivate(window, "GetScreenPixelBounds", new Rect(0, 0, 0.1, 0.1));

                Assert.Equal(1, bounds.Width);
                Assert.Equal(1, bounds.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CreateSelectionBackground_CropsExpectedRegion()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                var background = (BitmapSource)InvokePrivate(window, "CreateSelectionBackground", new Int32Rect(120, 220, 24, 18));

                Assert.Equal(24, background.PixelWidth);
                Assert.Equal(18, background.PixelHeight);
                Assert.True(background.IsFrozen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UpdateSelectionVisual_UpdatesBorderAndLabel()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                InvokePrivate(window, "UpdateSelectionVisual", new Point(10, 12), new Point(34, 28));

                var selectionBorder = GetPrivateField<System.Windows.Shapes.Rectangle>(window, "_selectionBorder");
                var sizeLabelText = GetPrivateField<TextBlock>(window, "_sizeLabelText");
                var sizeLabelBorder = GetPrivateField<Border>(window, "_sizeLabelBorder");

                Assert.Equal(10d, Canvas.GetLeft(selectionBorder));
                Assert.Equal(12d, Canvas.GetTop(selectionBorder));
                Assert.Equal(24d, selectionBorder.Width);
                Assert.Equal(16d, selectionBorder.Height);
                Assert.Equal("48×32", sizeLabelText.Text);
                Assert.Equal(10d, Canvas.GetLeft(sizeLabelBorder));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OnPreviewKeyDown_Escape_RaisesSelectionCanceled()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                var canceled = false;
                window.SelectionCanceled += () => canceled = true;

                var args = CreateKeyArgs(Key.Escape);
                InvokeProtected(window, "OnPreviewKeyDown", args);

                Assert.True(canceled);
                Assert.True(args.Handled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OnPreviewKeyDown_NonEscape_DoesNotRaiseSelectionCanceled()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            try
            {
                var canceled = false;
                window.SelectionCanceled += () => canceled = true;

                var args = CreateKeyArgs(Key.Enter);
                InvokeProtected(window, "OnPreviewKeyDown", args);

                Assert.False(canceled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static SelectionMonitorWindow CreateWindow()
    {
        return new SelectionMonitorWindow(
            "DISPLAY1",
            CreateBitmap(320, 180),
            new Rect(10, 20, 320, 180),
            new Int32Rect(100, 200, 640, 360),
            2d,
            2d);
    }

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static KeyEventArgs CreateKeyArgs(Key key)
    {
        var source = new HwndSource(new HwndSourceParameters("SelectionMonitorWindowTests")
        {
            Width = 1,
            Height = 1,
        });

        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
    }

    private static object InvokePrivateStatic(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, args)!;
    }

    private static object InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, args)!;
    }

    private static void InvokeProtected(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field.GetValue(target)!;
    }
}
