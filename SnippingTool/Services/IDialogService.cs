namespace SnippingTool.Services;

public interface IDialogService
{
    string? PickFolder(string initialPath, string description);

    Color? PickColor(Color initialColor);
}
