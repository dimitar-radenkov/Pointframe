namespace Pointframe.Services;

public interface IDialogService
{
    string? PickOpenImageFile();

    string? PickSaveImageFile(string initialDirectory, string suggestedFileName);

    string? PickFolder(string initialPath, string description);

    Color? PickColor(Color initialColor);
}
