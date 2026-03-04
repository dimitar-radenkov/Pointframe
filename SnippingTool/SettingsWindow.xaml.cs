using System.Globalization;
using System.Windows;
using System.Windows.Input;
using SnippingTool.ViewModels;
using DataFormats = System.Windows.DataFormats;
using TextBox = System.Windows.Controls.TextBox;

namespace SnippingTool;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
    }

    private void IntInput_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void IntInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            if (!int.TryParse((string)e.DataObject.GetData(DataFormats.Text), out _))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void DoubleInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox tb)
        {
            var proposed = tb.Text.Remove(tb.SelectionStart, tb.SelectionLength)
                                  .Insert(tb.SelectionStart, e.Text);
            e.Handled = !double.TryParse(proposed, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
        }
    }

    private void DoubleInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            if (!double.TryParse((string)e.DataObject.GetData(DataFormats.Text), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }
}
