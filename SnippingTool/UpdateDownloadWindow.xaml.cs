using System.Windows;
using SnippingTool.ViewModels;

namespace SnippingTool;

public partial class UpdateDownloadWindow : Window
{
    public UpdateDownloadWindow(UpdateDownloadViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
    }
}
