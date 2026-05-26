using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pointframe.Services;
using Pointframe.ViewModels;
using DataFormats = System.Windows.DataFormats;

namespace Pointframe;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly IGlobalHotkeyService _hotkeyService;
    private bool _closedByVm;

    public SettingsWindow(SettingsViewModel vm, IGlobalHotkeyService hotkeyService)
    {
        InitializeComponent();
        _vm = vm;
        _hotkeyService = hotkeyService;
        DataContext = vm;
        vm.RequestClose += () =>
        {
            _closedByVm = true;
            Close();
        };
#if DEBUG
        UpdateIntervalComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "Every 30 seconds (Debug)",
            Tag = UpdateCheckInterval.EveryThirtySeconds,
        });
#endif
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_closedByVm)
        {
            _vm.RevertThemePreview();
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

    private void HotkeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Non-modifier keys are intercepted by the hook in capture mode.
        // This handler only fires for modifier keys — update the live display.
        e.Handled = true;
        UpdateCaptureHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void HotkeyCapture_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        UpdateCaptureHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void UpdateCaptureHotkeyCurrentInput(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        CaptureHotkeyCurrentInput.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + ?" : "—";
    }

    private void HotkeyRecordingPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            CaptureHotkeyCurrentInput.Text = "—";
            _hotkeyService.BeginKeyCaptureMode(OnCaptureHotkeyKeyPressed);
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () => Keyboard.Focus(HotkeyRecordingPanel));
        }
        else
        {
            _hotkeyService.EndKeyCaptureMode();
        }
    }

    private void OnCaptureHotkeyKeyPressed(uint vk, HotkeyModifiers modifiers)
    {
        if (vk == NativeMethods.VK_ESCAPE)
        {
            _vm.IsRecordingHotkey = false;
            return;
        }

        _vm.RegionCaptureHotkeyModifiers = modifiers;
        _vm.RegionCaptureHotkey = vk;
        _vm.IsRecordingHotkey = false;
    }

    private void RecordHotkeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Non-modifier keys are intercepted by the hook in capture mode.
        // This handler only fires for modifier keys — update the live display.
        e.Handled = true;
        UpdateRecordHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void WholeScreenRecordHotkeyRecordingPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            RecordHotkeyCurrentInput.Text = "—";
            _hotkeyService.BeginKeyCaptureMode(OnRecordHotkeyKeyPressed);
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () => Keyboard.Focus(RecordHotkeyRecordingPanel));
        }
        else
        {
            _hotkeyService.EndKeyCaptureMode();
        }
    }

    private void OnRecordHotkeyKeyPressed(uint vk, HotkeyModifiers modifiers)
    {
        if (vk == NativeMethods.VK_ESCAPE)
        {
            _vm.IsCapturingWholeScreenRecordHotkey = false;
            return;
        }

        _vm.WholeScreenRecordHotkeyModifiers = modifiers;
        _vm.WholeScreenRecordHotkey = vk;
        _vm.IsCapturingWholeScreenRecordHotkey = false;
    }

    private void RecordHotkeyCapture_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        UpdateRecordHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void UpdateRecordHotkeyCurrentInput(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        RecordHotkeyCurrentInput.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + ?" : "—";
    }

    private void ShortcutsRegionHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        UpdateShortcutsRegionHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void ShortcutsRegionHotkeyCapture_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        UpdateShortcutsRegionHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void ShortcutsRegionHotkeyRecordingPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            ShortcutsCaptureHotkeyCurrentInput.Text = "—";
            _hotkeyService.BeginKeyCaptureMode(OnCaptureHotkeyKeyPressed);
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () => Keyboard.Focus(ShortcutsRegionHotkeyRecordingPanel));
        }
        else
        {
            _hotkeyService.EndKeyCaptureMode();
        }
    }

    private void UpdateShortcutsRegionHotkeyCurrentInput(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        ShortcutsCaptureHotkeyCurrentInput.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + ?" : "—";
    }

    private void ShortcutsRecordHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        UpdateShortcutsRecordHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void ShortcutsRecordHotkeyCapture_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        UpdateShortcutsRecordHotkeyCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void ShortcutsRecordHotkeyRecordingPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            ShortcutsRecordHotkeyCurrentInput.Text = "—";
            _hotkeyService.BeginKeyCaptureMode(OnRecordHotkeyKeyPressed);
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () => Keyboard.Focus(ShortcutsRecordHotkeyRecordingPanel));
        }
        else
        {
            _hotkeyService.EndKeyCaptureMode();
        }
    }

    private void UpdateShortcutsRecordHotkeyCurrentInput(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        ShortcutsRecordHotkeyCurrentInput.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + ?" : "—";
    }

    private void OverlayShortcutCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        UpdateOverlayShortcutCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void OverlayShortcutCapture_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        UpdateOverlayShortcutCurrentInput(e.KeyboardDevice.Modifiers);
    }

    private void OverlayShortcutRecordingPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            OverlayShortcutCurrentInput.Text = "—";
            _hotkeyService.BeginKeyCaptureMode(OnOverlayShortcutKeyPressed);
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () => Keyboard.Focus(OverlayShortcutRecordingPanel));
        }
        else
        {
            _hotkeyService.EndKeyCaptureMode();
        }
    }

    private void OnOverlayShortcutKeyPressed(uint vk, HotkeyModifiers modifiers)
    {
        if (vk == NativeMethods.VK_ESCAPE)
        {
            _vm.CancelCapturingOverlayShortcutCommand.Execute(null);
            return;
        }

        _vm.ApplyOverlayShortcutCapture(vk, modifiers);
    }

    private void UpdateOverlayShortcutCurrentInput(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        OverlayShortcutCurrentInput.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + ?" : "—";
    }

    private void SectionNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContentScrollViewer?.ScrollToHome();
    }
}
