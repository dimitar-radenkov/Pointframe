# Planned Features

Features identified through competitive analysis against Snagit, ShareX, Greenshot, Windows Snipping Tool, and PicPick.

---

## Completed

| # | Feature | Version |
|---|---------|---------|
| 1 | Blur / Pixelate Tool | v2.3 |
| 2 | Pin Screenshot to Screen | v2.3 |
| 3 | MP4 Recording Output | v2.3 |
| 4 | OCR — Copy Text from Screenshot | v2.4 |

---

## 5. Pixel-Accurate Selection Magnifier

**Priority:** Medium–High  
**Category:** Capture UX

While dragging a region selection, display a small zoomed loupe (≈4× magnification, ~120×120 px) near the cursor showing the exact pixels under the current selection edge. Eliminates the need to re-snip due to 1–2 px misalignment.

**Implementation sketch:**
- Take a full-screen `BitmapSource` snapshot in `OnSourceInitialized` (before the overlay is visible).
- During `Root_MouseMove` (Phase: Selecting), crop a small region centred on the cursor using `CroppedBitmap` and display it in a `LoupeBorder` / `LoupeImage` element on the `Root` canvas.
- Hide the loupe in `Root_MouseUp` and release the snapshot in `TransitionToAnnotating()`.
- No ViewModel or service changes needed — pure view logic.

→ See [selection-magnifier.md](selection-magnifier.md) for the full step-by-step implementation plan.

---

## 6. Crop Tool in Preview

**Priority:** Medium  
**Category:** Annotation / Editing

After capture, the user can crop the image by dragging handles on any edge. Avoids the need to re-snip when the captured region is slightly too large.

**Implementation sketch:**
- Add `CropCommand` / crop mode to `PreviewViewModel`.
- Overlay four edge handles on the `PreviewWindow` canvas.
- On commit, crop the underlying `BitmapSource` using `CroppedBitmap` and replace the displayed image.
- Push the operation onto the undo stack.

---

## 7. Screenshot History

**Priority:** Medium  
**Category:** Workflow

A persistent list of captured screenshots (thumbnails + timestamps) accessible from the tray icon. Lets users retrieve a screenshot taken earlier in the session without opening File Explorer.

**Implementation sketch:**
- Maintain a JSON index file alongside the screenshots folder.
- Add a `HistoryWindow` with an `ItemsControl` of thumbnails bound to a `HistoryViewModel`.
- On open: load the index, display the 20 most recent entries, allow re-open or copy.
- Respect the existing `ScreenshotSavePath` setting.

---

## 8. Custom Hotkey Configuration

**Priority:** Medium  
**Category:** Settings / Accessibility

Allow users to assign different keyboard shortcuts per capture mode (region snip, full screen, active window, record). Currently hard-coded to `Print Screen`.

**Implementation sketch:**
- Add hotkey fields to `UserSettings` (e.g., `RegionCaptureHotkey`, `FullScreenHotkey`).
- Add a hotkey-capture control to `SettingsWindow` (listen for the next key press and display it).
- Update `App.xaml.cs` `KeyboardHookCallback` to read from settings instead of the hard-coded `VK_PRINTSCREEN`.

---

## 9. GIF Export for Recordings

**Priority:** Medium  
**Category:** Recording / Sharing

Export short screen recordings as animated GIFs for direct sharing in chat tools (Slack, Teams, GitHub comments). GIFs require no video player and display inline everywhere.

**Implementation sketch:**
- Add a "Save as GIF" option to the stop/save flow in `RecordingHudWindow`.
- Use `AnimatedGifEncoder` (open-source, MIT) or encode via `System.Windows.Media.Imaging.GifBitmapEncoder` frame-by-frame.
- Apply an optional palette quantiser for file-size control.
- Consider a 30-second / configurable cap to prevent enormous files.

---

## 10. Freeform (Lasso) Capture

**Priority:** Low  
**Category:** Capture

Allow drawing an irregular polygon or freehand path as the capture boundary, matching the freeform mode present in the Windows built-in Snipping Tool since Vista.

