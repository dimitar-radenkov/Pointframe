namespace SnippingTool.Services;

public interface IMessageBoxService
{
    void ShowInformation(string message, string title);
    void ShowWarning(string message, string title);
    void ShowError(string message, string title);
    bool Confirm(string message, string title);
}
