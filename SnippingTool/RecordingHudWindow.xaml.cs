using System.Windows;
using SnippingTool.Services;
using SnippingTool.ViewModels;

namespace SnippingTool;

public partial class RecordingHudWindow : Window
{
    private readonly RecordingHudViewModel _vm;
    private readonly Rect _regionRect;
    private readonly IUserSettingsService _settings;

    public RecordingHudWindow(
        RecordingHudViewModel vm,
        Rect regionRect,
        IUserSettingsService settings)
    {
        _vm = vm;
        _regionRect = regionRect;
        _settings = settings;
        DataContext = vm;
        InitializeComponent();
        _vm.StopCompleted += () => Dispatcher.Invoke(() =>
        {
            if (IsLoaded)
            {
                Close();
            }
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _vm.StartElapsedTimer();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RepositionHud();
    }

    // Extracted for unit testability.
    internal static (double Left, double Top) ComputePosition(
        Rect region, double hudWidth, double hudHeight, Rect workArea, int gapPixels = 8)
    {
        var left = Math.Max(workArea.Left, Math.Min(region.Left + (region.Width - hudWidth) / 2, workArea.Right - hudWidth));
        var top = Math.Min(region.Bottom + gapPixels, workArea.Bottom - hudHeight);
        return (left, top);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.CancelElapsedTimer();
        base.OnClosed(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsLoaded)
        {
            RepositionHud();
        }
    }

    private void SavedText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.OpenOutputFolderCommand.Execute(null);
    }

    private void ToolButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag }
            && _vm.SelectToolCommand.CanExecute(tag))
        {
            _vm.SelectToolCommand.Execute(tag);
        }
    }

    private void RepositionHud()
    {
        var (left, top) = ComputePosition(_regionRect, ActualWidth, ActualHeight, SystemParameters.WorkArea, _settings.Current.HudGapPixels);
        Left = left;
        Top = top;
    }
}
