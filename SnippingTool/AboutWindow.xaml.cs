using System.Windows;
using Pointframe.ViewModels;

namespace Pointframe;

public partial class AboutWindow : Window
{
    public AboutWindow(AboutViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
    }
}
