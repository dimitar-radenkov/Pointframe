using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Pointframe.ViewModels;

namespace Pointframe;

public partial class BeautifierWindow : Window
{
    private readonly BeautifierViewModel _vm;

    internal BeautifierWindow(BeautifierViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;

        _vm.CloseRequested += Close;
        _vm.ToastRequested += ShowToast;
    }

    internal void Initialize(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        _vm.SetSourceBitmap(bitmap);
    }

    private async void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        await Task.Delay(2000);
        Toast.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.CloseRequested -= Close;
        _vm.ToastRequested -= ShowToast;
        base.OnClosed(e);
    }
}

// Converter for RadioButton.IsChecked bound to an enum property.
// Returns true when the bound value equals the ConverterParameter.
internal sealed class EnumEqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.Equals(parameter) == true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (bool?)value == true ? parameter! : System.Windows.Data.Binding.DoNothing;
}
