# Recording

Recording a region or screen to MP4 with a HUD, microphone, live annotation, redaction, and post-processing (trim, GIF).

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-15 | Region recording | Overlay action bar `Record` | `Pointframe/Views/OverlayWindow.Recording.cs`, `Pointframe/Services/Recording/ScreenRecordingService.cs`, `Pointframe/Services/Recording/FFMpegVideoWriter.cs` | `recording_started`, `recording_completed`, `first_recording_completed`, `ffmpeg_missing` | `Pointframe.Tests/OverlayWindowRecordingFlowTests.cs`, `Pointframe.Tests/Services/ScreenRecordingServiceTests.cs`, `Pointframe.AutomationTests/Smoke/RecordingOverlaySmokeTests.cs` | [Recording pipeline](#recording-pipeline), [D-003](#d-003-recording-uses-one-authoritative-session-geometry), [Even dimensions](#recording-width-and-height-are-even) |
| F-16 | Whole-screen recording | Whole-screen record hotkey | `Pointframe/Services/Capture/CaptureLaunchService.cs` (`StartWholeScreenRecord`) | `recording_started` | `Pointframe.Tests/Services/GlobalHotkeyServiceTests.cs` | [Recording pipeline](#recording-pipeline) |
| F-17 | Recording HUD: pause, stop, minimize, expand | HUD `PauseResume`, `Stop`, `MinimizeHud`, `ExpandHud` | `Pointframe/ViewModels/RecordingHudViewModel.cs`, `Pointframe/Services/Recording/RecordingHudCoordinator.cs` | `recording_hud_stopped`, `recording_hud_pause_toggled`, `recording_hud_display_mode_changed` | `Pointframe.Tests/ViewModels/RecordingHudViewModelTests.cs`, `Pointframe.Tests/RecordingHudPositionTests.cs`, `Pointframe.AutomationTests/Smoke/RecordingHudInteractionTests.cs` | [Recording pipeline](#recording-pipeline), [DIPs and physical pixels](../knowledge-base.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| F-18 | Microphone audio | HUD `ToggleMicrophone`; microphone setting | `Pointframe/Services/Recording/RecordingMicrophoneSession.cs`, `Pointframe/Services/Infrastructure/MicrophoneDeviceService.cs` | `recording_hud_microphone_toggled`, `microphone_unavailable` | `Pointframe.Tests/Services/RecordingMicrophoneSessionTests.cs`, `Pointframe.Tests/Services/MicrophoneDeviceServiceTests.cs` | [Recording pipeline](#recording-pipeline) |
| F-19 | Live annotation while recording | HUD `ToggleAnnotation` and `Tool.*` | `Pointframe/ViewModels/RecordingAnnotationViewModel.cs`, `Pointframe/Services/Recording/RecordingAnnotationSurfaceCoordinator.cs` | `recording_hud_annotation_input_toggled`, `recording_hud_tool_selected`, `recording_hud_undo_annotations`, `recording_hud_clear_annotations` | `Pointframe.Tests/ViewModels/RecordingAnnotationViewModelTests.cs`, `Pointframe.Tests/OverlayWindowRecordingAnnotationTests.cs`, `Pointframe.AutomationTests/Smoke/RecordingAnnotationToolSmokeTests.cs` | [Recording pipeline](#recording-pipeline), [Annotation engine](annotation.md#annotation-engine) |
| F-20 | Blur redaction burned into the video | Blur tool while recording | `Pointframe/Services/Recording/RecordingRedactionSession.cs` | — | `Pointframe.Tests/Services/RecordingRedactionSessionTests.cs` | [Recording pipeline](#recording-pipeline) |
| F-21 | Cursor effects | Recording settings | `Pointframe/Services/Recording/RecordingCursorEffectsService.cs`, `Pointframe/Services/Infrastructure/MouseHookService.cs` | — | `Pointframe.Tests/Services/RecordingCursorEffectsServiceTests.cs` | [Recording pipeline](#recording-pipeline) |
| F-22 | Trim a recording | Tray recent recording, "Trim recording" | `Pointframe/ViewModels/TrimViewModel.cs`, `Pointframe/Views/TrimWindow.xaml.cs`, `Pointframe/Services/Recording/VideoTrimService.cs` | `video_trim_opened`, `video_trim_started`, `video_trim_completed` | `Pointframe.Tests/ViewModels/TrimViewModelTests.cs`, `Pointframe.Tests/Services/VideoTrimServiceTests.cs` | [Recording pipeline](#recording-pipeline) |
| F-23 | Export a recording as GIF | Tray recent recording, GIF export | `Pointframe/Services/Recording/GifExportService.cs` | `gif_export_started`, `gif_export_completed` | `Pointframe.Tests/Services/GifExportServiceTests.cs` | [Recording pipeline](#recording-pipeline), [Runtime paths and external binaries](../knowledge-base.md#runtime-paths-and-external-binaries) |

## Recording pipeline

**Responsibility.** Record a screen region to MP4 (and optionally GIF) while the user can pause, resume, stop, toggle the microphone, and draw on the live desktop. Keep every visual (border, HUD, annotation surface, cursor effects) aligned with the exact pixels being captured on mixed-DPI multi-monitor setups.

**Entry points.**

| Trigger | Path |
|---|---|
| Overlay "Record" on a selection | The `OverlayWindow.Recording.cs` partial starts the countdown, then the session |
| Hotkey full-screen record | `ICaptureLaunchService.StartWholeScreenRecord` |
| Post-recording trim | `TrimRecordingRequestedMessage` opens `TrimWindow` with a `TrimViewModel` built by `Func<string, TrimViewModel>` |

**Flow.**

1. `CountdownWindow` runs the pre-roll. Recording adornments must be invisible to the capture, or they are burned into the frames.
2. `RecordingSessionGeometry` is computed once for the target monitor: host and capture bounds in physical pixels, the same in DIPs, work area, monitor name, scale X and Y. Every consumer maps through its `Map*` methods. Compute it only after the monitor-scoped host window has settled.
3. `IScreenRecordingService.Start(x, y, width, height, outputPath)` (transient `ScreenRecordingService`) truncates width and height to even numbers, starts the capture loop, and writes frames through `IVideoWriterFactory` to `FFMpegVideoWriter`, which pipes into an ffmpeg process located by `FfmpegResolver`.
4. `RecordingOverlayWindow` hosts the border, HUD, and annotation surface for that monitor in PerMonitorV2 context. `RecordingHudCoordinator`, `RecordingAnnotationSurfaceCoordinator`, and `RecordingMousePassthroughCoordinator` place them and toggle click-through; `RecordingOverlayNativeInterop` holds the Win32 calls.
5. `RecordingHudViewModel` (one per session through `Func<IScreenRecordingService, string, RecordingHudViewModel>`) drives pause, resume, stop, microphone, and tool selection. `RecordingMicrophoneSession` restores the device's original mute state on stop.
6. Stop publishes `RecordingCompletedMessage`, carrying whether the microphone was captured. `GifExportService` and `VideoTrimService` post-process through ffmpeg. `WatermarkTokenResolver` expands watermark text templates. See [Recording transcription](transcription.md#recording-transcription).
7. Committed blur elements retain their `RecordingRedactionRegion` identity. Recording annotation undo removes that exact region from the frame redaction snapshot, and redo restores it. The event sidecar uses an unbounded producer queue so event bursts do not fail before recording cleanup.

`FfmpegResolver` order: `AppContext` data key override, `ffmpeg.exe` next to the binary, `Assets\ffmpeg\ffmpeg.exe`, then `PATH`. See [Runtime paths](../knowledge-base.md#runtime-paths-and-external-binaries).

**Invariants.**

- One geometry model per session; see [D-003](#d-003-recording-uses-one-authoritative-session-geometry). Never recompute DPI conversions in a consumer.
- Even width and height before ffmpeg starts; see [Recording width and height are even](#recording-width-and-height-are-even).
- Border and annotation windows are positioned in physical pixels, not DIPs.
- Microphone enumeration uses WASAPI (`MicrophoneDeviceService`); WinMM names are truncated and do not match ffmpeg device names.
- The ffmpeg process must end when the video input ends, or it keeps running on the audio input alone.


**Files.** `Pointframe/Services/Recording/ScreenRecordingService.cs`, `Pointframe/Services/Recording/IScreenRecordingService.cs`, `Pointframe/Services/Recording/IRecordingRedactionSession.cs`, `Pointframe/Services/Recording/RecordingRedactionSession.cs`, `Pointframe/Services/Recording/IRecordingEventTrack.cs`, `Pointframe/Services/Recording/RecordingEventTrack.cs`, `Pointframe/Services/Recording/VideoWriterFactory.cs`, `Pointframe/Services/Recording/FFMpegVideoWriter.cs`, `Pointframe/Services/Recording/FfmpegResolver.cs`, `Pointframe/Models/RecordingSessionGeometry.cs`, `Pointframe/Views/RecordingOverlayWindow.xaml.cs`, `Pointframe/Views/OverlayWindow.Recording.cs`, `Pointframe/Views/OverlayWindow.RecordingHud.cs`, `Pointframe/Views/OverlayWindow.RecordingAnnotation.cs`, `Pointframe/Views/CountdownWindow.xaml.cs`, `Pointframe/ViewModels/RecordingHudViewModel.cs`, `Pointframe/Services/Recording/RecordingHudCoordinator.cs`, `Pointframe/Services/Recording/RecordingAnnotationSurfaceCoordinator.cs`, `Pointframe/Services/Recording/RecordingMousePassthroughCoordinator.cs`, `Pointframe/Services/Recording/RecordingOverlayNativeInterop.cs`, `Pointframe/Services/Recording/RecordingCursorEffectsService.cs`, `Pointframe/Services/Recording/RecordingMicrophoneSession.cs`, `Pointframe/Services/Infrastructure/MicrophoneDeviceService.cs`, `Pointframe/Services/Recording/GifExportService.cs`, `Pointframe/Services/Recording/VideoTrimService.cs`, `Pointframe/ViewModels/TrimViewModel.cs`, `Pointframe/Services/Recording/WatermarkTokenResolver.cs`, `Pointframe/Services/Messaging/RecordingCompletedMessage.cs`, `Pointframe/Services/Messaging/TrimRecordingRequestedMessage.cs`.

**Lessons.**

- Lesson: Recording mode must use one authoritative geometry model
- Lesson: Recording border windows must be positioned in physical screen pixels on mixed-DPI multi-monitor setups
- Lesson: Recording annotation windows must be positioned in physical screen pixels on mixed-DPI multi-monitor setups
- Lesson: Recording-time desktop capture and HUD placement must use the target monitor's coordinate system
- Lesson: Monitor-scoped recording hosts must settle before capture geometry is computed
- Lesson: Recording-time controls and annotation surfaces are most reliable when hosted inside the main overlay window
- Lesson: Recording overlays need native click relays for interactive mode, not only `HTTRANSPARENT`
- Lesson: Visible topmost WPF overlays can be captured by screen recording and still toggle click-through input at runtime
- Lesson: Visible recording adornments are burned into CopyFromScreen output
- Lesson: Full-screen recording HUDs need a compact default, not the region-recording layout
- Lesson: ffmpeg microphone capture must use Windows capture-device names compatible with the recording backend
- Lesson: Recording annotation undo must reconcile output redactions
- Lesson: ffmpeg screen-plus-microphone recordings must stop when the video input ends
- Lesson: Recording HUD microphone toggles must restore the device's original mute state
- Lesson: Dropped recording frames shorten the final MP4 duration
- Lesson: WinMM device names are truncated — use WASAPI (MMDeviceEnumerator) for microphone enumeration
**Tests.** Services: `Pointframe.Tests/Services/ScreenRecordingServiceTests.cs`, `Pointframe.Tests/Services/FFMpegVideoWriterTests.cs`, `Pointframe.Tests/Services/VideoWriterFactoryTests.cs`, `Pointframe.Tests/Services/RecordingMicrophoneSessionTests.cs`, `Pointframe.Tests/Services/RecordingCursorEffectsServiceTests.cs`, `Pointframe.Tests/Services/GifExportServiceTests.cs`, `Pointframe.Tests/Services/VideoTrimServiceTests.cs`, `Pointframe.Tests/Services/WatermarkTokenResolverTests.cs`. Models and ViewModels: `Pointframe.Tests/Models/RecordingSessionGeometryTests.cs`, `Pointframe.Tests/ViewModels/RecordingHudViewModelTests.cs`, `Pointframe.Tests/ViewModels/TrimViewModelTests.cs`. Windows: `Pointframe.Tests/RecordingHudPositionTests.cs`, `Pointframe.Tests/RecordingOverlayWindowTests.cs`, `Pointframe.Tests/OverlayWindowRecordingFlowTests.cs`, `Pointframe.Tests/OverlayWindowRecordingAnnotationTests.cs`. Automation: `Pointframe.AutomationTests/Smoke/RecordingOverlaySmokeTests.cs`, `Pointframe.AutomationTests/Smoke/RecordingHudInteractionTests.cs`.

## Recording width and height are even

**Rule.** `ScreenRecordingService.Start` truncates an odd width or height by one pixel and aborts with a logged error if the result is too small. Anything that positions a visual against the capture (border, annotation surface, cursor mapping) uses the same even size, not the user's raw selection.

**Why.** Frames are handed to ffmpeg as JPEG, whose minimum coded unit needs even dimensions, and the MP4 encoder's 4:2:0 chroma subsampling needs the same. An odd dimension makes the encoder fail or produce corrupt output.

**Enforced by.** `Pointframe.Tests/Services/ScreenRecordingServiceTests.cs`: `Start_WithOddDimensions_TruncatesToEven` and `Start_OddDimensions_TruncatesToEvenBeforeFactory`. `RecordingSessionGeometry` does not round for you; callers pass the truncated size in.

**Symptoms when violated.** ffmpeg exits immediately, or the MP4 has green or shifted edges. The recording border is one pixel wider than the recorded area.

**Files.** `Pointframe/Services/Recording/ScreenRecordingService.cs`, `Pointframe/Models/RecordingSessionGeometry.cs`. See [Recording pipeline](#recording-pipeline) and [D-003](#d-003-recording-uses-one-authoritative-session-geometry).

## D-003 Recording uses one authoritative session geometry

Decided 2026-04-09.

**Context.** Mixed-DPI multi-monitor bugs kept recurring: the border, the HUD, the annotation surface, and the recorder each converted DIPs to pixels with their own idea of the scale, so they drifted apart by a few pixels or by a whole monitor offset.

**Decision.** `RecordingSessionGeometry` is computed once per recording session and carries host bounds and capture bounds in both physical pixels and DIPs, the work area, the monitor name, and the X and Y scale. Consumers call its `Map*` methods (`MapHostDipPointToScreenPixels`, `MapScreenPixelRectToHostDips`, `MapCaptureLocalDipRectToScreenPixels`, and the rest). No consumer reads `PresentationSource` DPI or `SystemParameters` to place recording visuals.

**Consequences.** A new recording visual takes the geometry as input and adds a `Map*` method if none fits. The geometry is computed after the monitor-scoped host window has settled, or every consumer inherits the wrong scale. `Pointframe.Tests/Models/RecordingSessionGeometryTests.cs` pins the mapping math; extend it with each new method.

**Alternatives rejected.** Per-window DPI reads with shared helper functions: still produced disagreements because each window's HWND could sit on a different monitor at read time.

**Files.** `Pointframe/Models/RecordingSessionGeometry.cs`, `Pointframe/Services/Recording/ScreenRecordingService.cs`. See [Recording pipeline](#recording-pipeline) and [DPI coordinate systems](../knowledge-base.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor).

**Lessons.**

- Lesson: Recording mode must use one authoritative geometry model
- Lesson: Mixed-DPI multi-monitor capture features need PerMonitorV2 process DPI awareness
