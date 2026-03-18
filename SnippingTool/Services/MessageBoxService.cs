using System.Runtime.InteropServices;
using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace SnippingTool.Services;

public sealed class MessageBoxService : IMessageBoxService
{
    private const uint MbOk = 0x00000000;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbTaskModal = 0x00002000;
    private const uint MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    public void ShowInformation(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            return WpfMessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
        }

        return ShowNative(message, title, MbYesNo | MbIconInformation) == IdYes;
    }

    private static void Show(string message, string title, MessageBoxButton button, MessageBoxImage image)
    {
        var owner = GetOwnerWindow();
        if (owner is not null)
        {
            WpfMessageBox.Show(owner, message, title, button, image);
            return;
        }

        _ = ShowNative(message, title, MapButton(button) | MapImage(image));
    }

    private static Window? GetOwnerWindow()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return null;
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsActive)
            {
                return window;
            }
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsVisible)
            {
                return window;
            }
        }

        return null;
    }

    private static uint MapButton(MessageBoxButton button) =>
        button switch
        {
            MessageBoxButton.YesNo => MbYesNo,
            _ => MbOk,
        };

    private static uint MapImage(MessageBoxImage image) =>
        image switch
        {
            MessageBoxImage.Error => MbIconError,
            MessageBoxImage.Warning => MbIconWarning,
            _ => MbIconInformation,
        };

    private static int ShowNative(string message, string title, uint type) =>
        ShowMessageBox(IntPtr.Zero, message, title, type | MbTaskModal | MbSetForeground);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type);
}
