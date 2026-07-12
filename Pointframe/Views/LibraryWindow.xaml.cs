using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Pointframe.ViewModels;

namespace Pointframe;

public partial class LibraryWindow : Window
{
    public LibraryWindow(LibraryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        ViewModel = vm;
        vm.RequestClose += Close;
        Loaded += (_, _) =>
        {
            vm.RefreshCommand.Execute(null);
            SearchBox.Focus();
        };
    }

    public LibraryViewModel ViewModel { get; }

    private void Captures_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        var container = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is null)
        {
            return;
        }

        listBox.SelectedItem = container.DataContext;
        ViewModel.OpenCommand.Execute(null);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}

public sealed class FilePathToThumbnailConverter : IValueConverter
{
    private const int ThumbnailWidth = 220;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);

            // OnLoad keeps the file unlocked; DecodePixelWidth bounds memory for large libraries.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = ThumbnailWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
