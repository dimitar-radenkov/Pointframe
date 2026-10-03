# Capture

Selecting a region, window, or screen and acting on the bitmap: copy, save, pin, OCR, beautify, watermarks.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-01 | Region snip | Region hotkey; tray left click and "New Snip" | `Pointframe/Services/Capture/CaptureLaunchService.cs` (`StartRegionSnip`), `Pointframe/Services/Capture/SelectionSession.cs` | `snip_started`, `snip_cancelled` | `Pointframe.Tests/SelectionSessionTests.cs`, `Pointframe.Tests/Services/GlobalHotkeyServiceTests.cs`, `Pointframe.AutomationTests/Smoke/LaunchModeSmokeTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection), [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |
| F-02 | Whole-screen snip | Whole-screen hotkey; tray "Whole Screen Snip" | `Pointframe/Services/Capture/CaptureLaunchService.cs` (`StartWholeScreenSnip`) | `snip_started` | `Pointframe.Tests/Services/TrayIconManagerTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection) |
| F-03 | Clean-window snip | Clean-window hotkey; tray "Clean Window Snip" | `Pointframe/Services/Capture/CaptureLaunchService.cs` (`StartCleanWindowSnip`), `Pointframe/Services/Capture/WindowCaptureService.cs` | `snip_started` | `Pointframe.Tests/Services/TrayIconManagerTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection), [DIPs and physical pixels](../knowledge-base.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| F-04 | Capture delay countdown | Capture delay setting, applied to every snip | `Pointframe/Services/Capture/CaptureLaunchService.cs`, `Pointframe/Views/CountdownWindow.xaml.cs` | `capture_delay_used` | `Pointframe.Tests/CountdownWindowTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection), [User settings](settings.md#user-settings) |
| F-05 | Open an image in the overlay | Tray "Open Image..." | `Pointframe/App.xaml.cs` (`HandleOpenImageRequested`), `Pointframe/Services/Capture/OpenedImageBitmapCapture.cs` | `open_image_used` | `Pointframe.Tests/Services/OpenedImageBitmapCaptureTests.cs`, `Pointframe.AutomationTests/Smoke/TrayOpenImageSmokeTests.cs`, `Pointframe.AutomationTests/Smoke/OpenedImageOverlaySmokeTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection) |
| F-06 | Copy, save, save as | Overlay action bar `Copy`, `SaveAs` (and compact variants) | `Pointframe/ViewModels/OverlayViewModel.cs` (`Copy`, `Save`, `SaveAs`), `Pointframe/Services/Infrastructure/ActivationTelemetryService.cs` | `capture_completed`, `first_capture_completed` | `Pointframe.Tests/ViewModels/OverlayViewModelTests.cs`, `Pointframe.Tests/Services/OverlayBitmapCaptureTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection), [Telemetry](telemetry.md#telemetry-pipeline) |
| F-07 | Pin a screenshot | Overlay action bar `Pin` | `Pointframe/ViewModels/OverlayViewModel.cs` (`Pin`), `Pointframe/Views/PinnedScreenshotWindow.xaml.cs` | `capture_pinned` | `Pointframe.Tests/PinnedScreenshotWindowTests.cs`, `Pointframe.AutomationTests/Smoke/McpPinWorkflowTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection) |
| F-08 | Copy text with an OCR lasso | Overlay copy-text action | `Pointframe/ViewModels/OverlayViewModel.cs` (`CopyText`), `Pointframe/Services/Annotation/OcrLassoController.cs`, `Pointframe/Services/Infrastructure/WindowsOcrService.cs` | `ocr_attempted`, `ocr_used`, `ocr_no_text` | `Pointframe.Tests/Services/WindowsOcrServiceTests.cs` | [Capture overlay and selection](#capture-overlay-and-selection) |
| F-09 | Beautify a screenshot | Overlay beautify action | `Pointframe/ViewModels/BeautifierViewModel.cs`, `Pointframe/Views/BeautifierWindow.xaml.cs`, `Pointframe/Services/Recording/BeautifierRenderService.cs` | `beautify_opened`, `screenshot_beautified`, `screenshot_beautified_copied` | — | [Capture overlay and selection](#capture-overlay-and-selection) |
| F-10 | Watermarks on screenshots and videos | Watermark settings | `Pointframe/Services/Recording/ScreenshotWatermarkService.cs`, `Pointframe/Services/Recording/WatermarkTokenResolver.cs` | — | `Pointframe.Tests/Services/ScreenshotWatermarkServiceLayoutTests.cs`, `Pointframe.Tests/Services/WatermarkTokenResolverTests.cs` | [User settings](settings.md#user-settings), [Recording pipeline](recording.md#recording-pipeline) |

## Capture overlay and selection

**Responsibility.** Turn a user gesture into a selected region and a bitmap, then host the annotation surface and the action toolbar (copy, save, pin, record, OCR, beautify). Everything screenshot-shaped starts here; recording branches off the same selection.

**Entry points.**

| Trigger | Path |
|---|---|
| Hotkey or tray menu | `ICaptureLaunchService.StartRegionSnip`, `StartWholeScreenSnip`, `StartCleanWindowSnip`, `StartWholeScreenRecord`; the `source` argument (`"hotkey"` or `"tray"`) feeds telemetry |
| Open an image file | `OpenImageRequestedMessage` through the event aggregator; mode `OpenedImage` |
| Library item | `LibraryViewModel` closes the library, then launches the overlay with the file |

**Flow.**

1. `SelectionSession.SelectAsync` creates one `SelectionMonitorWindow` per `Screen.AllScreens`, each with its own snapshot from `IScreenCaptureService` and its own scale from `MonitorDpiHelper`. The first window to complete wins and closes the rest. Result: `SelectionSessionResult` with pixel bounds and the owning monitor.
2. `OverlayWindow` is resolved from DI (transient) and initialized from the session result. Its bounds are assigned before `Show()`; see the PerMonitorV2 lesson.
3. `OverlayViewModel` moves through phases (selecting, annotating, recording) and exposes the toolbar commands. It reads DPI from `PresentationSource` in `OnSourceInitialized`.
4. Output: `IOverlayBitmapCapture` renders the annotated result (`OverlayBitmapCapture` for live captures, `OpenedImageBitmapCapture` for opened files). Copy goes through `IClipboardService`, save through `IImageFileService`, pin opens `PinnedScreenshotWindow`, beautify opens `BeautifierWindow`.
5. OCR: `OcrLassoController` collects a lasso region and `IOcrService` (`WindowsOcrService`, Windows.Media.Ocr) extracts the text.

`SelectionSessionMode` values: `Region`, `FullScreen`, `OpenedImage`, `WindowClean`. `WindowClean` captures the window under the cursor through `IWindowCaptureService` after the configured capture delay, so a tray launch gives the user time to move off the menu.

**Key types.**

- `OverlayWindow` is split into partial files by concern: `Selection`, `Layout`, `Recording`, `RecordingAnnotation`, `RecordingHud`, `ColorPicker`. Put new code in the partial that owns the concern.
- `OverlayToolbarLayoutHelper` decides toolbar placement, including the compact fallback for small selections.
- `DpiAwarenessScope` switches the thread DPI context. The virtual-desktop-wide selection runs system-aware; recording hosts stay PerMonitorV2.

**Invariants.**

- Hide the overlay and yield the dispatcher before capturing the screen, or the overlay lands in the bitmap.
- Assign window bounds before `Show()`, never inside HWND-lifecycle callbacks.
- Do not show a replacement window (pin, beautifier, library) until the overlay has fully closed.
- Convert coordinates per monitor; see [DPI coordinate systems](../knowledge-base.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor). Never divide Win32 screen coordinates by a single overlay DPI.

**Tests.** `Pointframe.Tests/ViewModels/OverlayViewModelTests.cs`, `Pointframe.Tests/OverlayWindowLayoutTests.cs`, `Pointframe.Tests/OverlayWindowInteractionTests.cs`, `Pointframe.Tests/OverlayToolbarLayoutTests.cs`, `Pointframe.Tests/SelectionSessionTests.cs`, `Pointframe.Tests/SelectionMonitorWindowTests.cs`, `Pointframe.Tests/PinnedScreenshotWindowTests.cs`, `Pointframe.Tests/Services/ScreenCaptureServiceTests.cs`, `Pointframe.Tests/Services/OverlayBitmapCaptureTests.cs`, `Pointframe.Tests/Services/WindowsOcrServiceTests.cs`. Automation: `Pointframe.AutomationTests/Smoke/OpenedImageOverlaySmokeTests.cs`, `Pointframe.AutomationTests/Smoke/TrayOpenImageSmokeTests.cs`.

**Files.** `Pointframe/Services/Capture/CaptureLaunchService.cs`, `Pointframe/Services/Capture/SelectionSession.cs`, `Pointframe/Models/SelectionSessionMode.cs`, `Pointframe/Models/SelectionSessionResult.cs`, `Pointframe/Views/SelectionMonitorWindow.cs`, `Pointframe/Views/SelectionBackdropWindow.cs`, `Pointframe/Views/OverlayWindow.xaml.cs`, `Pointframe/Views/OverlayWindow.Selection.cs`, `Pointframe/Views/OverlayWindow.Layout.cs`, `Pointframe/Views/OverlayToolbarLayoutHelper.cs`, `Pointframe/ViewModels/OverlayViewModel.cs`, `Pointframe/Services/Capture/ScreenCaptureService.cs`, `Pointframe/Services/Capture/WindowCaptureService.cs`, `Pointframe/Services/Capture/OverlayBitmapCapture.cs`, `Pointframe/Services/Capture/OpenedImageBitmapCapture.cs`, `Pointframe/Services/Annotation/OcrLassoController.cs`, `Pointframe/Services/Infrastructure/WindowsOcrService.cs`, `Pointframe/Views/PinnedScreenshotWindow.xaml.cs`, `Pointframe/Views/BeautifierWindow.xaml.cs`, `Pointframe/Native/MonitorDpiHelper.cs`, `Pointframe/Native/DpiAwarenessScope.cs`.

**Lessons.**

- Lesson: WPF PerMonitorV2: set window bounds before Show(), not in OnSourceInitialized
- Lesson: Full-desktop selection overlays are safer in a system-aware DPI context while monitor-scoped recording hosts stay PerMonitorV2
- Lesson: Overlay capture must yield the dispatcher after hiding the overlay window
- Lesson: Pin capture must not restore the live overlay before the overlay window closes
- Lesson: Replacement windows should not be shown until the full-screen overlay has fully closed
- Lesson: Active-window capture must map Win32 screen coordinates into overlay space instead of dividing by one overlay DPI
- Lesson: Opened-image overlay layout must target a single monitor, not the full virtual desktop
- Lesson: Window picker overlays must enumerate capturable windows before showing any picker UI
- Lesson: Cursor-targeted tray captures must honor capture delay
- Lesson: Selection-adjacent toolbars need a compact fallback for small snips
