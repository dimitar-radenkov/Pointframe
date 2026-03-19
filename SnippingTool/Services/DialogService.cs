using Forms = System.Windows.Forms;

namespace SnippingTool.Services;

public sealed class DialogService : IDialogService
{
    public string? PickFolder(string initialPath, string description)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            SelectedPath = initialPath
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    public Color? PickColor(Color initialColor)
    {
        using var dialog = new Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(initialColor.A, initialColor.R, initialColor.G, initialColor.B),
            FullOpen = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return null;
        }

        var selectedColor = dialog.Color;
        return Color.FromArgb(selectedColor.A, selectedColor.R, selectedColor.G, selectedColor.B);
    }
}
