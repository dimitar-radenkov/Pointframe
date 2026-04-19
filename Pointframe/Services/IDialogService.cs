namespace Pointframe.Services;

public interface IDialogService
{
    string? PickOpenImageFile();

    string? PickFolder(string initialPath, string description);

    Color? PickColor(Color initialColor);
}