**Implementation sketch:**
- Add a `FreeformCapture` mode to `OverlayViewModel.Phase`.
- Track mouse points into a `Polyline` during drag.
- On mouse-up, close the path and compute its bounding `Rect`; capture that rect and apply a `GeometryClip` (the drawn path) to mask the resulting `BitmapSource`.

---

## 11. Open & Annotate Existing Image

**Priority:** High  
**Category:** Workflow  
**Demand:** 175 👍 on Flameshot (#240) — highest single data point in competitive research

Allow users to open an existing image file directly into the annotation overlay instead of taking a new screenshot. Eliminates the current need to open an external editor for images already on disk.

**Implementation sketch:**
- Add "Open image…" to the tray context menu and as a drag-and-drop target on any overlay window.
- On file selection, load the image as a `BitmapSource` and pass it to `OverlayViewModel` the same way a captured screenshot is today.
- Skip the selection phase; go straight to `Phase.Annotating`.
- Reuse the full existing annotation and copy/save pipeline unchanged.

---

## 12. Scrolling / Full-Page Capture

**Priority:** High  
**Category:** Capture  
**Demand:** 168 👍 on Flameshot (#1130); chronic ShareX pain point; present in PicPick and Snagit

Capture a scrollable window (browser tab, document, long chat) by stitching multiple frames taken while auto-scrolling, producing one tall composite image.

**Implementation sketch:**
- Add a "Scrolling capture" mode that, once a window is selected, sends `WM_SCROLL` messages or `SendInput` scroll events, taking a screenshot at each step.
- Detect scroll end (repeated identical frame).
- Stitch frames using `DrawingVisual` / `RenderTargetBitmap`, aligning on the overlap region with a simple pixel-diff comparison.
- Render the result into the standard annotation overlay.

---

## 13. Window / Active-App Capture

**Priority:** Medium–High  
**Category:** Capture  
**Demand:** 120 👍 on Flameshot (#5) with two duplicate issues

Snap a specific window or the currently focused application without manually drawing a selection rectangle. Hovering over a window highlights it; clicking captures exactly that window's bounds.

**Implementation sketch:**
- During the selection phase, enumerate top-level windows via `EnumWindows` P/Invoke and highlight the one under the cursor using a coloured border.
- On click, read the window rect with `GetWindowRect` and commit that as the `SelectionRect`.
- Feed the rect straight into `CommitSelection()` — no changes to the annotation or save pipeline.

---

## 14. Include Cursor in Screenshot

**Priority:** Medium  
**Category:** Capture  
**Demand:** 107 👍 on Flameshot (#604); common request in software documentation workflows

Optionally overlay the current cursor graphic onto the screenshot at the exact position it was when the capture was triggered.

**Implementation sketch:**
- Add a `IncludeCursor` bool to `UserSettings` and a toggle in `SettingsWindow`.
- In `ScreenCaptureService`, after capturing the bitmap, retrieve the current cursor image and position via `GetCursorInfo` / `GetIconInfo` P/Invoke.
- Draw the cursor onto the `RenderTargetBitmap` using `DrawingContext.DrawImage` before returning the result.

---

## 15. Callout / Speech Bubble Annotations

**Priority:** Medium  
**Category:** Annotation  
**Demand:** Present in Snagit, CleanShot X, PicPick, and ShareX (#7278); top annotation gap vs. Snagit

Add a callout/speech-bubble shape with a tail pointing to a subject and an editable text body — the most common annotation type in software tutorials and documentation.

**Implementation sketch:**
- Add `AnnotationTool.Callout` to the enum.
- Add `CalloutShapeParameters(Rect Bubble, Point Tail, string Text, Color Fill, Color Stroke, double Thickness)` sealed record to `ShapeParameters.cs`.
- Render as a rounded rectangle + triangle tail in `AnnotationCanvasRenderer`.
- Embed a `TextBox` inside the bubble during drag (same pattern as the existing Text tool) and convert to `TextBlock` on commit.

---

## 16. Movable Annotations After Placement

**Priority:** Medium  
**Category:** Annotation  
**Demand:** 40 👍 on Flameshot (#272); top UX frustration — currently only undo is available

Allow users to click-and-drag a previously placed annotation to reposition it without undoing and redrawing.

**Implementation sketch:**
- In `AnnotationCanvasRenderer`, after committing a shape, attach `MouseLeftButtonDown` / `MouseMove` / `MouseLeftButtonUp` handlers to the root `UIElement`.
- On mouse-down, hit-test the canvas children; if a committed shape is hit, enter a "move" drag state tracking the delta.
- On mouse-up, update the corresponding `ShapeParameters` record (new position) and push a move operation onto the undo stack.

---

## 17. Repeat Last Capture Region

**Priority:** Medium  
**Category:** Capture  
**Demand:** 48 👍 on Flameshot (#417); productivity feature for iterative documentation

Re-trigger a capture using the exact same pixel rectangle as the previous one — useful when annotating a UI that changes state, or for periodic monitoring.

**Implementation sketch:**
- Persist the last `SelectionRect` and DPI in `UserSettings` (or in-memory singleton).
- Add a "Repeat last capture" tray menu item and an optional second hotkey binding.
- On activation, skip the selection phase entirely and jump straight to `Phase.Annotating` with the stored rect.

---

## 18. Webcam Overlay in Recordings

**Priority:** Medium  
**Category:** Recording  
**Demand:** High demand among tutorial/course creators; present in ShareX and Loom

Show a draggable picture-in-picture webcam feed on top of the recording region, letting presenters keep their face visible while demonstrating software.

**Implementation sketch:**
- Use `Windows.Media.Capture.MediaCapture` (WinRT) or `AForge.Video.DirectShow` to pull a webcam frame at recording FPS.
- Composite the webcam frame onto each captured screen frame in `ScreenRecordingService` before passing it to `IVideoWriter`.
- Add `UserSettings` fields: `WebcamEnabled`, `WebcamDeviceId`, `WebcamSize`, `WebcamCorner`.
- Expose a device picker and toggle in `SettingsWindow`.

---

## 19. Windows Share API

**Priority:** Medium  
**Category:** Sharing  
**Demand:** Core feature of ShareX and Greenshot; Windows 11 "Share" charm is the OS-native way to send to Slack, Teams, Mail, etc.

Add a "Share…" button in the annotation toolbar that invokes the Windows Share sheet, letting users send the screenshot to any registered share target (Slack, Teams, Outlook, OneDrive, etc.) without leaving the tool.

**Implementation sketch:**
- Call `Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI()` via WinRT interop.
- Set a `DataPackage` with a `BitmapImage` and optional title/description.
- Wire a "Share" `RelayCommand` in `OverlayViewModel` alongside the existing Copy and Pin commands.

---

## 20. Zoom in Annotation Editor

**Priority:** Medium  
**Category:** Annotation UX  
**Demand:** Top ShareX enhancement request (#2325); important for precise annotation on high-resolution screenshots

Let users zoom into the annotation canvas to place arrows, text, and shapes with pixel-level accuracy on large or dense screenshots.

**Implementation sketch:**
- Apply a `ScaleTransform` on the annotation `Canvas` driven by `Ctrl+Scroll` or +/- buttons in the toolbar.
- Maintain the logical shape coordinates in the original pixel space so exported bitmaps are unaffected by zoom level.
- Add scroll bars (or panning via `Space`+drag) when the scaled canvas exceeds the overlay bounds.

---

## 21. Curved / Bidirectional Arrows

**Priority:** Low–Medium  
**Category:** Annotation  
**Demand:** Greenshot issue #311, ShareX issue #4227; present in Snagit and PicPick

Extend the existing arrow tool with a double-headed variant (arrow at both ends) and a curved arrow that bends around UI elements.

**Implementation sketch:**
- Add `ArrowStyle` enum (`Single`, `Double`, `Curved`) to `ArrowShapeParameters`.
- For `Double`: add a second arrowhead at `P1` in `AnnotationGeometryService.GetArrowHeadGeometry`.
- For `Curved`: replace the straight `Line` with a `BezierSegment` whose control point is the midpoint offset perpendicular to the line; expose a drag handle at the midpoint during editing.

---

## 22. WebP / AVIF Export

**Priority:** Low–Medium  
**Category:** Output  
**Demand:** 117+ 👍 combined across ShareX issues #6090 and #5250; modern web-optimised formats

Support saving screenshots as WebP or AVIF in addition to PNG and JPEG, producing files 25–50 % smaller with equivalent quality — useful for web developers.

**Implementation sketch:**
- Add `ImageFormat` enum values (`Png`, `Jpeg`, `WebP`, `Avif`) to `UserSettings`.
- Use `ImageMagick.NET` (MIT) or the `Windows.Graphics.Imaging.BitmapEncoder` WinRT API (supports WebP natively on Windows 11+) for encoding.
- Expose the format choice in `SettingsWindow` next to the existing save-path controls.

---

## 23. HDR Screenshot Support

**Priority:** Low  
**Category:** Capture  
**Demand:** 117 👍, 215 💬 on ShareX (#6688); growing importance as HDR monitors become mainstream

Capture and save screenshots that preserve HDR colour data (10-bit / scRGB) rather than tonemapping to SDR 8-bit, preventing washed-out colours on HDR displays.

**Implementation sketch:**
- Replace the current `Graphics.CopyFromScreen` path with `Windows.Graphics.Capture` (WinRT), which can return an HDR `Direct3D11CaptureFrame` in `B8G8R8A8` or `R16G16B16A16Float` format.
- Save as JXL or HDR PNG using `Windows.Graphics.Imaging.BitmapEncoder` with the appropriate pixel format.
- Gate the feature on a `UserSettings.HdrCapture` toggle; fall back to existing SDR path when disabled or on SDR displays.

---

## Summary Table

| # | Feature | Priority | Effort | Dependencies |
|---|---|---|---|---|
| 1 | Blur / Pixelate tool | High | Low | None |
| 2 | Pin screenshot to screen | High | Low | None |
| 3 | MP4 recording output | High | Medium | FFMpegCore or WinRT MF |
| 4 | OCR copy text | High | Low | `Windows.Media.Ocr` (built-in) |
| 5 | Selection magnifier | Medium–High | Medium | None — pure view, `CroppedBitmap` only |
| 6 | Crop in preview | Medium | Medium | None |
| 7 | Screenshot history | Medium | Medium | None |
| 8 | Custom hotkeys | Medium | Medium | None |
| 9 | GIF export | Medium | Medium | GIF encoder |
| 10 | Freeform capture | Low | High | None |
| 11 | Open & annotate existing image | High | Low | None |
| 12 | Scrolling / full-page capture | High | High | `SendInput` P/Invoke, frame stitching |
| 13 | Window / active-app capture | Medium–High | Medium | `EnumWindows` / `GetWindowRect` P/Invoke |
| 14 | Include cursor in screenshot | Medium | Low | `GetCursorInfo` P/Invoke |
| 15 | Callout / speech bubble annotations | Medium | Medium | None |
| 16 | Movable annotations after placement | Medium | Medium | None |
| 17 | Repeat last capture region | Medium | Low | None |
| 18 | Webcam overlay in recordings | Medium | High | `MediaCapture` WinRT or AForge |
| 19 | Windows Share API | Medium | Low | WinRT `DataTransferManager` |
| 20 | Zoom in annotation editor | Medium | Medium | None |
| 21 | Curved / bidirectional arrows | Low–Medium | Medium | None |
| 22 | WebP / AVIF export | Low–Medium | Low | `Windows.Graphics.Imaging` or ImageMagick |
| 23 | HDR screenshot support | Low | High | `Windows.Graphics.Capture` WinRT |
