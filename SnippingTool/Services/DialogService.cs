using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace SnippingTool.Services;

public sealed class DialogService : IDialogService
{
    public string? PickOpenImageFile()
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Open image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };

        using var owner = CreateDialogOwner();
        var result = dialog.ShowDialog(owner);

        return result == Forms.DialogResult.OK
            ? dialog.FileName
            : null;
    }

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

    private static DialogOwner CreateDialogOwner()
    {
        var existingOwner = GetOwnerWindowHandle();
        if (existingOwner != IntPtr.Zero)
        {
            return new ExistingDialogOwner(existingOwner);
        }

        return TemporaryDialogOwner.Create();
    }

    private static IntPtr GetOwnerWindowHandle()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return IntPtr.Zero;
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsActive)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }
        }

        foreach (Window window in application.Windows)
        {
            if (window.IsVisible)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    private abstract class DialogOwner : Forms.IWin32Window, IDisposable
    {
        public abstract IntPtr Handle { get; }

        public virtual void Dispose()
        {
        }
    }

    private sealed class ExistingDialogOwner : DialogOwner
    {
        public ExistingDialogOwner(IntPtr handle)
        {
            Handle = handle;
        }

        public override IntPtr Handle { get; }
    }

    private sealed class TemporaryDialogOwner : DialogOwner
    {
        private readonly Window _window;

        private TemporaryDialogOwner(Window window)
        {
            _window = window;
            Handle = new WindowInteropHelper(window).Handle;
        }

        public override IntPtr Handle { get; }

        public static TemporaryDialogOwner Create()
        {
            var window = new Window
            {
                Width = 1,
                Height = 1,
                Left = -10000,
                Top = -10000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true,
                Opacity = 0
            };

            window.Show();
            window.Activate();

            return new TemporaryDialogOwner(window);
        }

        public override void Dispose()
        {
            _window.Close();
        }
    }
}
