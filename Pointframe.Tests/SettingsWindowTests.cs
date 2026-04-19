using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests;

public sealed class SettingsWindowTests
{
    [Fact]
    public void DoubleInput_PreviewTextInput_RejectsInvalidCharacters()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            var textBox = new TextBox { Text = "1.2", SelectionStart = 3, SelectionLength = 0 };
            var args = CreateTextInputArgs(textBox, "x");

            InvokePrivateHandler(window, "DoubleInput_PreviewTextInput", textBox, args);

            Assert.True(args.Handled);
        });
    }

    [Fact]
    public void DoubleInput_PreviewTextInput_AllowsValidCharacters()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            var textBox = new TextBox { Text = "1.2", SelectionStart = 3, SelectionLength = 0 };
            var args = CreateTextInputArgs(textBox, "5");

            InvokePrivateHandler(window, "DoubleInput_PreviewTextInput", textBox, args);

            Assert.False(args.Handled);
        });
    }

    [Fact]
    public void DoubleInput_Pasting_CancelsInvalidPaste()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            var args = new DataObjectPastingEventArgs(new DataObject("abc"), false, DataFormats.Text);

            InvokePrivateHandler(window, "DoubleInput_Pasting", window, args);

            Assert.True(args.CommandCancelled);
        });
    }

    [Fact]
    public void DoubleInput_Pasting_AllowsValidPaste()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            var args = new DataObjectPastingEventArgs(new DataObject("1.5"), false, DataFormats.Text);

            InvokePrivateHandler(window, "DoubleInput_Pasting", window, args);

            Assert.False(args.CommandCancelled);
        });
    }

    [Fact]
    public void HotkeyCapture_PreviewKeyDown_Escape_CancelsRecording()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow(out var viewModel);
            viewModel.IsRecordingHotkey = true;
            var args = CreateKeyArgs(Key.Escape);

            InvokePrivateHandler(window, "HotkeyCapture_PreviewKeyDown", window, args);

            Assert.True(args.Handled);
            Assert.False(viewModel.IsRecordingHotkey);
        });
    }

    [Fact]
    public void HotkeyCapture_PreviewKeyDown_StoresNewHotkey()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow(out var viewModel);
            viewModel.IsRecordingHotkey = true;
            var args = CreateKeyArgs(Key.A);

            InvokePrivateHandler(window, "HotkeyCapture_PreviewKeyDown", window, args);

            Assert.True(args.Handled);
            Assert.Equal((uint)KeyInterop.VirtualKeyFromKey(Key.A), viewModel.RegionCaptureHotkey);
            Assert.False(viewModel.IsRecordingHotkey);
        });
    }

    [Fact]
    public void HotkeyCapture_PreviewKeyDown_IgnoresModifierKeys()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow(out var viewModel);
            viewModel.IsRecordingHotkey = true;
            viewModel.RegionCaptureHotkey = 0x2C;
            var args = CreateKeyArgs(Key.LeftShift);

            InvokePrivateHandler(window, "HotkeyCapture_PreviewKeyDown", window, args);

            Assert.True(args.Handled);
            Assert.Equal(0x2Cu, viewModel.RegionCaptureHotkey);
            Assert.True(viewModel.IsRecordingHotkey);
        });
    }

    [Fact]
    public void HotkeyRecordingPanel_IsVisibleChanged_WhenVisible_FocusesPanel()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            var panel = (StackPanel)window.FindName("HotkeyRecordingPanel");
            Assert.NotNull(panel);
            var args = new DependencyPropertyChangedEventArgs(UIElement.IsVisibleProperty, false, true);

            InvokePrivateHandler(window, "HotkeyRecordingPanel_IsVisibleChanged", panel!, args);

            Assert.True(panel!.Focusable);
        });
    }

    [Fact]
    public void SettingsWindow_ContainsSectionNavigationAndResetActions()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow();
            window.Show();
            window.UpdateLayout();

            Assert.NotNull(FindByAutomationId<ListBox>(window, "SettingsWindow.SectionNavigation"));
            Assert.NotNull(FindByAutomationId<Button>(window, "SettingsWindow.ResetCurrentSection"));
            Assert.NotNull(FindByAutomationId<Button>(window, "SettingsWindow.RestoreDefaults"));

            window.Close();
        });
    }

    [Fact]
    public void SectionNavigation_SelectedValue_UpdatesSelectedSection()
    {
        StaTestHelper.Run(() =>
        {
            var window = CreateWindow(out var viewModel);
            window.Show();
            window.UpdateLayout();

            var navigation = FindByAutomationId<ListBox>(window, "SettingsWindow.SectionNavigation");
            Assert.NotNull(navigation);

            navigation!.SelectedValue = SettingsSection.App;

            Assert.Equal(SettingsSection.App, viewModel.SelectedSection);

            window.Close();
        });
    }

    private static SettingsWindow CreateWindow(out SettingsViewModel viewModel)
    {
        var settings = new UserSettings { DefaultAnnotationColor = "#FFFF0000" };
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(settings);
        viewModel = new SettingsViewModel(
            settingsMock.Object,
            Mock.Of<IThemeService>(),
            Mock.Of<IDialogService>(),
            Mock.Of<IMicrophoneDeviceService>(service =>
                service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic", "USB Mic" } &&
                service.GetDefaultCaptureDeviceName() == "Studio Mic"));
        return new SettingsWindow(viewModel);
    }

    private static SettingsWindow CreateWindow() => CreateWindow(out _);

    private static void InvokePrivateHandler(object target, string methodName, object sender, object args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, [sender, args]);
    }

    private static TextCompositionEventArgs CreateTextInputArgs(TextBox textBox, string text)
    {
        var composition = new TextComposition(InputManager.Current, textBox, text);
        return new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
        };
    }

    private static KeyEventArgs CreateKeyArgs(Key key)
    {
        var source = new HwndSource(new HwndSourceParameters("SettingsWindowTests")
        {
            Width = 1,
            Height = 1,
        });

        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
    }

    private static T? FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T typed && AutomationProperties.GetAutomationId(typed) == automationId)
            {
                return typed;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        return null;
    }
}