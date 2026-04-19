using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests;

public sealed class CountdownWindowTests
{
    [Fact]
    public void Loaded_InitializesDisplayedCount()
    {
        StaTestHelper.Run(() =>
        {
            var window = new CountdownWindow(3, () => { });

            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            var countText = Assert.IsType<TextBlock>(window.FindName("CountText"));
            Assert.Equal("3", countText.Text);
        });
    }

    [Fact]
    public void OnTick_WhenCountdownCompletes_InvokesCompletionCallback()
    {
        StaTestHelper.Run(() =>
        {
            var completed = false;
            var window = new CountdownWindow(1, () => completed = true);

            InvokeTick(window);

            Assert.True(completed);
        });
    }

    [Fact]
    public void OnTick_WhenTimeRemains_UpdatesDisplayedCount()
    {
        StaTestHelper.Run(() =>
        {
            var window = new CountdownWindow(2, () => { });
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            InvokeTick(window);

            var countText = Assert.IsType<TextBlock>(window.FindName("CountText"));
            Assert.Equal("1", countText.Text);
        });
    }

    private static void InvokeTick(CountdownWindow window)
    {
        var onTick = typeof(CountdownWindow).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onTick);
        onTick.Invoke(window, [null, EventArgs.Empty]);
    }
}