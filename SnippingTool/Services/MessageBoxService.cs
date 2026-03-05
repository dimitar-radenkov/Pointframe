using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace SnippingTool.Services;

public sealed class MessageBoxService : IMessageBoxService
{
    public void ShowInformation(string message, string title) =>
        WpfMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title) =>
        WpfMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title) =>
        WpfMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title) =>
        WpfMessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
}
