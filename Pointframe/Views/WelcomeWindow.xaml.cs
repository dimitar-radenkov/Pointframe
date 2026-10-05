using System.ComponentModel;
using System.Windows;
using Pointframe.Services;
using Pointframe.ViewModels;

namespace Pointframe;

public partial class WelcomeWindow : Window
{
    private readonly WelcomeViewModel _viewModel;
    private readonly ICaptureLaunchService _captureLaunch;
    private bool _closeRequested;
    private bool _isClosing;

    internal WelcomeWindow(WelcomeViewModel viewModel, ICaptureLaunchService captureLaunch)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _captureLaunch = captureLaunch;
        DataContext = viewModel;
        viewModel.CloseRequested += CloseFromViewModel;
        viewModel.CaptureRequested += CaptureFromViewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeRequested)
        {
            _closeRequested = true;
            _isClosing = true;
            _viewModel.DismissCommand.Execute(null);
            _isClosing = false;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= CloseFromViewModel;
        _viewModel.CaptureRequested -= CaptureFromViewModel;
        base.OnClosed(e);
    }

    private void CloseFromViewModel()
    {
        if (_isClosing)
        {
            return;
        }

        _closeRequested = true;
        Close();
    }

    private async void CaptureFromViewModel()
    {
        _closeRequested = true;
        Close();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        _captureLaunch.StartRegionSnip("onboarding");
    }
}
