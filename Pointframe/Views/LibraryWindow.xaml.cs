using System.Globalization;
using System.Windows;
using System.Windows.Data;
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
