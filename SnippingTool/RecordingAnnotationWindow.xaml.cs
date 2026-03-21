using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;

namespace SnippingTool;

public partial class RecordingAnnotationWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    private readonly RecordingAnnotationViewModel _vm;
    private readonly ILogger<RecordingAnnotationWindow> _logger;
    private readonly IScreenCaptureService _screenCapture;
    private readonly AnnotationCanvasRenderer _renderer;
    private readonly AnnotationCanvasInteractionController _interactionController;
    private readonly IEventSubscription _undoSubscription;
    private readonly IEventSubscription _redoSubscription;
    private IntPtr _hwnd;

    public bool IsInputArmed => _vm.IsInputArmed;
    public RecordingAnnotationViewModel ViewModel => _vm;

    public RecordingAnnotationWindow(
        RecordingAnnotationViewModel vm,
        Rect regionRect,
        IScreenCaptureService screenCapture,
        IEventAggregator eventAggregator,
        ILoggerFactory loggerFactory)
    {
        _vm = vm;
        _logger = loggerFactory.CreateLogger<RecordingAnnotationWindow>();
        _screenCapture = screenCapture;

        InitializeComponent();
        DataContext = _vm;

        _renderer = new AnnotationCanvasRenderer(
            AnnotationCanvas,
            _vm,
            element => _vm.TrackElement(element),
            loggerFactory.CreateLogger<AnnotationCanvasRenderer>(),
            UpdateAnnotationStateFromCanvas,
            CaptureLiveBlurSource);
        _interactionController = new AnnotationCanvasInteractionController(
            AnnotationCanvas,
            _vm,
            _renderer,
            UpdateAnnotationStateFromCanvas);
        _undoSubscription = eventAggregator.Subscribe<UndoGroupMessage>(HandleUndoGroupAsync);
        _redoSubscription = eventAggregator.Subscribe<RedoGroupMessage>(HandleRedoGroupAsync);

        Left = regionRect.Left;
        Top = regionRect.Top;
        Width = regionRect.Width;
        Height = regionRect.Height;

        RootCanvas.Width = regionRect.Width;
        RootCanvas.Height = regionRect.Height;
        AnnotationCanvas.Width = regionRect.Width;
        AnnotationCanvas.Height = regionRect.Height;

        RootCanvas.MouseLeftButtonDown += RootCanvas_MouseLeftButtonDown;
        RootCanvas.MouseMove += RootCanvas_MouseMove;
        RootCanvas.MouseLeftButtonUp += RootCanvas_MouseLeftButtonUp;
        KeyDown += RecordingAnnotationWindow_KeyDown;
        _vm.ClearRequested += HandleClearRequested;
    }

    public bool ToggleInputMode()
    {
        SetInputArmed(!IsInputArmed);
        return IsInputArmed;
    }

    public void SetInputArmed(bool isInputArmed)
    {
        if (_vm.IsInputArmed == isInputArmed)
        {
            return;
        }

        if (!isInputArmed && HasActiveEditor())
        {
            _logger.LogInformation("Recording annotation input remains armed because an editor is active");
            return;
        }

        if (!isInputArmed)
        {
            CancelCurrentShape();
        }

        _vm.SetInputArmed(isInputArmed);
        ApplyHitTestMode();

        if (_vm.IsInputArmed)
        {
            Activate();
            Focus();
        }

        _logger.LogInformation("Recording annotation input mode changed: {IsInputArmed}", _vm.IsInputArmed);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyHitTestMode();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.ClearRequested -= HandleClearRequested;
        _undoSubscription.Dispose();
        _redoSubscription.Dispose();
        base.OnClosed(e);
    }

    private void ApplyHitTestMode()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // Keep the overlay layered but toggle transparency so the same window can
        // switch between visible-passive and visible-interactive modes at runtime.
        var style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        style |= WS_EX_LAYERED;

        if (IsInputArmed)
        {
            style &= ~WS_EX_TRANSPARENT;
        }
        else
        {
            style |= WS_EX_TRANSPARENT;
        }

        SetWindowLong(_hwnd, GWL_EXSTYLE, style);
    }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsInputArmed)
        {
            return;
        }

        _interactionController.HandlePointerDown(e.GetPosition(AnnotationCanvas));
        e.Handled = true;
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.IsInputArmed)
        {
            return;
        }

        _interactionController.HandlePointerMove(e.GetPosition(AnnotationCanvas));
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsInputArmed)
        {
            return;
        }

        _interactionController.HandlePointerUp(e.GetPosition(AnnotationCanvas));
        e.Handled = true;
    }

    private void RecordingAnnotationWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetInputArmed(false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            if (_vm.UndoCommand.CanExecute(null))
            {
                _vm.UndoCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void CancelCurrentShape()
    {
        _interactionController.Cancel();
    }

    private void HandleClearRequested()
    {
        CancelCurrentShape();
        AnnotationCanvas.Children.Clear();
        UpdateAnnotationStateFromCanvas();
    }

    private ValueTask HandleUndoGroupAsync(UndoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            AnnotationCanvas.Children.Remove(element);
        }

        UpdateAnnotationStateFromCanvas();
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRedoGroupAsync(RedoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            AnnotationCanvas.Children.Add(element);
        }

        UpdateAnnotationStateFromCanvas();
        return ValueTask.CompletedTask;
    }

    private void UpdateAnnotationStateFromCanvas()
    {
        var numberCount = AnnotationCanvas.Children
            .OfType<FrameworkElement>()
            .Count(element => element.Tag is "number");
        _vm.SyncAnnotationState(AnnotationCanvas.Children.Count > 0, numberCount);
    }

    private bool HasActiveEditor() => AnnotationCanvas.Children.OfType<TextBox>().Any();

    private BitmapSource? CaptureLiveBlurSource(BlurShapeParameters parameters)
    {
        var screenX = (int)Math.Round(Left + parameters.Left);
        var screenY = (int)Math.Round(Top + parameters.Top);
        var screenWidth = Math.Max(1, (int)Math.Round(parameters.Width));
        var screenHeight = Math.Max(1, (int)Math.Round(parameters.Height));

        var previousVisibility = Visibility;
        try
        {
            Visibility = Visibility.Hidden;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var bitmap = _screenCapture.Capture(screenX, screenY, screenWidth, screenHeight);
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }
        finally
        {
            Visibility = previousVisibility;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            ApplyHitTestMode();
        }
    }
}
