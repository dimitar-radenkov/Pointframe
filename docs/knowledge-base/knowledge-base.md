# Pointframe Knowledge Base

Durable, agent-readable project knowledge in one file. Use the table of contents, then read only the sections that match the task.

Last full review against the code: 2026-09-05.

## How to maintain this file

**What belongs here.** How a subsystem is composed and where its entry points are; decisions with lasting impact and their reasons; rules the code must keep, why, and what breaks; recipes for recurring changes; stable facts such as paths, lifetimes, pipelines, and tools.

**What goes elsewhere.** Bug post-mortems: `lessons.md` (Problem, Root cause, What fixed it, Takeaway). Roadmap, plans, task status: `plan/` (local-only, not in git). Contributor setup: `docs/developer-guide.md`. Per-session instructions: `CLAUDE.md`, one line per topic pointing here. Test counts, PR numbers, "verified on my machine": the PR description.

**Structure.** Five fixed groups, each a `##` heading; one `###` section per topic. Templates per group:

| Group | Section layout |
|---|---|
| Subsystems | Responsibility, Entry points, Flow, Key types, Invariants, Tests, Files, Lessons |
| Decisions | `D-NNN Title`, decided date, Context, Decision, Consequences, Alternatives rejected, Files. Numbers are never reused; a reversed decision stays with "Superseded by D-NNN" on its first line |
| Invariants | Rule, Why, Enforced by, Symptoms when violated, Files |
| How-tos | When, Steps, Verify, Files |
| References | Tables or short lists, each fact with its source, Files |

**Conventions the script checks.** Repo paths in backticks must exist (`Pointframe/App.xaml.cs`). Lesson references are bullets of the form `- Lesson: <exact heading from lessons.md>`. Cross-references are anchor links, `[Recording pipeline](#recording-pipeline)`. The table of contents between the `toc` markers is generated.

**Writing rules.** Say why, not only what. Replace stale text in place; never append a correction below an old statement. Prefer a table or a short list over prose. Keep a section under about 80 lines. Absolute dates only. No status badges or task journals.

**Maintenance.** Use the `/knowledge-base` skill (`.claude/skills/knowledge-base/SKILL.md`): `/knowledge-base add <group> <title>` and `/knowledge-base update [section]`. Both finish with:

```powershell
pwsh .claude/skills/knowledge-base/knowledge-base.ps1          # refresh the table of contents, then check paths, lessons, links
pwsh .claude/skills/knowledge-base/knowledge-base.ps1 -Check   # check only
```

## Contents

<!-- toc -->

- [Subsystems](#subsystems)
  - [App bootstrap, DI, and messaging](#app-bootstrap-di-and-messaging)
  - [Capture overlay and selection](#capture-overlay-and-selection)
  - [Annotation engine](#annotation-engine)
  - [Recording pipeline](#recording-pipeline)
  - [Standalone CLI and MCP automation](#standalone-cli-and-mcp-automation)
  - [User settings](#user-settings)
  - [Capture library and data layer](#capture-library-and-data-layer)
  - [Telemetry](#telemetry)
  - [Update flow](#update-flow)
  - [Recording transcription](#recording-transcription)
- [Decisions](#decisions)
  - [D-001 MVVM plus DI is the composition model](#d-001-mvvm-plus-di-is-the-composition-model)
  - [D-002 One knowledge base file, checked by script](#d-002-one-knowledge-base-file-checked-by-script)
  - [D-003 Recording uses one authoritative session geometry](#d-003-recording-uses-one-authoritative-session-geometry)
  - [D-004 Native libraries ship loose and the installer packages them](#d-004-native-libraries-ship-loose-and-the-installer-packages-them)
  - [D-005 The speech model is delivered by both the installer and the app](#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app)
- [Invariants](#invariants)
  - [Undo groups are added only on commit](#undo-groups-are-added-only-on-commit)
  - [Settings are read at the point of use and persisted through three files](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files)
  - [Recording width and height are even](#recording-width-and-height-are-even)
  - [DIPs and physical pixels are converted explicitly per monitor](#dips-and-physical-pixels-are-converted-explicitly-per-monitor)
  - [Everything emitted next to the exe must be in the installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list)
- [How-tos](#how-tos)
  - [Add an annotation tool](#add-an-annotation-tool)
  - [Add a user setting](#add-a-user-setting)
  - [Register a service](#register-a-service)
- [References](#references)
  - [Runtime paths and external binaries](#runtime-paths-and-external-binaries)
  - [CI, CD, and versioning](#ci-cd-and-versioning)

<!-- /toc -->

## Subsystems

### App bootstrap, DI, and messaging

**Responsibility.** Own process lifetime: build the Generic Host, configure Serilog, register every service and window, apply database migrations, show the tray icon, register global hotkeys, and tear it all down on exit. There is no visible main window; the app is tray-first.

**Entry points.**

| Step | Where |
|---|---|
| Startup sequence | `App.OnStartup` in `Pointframe/App.xaml.cs` |
| Service registration | `AddPointframeAppServices` in `Pointframe/AppServiceRegistration.cs` |
| Data services (EF Core, SQLite) | `AddPointframeDataServices` in `Pointframe.Data/DependencyInjection.cs`, called from the registration above |
| Hotkey to action wiring | `App.OnStartup`: events on `IGlobalHotkeyService` call `ICaptureLaunchService` |
| Shutdown | `App.OnExit` disposes the hotkey hook and the host |

**Flow.**

1. Parse automation launch options (used by `Pointframe.AutomationTests`) and register them as a singleton.
2. Build `Host.CreateDefaultBuilder()` with configuration from `appsettings.json` plus optional `appsettings.Local.json`, Serilog logging, and `AddPointframeAppServices`.
3. Apply EF Core migrations through a scoped `IMigrationService` before any window opens.
4. Resolve `ITrayIconManager` and `IGlobalHotkeyService`. Wire `RegionSnipRequested`, `WholeScreenSnipRequested`, `WholeScreenRecordRequested`, and `CleanWindowSnipRequested` to the matching `ICaptureLaunchService.Start*` method with source `"hotkey"` (tray callers pass `"tray"`; the source feeds telemetry). Call `Register()`.
5. Hosted services start with the host: `AutoUpdateService` and `TelemetryHeartbeatService`.

**Lifetimes.**

| Lifetime | Rule | Examples |
|---|---|---|
| Singleton | Long-lived state or OS handles | settings, hotkeys, tray, telemetry, event aggregator, annotation geometry, OCR, update services, clipboard, dialogs, file system, capture library |
| Transient | One per operation; disposed per use | screen and window capture, video writer factory, screen recording, every ViewModel, every window |
| Scoped | EF Core only; resolve inside `CreateScope()` | `PointframeDataContext`, unit of work, repositories, migration service |
| Factory `Func<...>` | The instance needs runtime arguments | `Func<IScreenRecordingService, string, RecordingHudViewModel>`, `Func<BitmapSource, BeautifierWindow>`, `Func<string, TrimViewModel>` |

`OverlayWindow` is built by `CreateOverlayWindow` in the registration file because its constructor is long; add new dependencies there, not in XAML.

**Messaging.** `IEventAggregator` decouples tray, overlay, recording, and windows. `Subscribe<TEvent>(Func<TEvent, ValueTask>)` returns an `IEventSubscription`; `Publish(object)` awaits every handler. Message records live in `Pointframe/Services/Messaging/`: capture and recording completed, open image, show About, Library, and Settings, trim recording, undo and redo groups, update available. Prefer a new message over passing window references around.

**Invariants.**

- Every public service has an `I<Name>` interface and is registered here. See [Register a service](#register-a-service).
- The keyboard hook is unhooked in `OnExit`; an orphaned hook routes every keystroke on the machine through a dead process.
- Nothing reads settings into a field at startup; read `IUserSettingsService.Current` at the point of use. See [Settings persistence](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files).
- Windows launched from a tray callback are deferred until the tray menu unwinds and get a real owner window, or they lose focus and vanish.

**Tests.** `Pointframe.Tests/AppTests.cs` builds the container and resolves the core services and factories, so a missing registration fails there first. Also `Pointframe.Tests/Services/TrayIconManagerTests.cs`, `Pointframe.Tests/Services/GlobalHotkeyServiceTests.cs`, and `Pointframe.AutomationTests/Smoke/LaunchModeSmokeTests.cs` for launch modes.

**Files.** `Pointframe/App.xaml.cs`, `Pointframe/AppServiceRegistration.cs`, `Pointframe/Services/Messaging/IEventAggregator.cs`, `Pointframe/Services/Messaging/DefaultEventAggregator.cs`, `Pointframe/Services/Infrastructure/GlobalHotkeyService.cs`, `Pointframe/Services/Infrastructure/TrayIconManager.cs`, `Pointframe/Services/Capture/CaptureLaunchService.cs`, `Pointframe/Automation/AutomationLaunchOptions.cs`, `Pointframe.Data/DependencyInjection.cs`.

**Lessons.**

- Lesson: Automation-mode window replacement should not rely on OnLastWindowClose
- Lesson: Tray-launched file dialog can lose focus in a tray-only WPF app

### Capture overlay and selection

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
- Convert coordinates per monitor; see [DPI coordinate systems](#dips-and-physical-pixels-are-converted-explicitly-per-monitor). Never divide Win32 screen coordinates by a single overlay DPI.

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

### Annotation engine

**Responsibility.** Own everything about drawing on a captured image: the active tool, its color and stroke, the draft shape during a drag, committed elements, and undo and redo. The same engine serves the screenshot overlay and the recording overlay.

**Key types.**

| Type | Role |
|---|---|
| `AnnotationTool` enum | Arrow, Rectangle, Text, Highlight, Pen, Line, Circle, Number, Blur, Callout, ColorPicker, PixelRuler |
| `ShapeParameters` sealed records | Immutable description of one shape per tool, produced by `AnnotationViewModel.TryGetShapeParameters()` |
| `AnnotationViewModel` | Tool, color, thickness, style presets, number counter, undo and redo stacks of element groups |
| `RecordingAnnotationViewModel` | Recording-time variant with the reduced tool set |
| `AnnotationCanvasRenderer` | Maps each `AnnotationTool` to an `IAnnotationShapeHandler` and drives the active one through a drag |
| `IAnnotationShapeHandler` | `Begin(point, brush, thickness, canvas)`, `Update(point)`, `Commit(canvas, trackElement)`, `Cancel(canvas)`; one class per tool under `Pointframe/Services/Annotation/Handlers/` |
| `AnnotationCanvasInteractionController` | Translates mouse events into renderer calls |
| `IAnnotationGeometryService` | Pure math: arrowheads, bounding boxes, hit tests; unit-tested without WPF |

**Flow of one drag.**

1. Mouse down: the controller asks the renderer to begin. The renderer picks the handler for `SelectedTool` and calls `Begin` with the current style.
2. Mouse move: `Update(point)` mutates the draft element only.
3. Mouse up: `Commit` adds final elements to the canvas and calls `trackElement` for each; the ViewModel records them as one undo group.
4. Escape or tool switch mid-drag: `Cancel` removes the draft. Exactly one of `Commit` or `Cancel` runs per drag, or the next drag throws on a stale draft.

Text and Callout edit in a live `TextBox`; `LostFocus` converts it to a `TextBlock`. Removing that handler leaves editable boxes in the exported bitmap. Number resets its counter through the ViewModel on undo and redo.

**Invariants.**

- Undo groups are added only on commit. See [Undo groups are added only on commit](#undo-groups-are-added-only-on-commit).
- The recording HUD's tool list derives from the annotation allowlist; there is no second list to keep in sync.
- Shape definitions are records in `Pointframe/Models/ShapeParameters.cs`; handlers hold no state between drags.

**Tests.** `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs`, `Pointframe.Tests/ViewModels/RecordingAnnotationViewModelTests.cs`, `Pointframe.Tests/Services/AnnotationCanvasRendererTests.cs`, `Pointframe.Tests/Services/AnnotationCanvasInteractionControllerTests.cs`, `Pointframe.Tests/Services/AnnotationGeometryServiceTests.cs`, per-handler tests under `Pointframe.Tests/Services/Handlers/`. Smoke coverage: `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs` and `Pointframe.AutomationTests/Smoke/RecordingAnnotationToolSmokeTests.cs`, driven by ids in `Pointframe.AutomationTests/Support/AutomationIds.cs`.

**Files.** `Pointframe/Models/AnnotationTool.cs`, `Pointframe/Models/ShapeParameters.cs`, `Pointframe/Models/AnnotationStylePreset.cs`, `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/ViewModels/RecordingAnnotationViewModel.cs`, `Pointframe/ViewModels/AnnotationStylePresetViewModel.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs`, `Pointframe/Services/Annotation/AnnotationCanvasInteractionController.cs`, `Pointframe/Services/Annotation/IAnnotationGeometryService.cs`, `Pointframe/Services/Annotation/AnnotationGeometryService.cs`, `Pointframe/Services/Annotation/Handlers/IAnnotationShapeHandler.cs`.

**Lessons.**

- Lesson: Recording HUD tool selection should not duplicate the annotation-tool allowlist

### Recording pipeline

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
6. Stop publishes `RecordingCompletedMessage`, carrying whether the microphone was captured. `GifExportService` and `VideoTrimService` post-process through ffmpeg. `WatermarkTokenResolver` expands watermark text templates. See [Recording transcription](#recording-transcription).
7. Committed blur elements retain their `RecordingRedactionRegion` identity. Recording annotation undo removes that exact region from the frame redaction snapshot, and redo restores it. The event sidecar uses an unbounded producer queue so event bursts do not fail before recording cleanup.

`FfmpegResolver` order: `AppContext` data key override, `ffmpeg.exe` next to the binary, `Assets\ffmpeg\ffmpeg.exe`, then `PATH`. See [Runtime paths](#runtime-paths-and-external-binaries).

**Invariants.**

- One geometry model per session; see [D-003](#d-003-recording-uses-one-authoritative-session-geometry). Never recompute DPI conversions in a consumer.
- Even width and height before ffmpeg starts; see [Recording width and height are even](#recording-width-and-height-are-even).
- Border and annotation windows are positioned in physical pixels, not DIPs.
- Microphone enumeration uses WASAPI (`MicrophoneDeviceService`); WinMM names are truncated and do not match ffmpeg device names.
- The ffmpeg process must end when the video input ends, or it keeps running on the audio input alone.

**Tests.** Services: `Pointframe.Tests/Services/ScreenRecordingServiceTests.cs`, `Pointframe.Tests/Services/FFMpegVideoWriterTests.cs`, `Pointframe.Tests/Services/VideoWriterFactoryTests.cs`, `Pointframe.Tests/Services/RecordingMicrophoneSessionTests.cs`, `Pointframe.Tests/Services/RecordingCursorEffectsServiceTests.cs`, `Pointframe.Tests/Services/GifExportServiceTests.cs`, `Pointframe.Tests/Services/VideoTrimServiceTests.cs`, `Pointframe.Tests/Services/WatermarkTokenResolverTests.cs`. Models and ViewModels: `Pointframe.Tests/Models/RecordingSessionGeometryTests.cs`, `Pointframe.Tests/ViewModels/RecordingHudViewModelTests.cs`, `Pointframe.Tests/ViewModels/TrimViewModelTests.cs`. Windows: `Pointframe.Tests/RecordingHudPositionTests.cs`, `Pointframe.Tests/RecordingOverlayWindowTests.cs`, `Pointframe.Tests/OverlayWindowRecordingFlowTests.cs`, `Pointframe.Tests/OverlayWindowRecordingAnnotationTests.cs`. Automation: `Pointframe.AutomationTests/Smoke/RecordingOverlaySmokeTests.cs`, `Pointframe.AutomationTests/Smoke/RecordingHudInteractionTests.cs`.

### Standalone CLI and MCP automation

**Responsibility.** Provide agent-facing desktop capture and whole-monitor recording without starting the WPF tray application or creating overlay windows. Both hosts call the shared `Pointframe.Engine` services directly and require an interactive Windows desktop session.

**Entry points.**

| Host | Entry point | Transport |
|---|---|---|
| CLI | `Pointframe.Cli/Program.cs` and `CliApplication.RunAsync` | Process arguments and stdout/stderr |
| MCP server | `Pointframe.Mcp/Program.cs`, `PointframeMcpTools`, and `PointframeMcpResources` | MCP stdio |

**Capabilities.**

- The CLI accepts `displays` or `capture --monitor <exact Windows device name>`. Invalid or incomplete commands print usage and return exit code 2; execution failures return exit code 1.
- The MCP resource `pointframe://commands` lists the supported command identifiers.
- MCP tools list displays, capture a named monitor to a PNG artifact, start a no-microphone MP4 recording, and stop that recording with finalized artifact and sidecar metadata.
- Recording requires an explicit redaction-region array. Regions are capture-local physical pixels and are applied before frames reach ffmpeg.

**Packaging and configuration.**

`Pointframe.Mcp` is published self-contained for `win-x64`. `packaging/build-mcp-package.ps1` copies the published executable and a supplied `ffmpeg.exe` into a versioned legacy ZIP and MCPB bundle. It also emits a SHA-256 checksum and MCP Registry `server.json` metadata pointing at the GitHub Release MCPB asset. The standalone package contains no WPF application. VS Code and other MCP clients launch the executable as a local stdio server.

CI publishes the MCP executable and runs `packaging/test-mcp-stdio.ps1`, which verifies the initialize handshake and expected tool list without starting WPF. CD attaches the MCPB, checksum, and registry metadata to the versioned GitHub Release. The bundle remains Windows-only and requires an interactive desktop session for capture and recording.

**Invariants.**

- Capture and recording must run in the logged-in interactive desktop session; Windows services in session 0 cannot capture the user desktop.
- Monitor names passed to CLI/MCP commands are exact Windows device names, such as `\\.\DISPLAY1`.
- Artifact paths and metadata are produced through the shared direct services under `%LOCALAPPDATA%\Pointframe`; recording also emits an event sidecar without bitmap data, OCR text, clipboard contents, or prompts.

**Tests.** `Pointframe.Tests/Cli/CliApplicationTests.cs`, `Pointframe.Tests/Mcp/DirectCaptureServiceTests.cs`, `Pointframe.Tests/Mcp/DirectRecordingMcpServiceTests.cs`, and `Pointframe.Tests/Engine/DirectRecordingServiceTests.cs`. Protocol smoke coverage is in `packaging/test-mcp-stdio.ps1`.

**Files.** `Pointframe.Cli/Program.cs`, `Pointframe.Cli/Application/CliApplication.cs`, `Pointframe.Cli/Commands/CliCommandParser.cs`, `Pointframe.Cli/Commands/CliCommand.cs`, `Pointframe.Mcp/Program.cs`, `Pointframe.Mcp/Tools/PointframeMcpTools.cs`, `Pointframe.Mcp/Resources/PointframeMcpResources.cs`, `Pointframe.Mcp/Services/DirectRecordingMcpService.cs`, `Pointframe.Mcp/Mappers/McpResponseMapper.cs`, `Pointframe.Mcp/Models/DirectRecordingResponse.cs`, `Pointframe.Mcp/Models/McpToolResponses.cs`, `Pointframe.Engine/Capture/Services/DirectCaptureService.cs`, `Pointframe.Engine/Capture/Services/DisplayCaptureEngine.cs`, `Pointframe.Engine/Capture/Models/DirectCaptureModels.cs`, `Pointframe.Engine/Recording/Services/DirectRecordingService.cs`, `Pointframe.Engine/Recording/Services/RawFrameRecordingPipeline.cs`, `Pointframe.Engine/Recording/Services/FfmpegDirectVideoWriter.cs`, `Pointframe.Engine/Recording/Models/DirectRecordingModels.cs`, `Pointframe/Services/Recording/ArtifactMetadataService.cs`, `packaging/build-mcp-package.ps1`, `packaging/test-mcp-stdio.ps1`, `.github/workflows/ci.yml`, `.github/workflows/cd.yml`.

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

### User settings

**Responsibility.** Hold every user preference, persist it as JSON, and expose it through one singleton so every consumer sees the current value.

**Key types.**

| Type | Role |
|---|---|
| `UserSettings` | Mutable POCO with defaults in property initializers; serialized as-is |
| `IUserSettingsService` | `Current` (never cache it), `Save(settings)` replaces the whole object, `Update(Action<UserSettings>)` clones, mutates, and saves |
| `UserSettingsService` | Loads on construction; a missing or corrupt file falls back to defaults with a log line; `Clone` copies every property for `Update` |
| `SettingsViewModel` | One observable property per setting, grouped into `SettingsSection` items for the navigation rail; `Save()` writes them all back |
| `SettingsWindow` | Sectioned window: Capture, Recording, Annotation, Shortcuts, App; automation ids live in `AutomationIds.cs` |
| `HotkeyBinding`, `HotkeyModifiers` | Persisted hotkey model consumed by `GlobalHotkeyService` |
| `IThemeService`, `AppTheme` | Applies the light, dark, or system theme from settings |
| `ScreenshotWatermarkSettings`, `VideoWatermarkSettings` | Separate watermark settings; on load a missing video watermark is cloned from the screenshot one |

**Storage.** `settings.json` in the Pointframe local app data folder. Automation tests redirect it with the `SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH` environment variable. See [Runtime paths](#runtime-paths-and-external-binaries).

**Invariants.**

- A new setting touches three files together or it is silently dropped on save. See [Settings persistence](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) and [Add a user setting](#add-a-user-setting).
- Restore Defaults writes hidden persisted values directly; a mode flag that defers the write leaves stale values behind.
- Consumers read `IUserSettingsService.Current` at the point of use.

**Tests.** `Pointframe.Tests/Services/UserSettingsServiceTests.cs`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs` (reflection over every `UserSettings` property through save, load, `Update`, and `SettingsViewModel.Save`), `Pointframe.Tests/ViewModels/SettingsViewModelTests.cs`, `Pointframe.Tests/SettingsWindowTests.cs`, `Pointframe.Tests/Services/ThemeServiceTests.cs`, `Pointframe.Tests/Models/HotkeyBindingTests.cs`. Automation: `Pointframe.AutomationTests/Smoke/SettingsWindowSmokeTests.cs`, `Pointframe.AutomationTests/Smoke/SettingsSectionNavigationTests.cs`.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/IUserSettingsService.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe/Views/SettingsWindow.xaml`, `Pointframe/Models/SettingsSection.cs`, `Pointframe/Models/SettingsSectionItem.cs`, `Pointframe/Models/HotkeyBinding.cs`, `Pointframe/Models/HotkeyModifiers.cs`, `Pointframe/Models/AppTheme.cs`, `Pointframe/Services/Infrastructure/ThemeService.cs`, `Pointframe/Models/WatermarkSettings.cs`, `Pointframe/Models/ScreenshotWatermarkSettings.cs`, `Pointframe/Models/VideoWatermarkSettings.cs`.

**Lessons.**

- Lesson: Restore-defaults flows must update hidden persisted settings directly, not through a sticky mode flag

### Capture library and data layer

**Responsibility.** Let the user find any saved screenshot by date, file name, or text visible in the image, and open it in the annotation overlay. Cache OCR results so a search does not re-run OCR on every file.

**Entry points.**

| Trigger | Path |
|---|---|
| Tray "Library" | `ShowLibraryWindowRequestedMessage` through the event aggregator; `LibraryWindow` is transient |
| Search | `LibraryViewModel.SearchAsync(query, from, to, progress, ct)` calls `ICaptureLibraryService.SearchAsync` |
| Open item | The library closes first, then launches the overlay in `OpenedImage` mode |

**Flow.**

1. `CaptureLibraryService.GetCaptures()` lists image files in the configured save folder as `CaptureItem` values.
2. `SearchAsync` filters by date, then by file name, then by OCR text. Progress is reported per file through `CaptureSearchProgress`.
3. `CaptureTextLookupService.GetText(item)` returns cached text from the database when present, otherwise runs `IOcrService` and stores the result as a `CaptureTextCacheEntry`.

**Data layer (`Pointframe.Data`).**

- EF Core with SQLite at `pointframe.db` in the local app data folder. The connection string is built in `AppServiceRegistration`.
- `AddPointframeDataServices` registers `PointframeDataContext`, `IPointframeDataUnitOfWork`, `ICaptureTextCacheRepository`, and `IMigrationService` as scoped. Resolve them inside `IServiceProvider.CreateScope()`; the app's singletons must not capture them.
- Migrations live in `Pointframe.Data/Migrations/` and run at startup from `App.ApplyDataMigrations`. Add one with the `dotnet ef` tool from `dotnet-tools.json` (`dotnet tool restore` first); `PointframeDataContextFactory` is the design-time factory.
- Generic `IRepository<T>` and `IReadOnlyRepository<T>` in `Pointframe.Data/Abstractions/` back the concrete repositories.

**Tests.** `Pointframe.Tests/ViewModels/LibraryViewModelTests.cs`, `Pointframe.Tests/Services/CaptureLibraryServiceTests.cs`, `Pointframe.Tests/Services/CaptureLibrarySearchTests.cs`, `Pointframe.Tests/Services/CaptureLibraryOcrSearchTests.cs`, `Pointframe.Tests/Services/CaptureTextLookupServiceTests.cs`, `Pointframe.Tests/Services/SqliteCaptureTextCacheRepositoryTests.cs`.

**Files.** `Pointframe/Views/LibraryWindow.xaml.cs`, `Pointframe/ViewModels/LibraryViewModel.cs`, `Pointframe/Services/Capture/ICaptureLibraryService.cs`, `Pointframe/Services/Capture/CaptureLibraryService.cs`, `Pointframe/Services/Capture/ICaptureTextLookupService.cs`, `Pointframe/Services/Capture/CaptureTextLookupService.cs`, `Pointframe/Models/CaptureItem.cs`, `Pointframe/Models/CaptureSearchProgress.cs`, `Pointframe.Data/DependencyInjection.cs`, `Pointframe.Data/Context/PointframeDataContext.cs`, `Pointframe.Data/Context/PointframeDataContextFactory.cs`, `Pointframe.Data/Entities/CaptureTextCacheEntry.cs`, `Pointframe.Data/Repository/CaptureTextCacheRepository.cs`, `Pointframe.Data/Repository/PointframeDataUnitOfWork.cs`, `Pointframe.Data/Services/MigrationService.cs`.

### Telemetry

**Responsibility.** Record product usage and diagnostics in Application Insights without collecting content, and keep the public privacy statement and the code in lockstep.

**Key types.**

| Type | Role |
|---|---|
| `ITelemetryService` | `TrackEvent(name, properties)`, `TrackException(...)`, `Flush()` |
| `TelemetryService` | Application Insights client; sends nothing when the connection string is empty |
| `NullTelemetryService` | Explicit no-op for tests and disabled builds |
| `TelemetryEventCatalog` | `TelemetryChannel` (Product, Diagnostic), `TelemetryPropertyKeys`, `TelemetryEvents` name constants, and `All` definitions with required properties |
| `TelemetryHeartbeatService` | Hosted service emitting `app_heartbeat` with uptime and session data |
| `ActivationTelemetryService` | First-run and activation funnel events |

**Configuration.** `Pointframe/appsettings.json` ships `ApplicationInsights:ConnectionString` empty, so source builds and CI send nothing. The CD workflow injects the real string and verifies the injection before publishing. A developer can set a personal string in `appsettings.Local.json`, which is loaded after `appsettings.json`. See [CI, CD, and versioning](#ci-cd-and-versioning).

**Invariants.**

- Define every event in the catalog and emit it by its constant; the catalog is the single source for names and required properties.
- The README section `### What is collected` lists every catalog event with its required properties and nothing else. `TelemetryDocumentationTests` parses that table and fails the build on drift, so an event change and its README row ship in the same change.
- Properties are labels (tool, capture type, URL host), never content. Do not add file paths, OCR text, or image data.

**Analysis.** Kusto queries and the workbook template: `docs/appinsights-feature-usage-queries.kql` and `docs/appinsights-pointframe-workbook.all-in-one.template.json`.

**Tests.** `Pointframe.Tests/Services/TelemetryServiceTests.cs`, `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs`, `Pointframe.Tests/Services/TelemetryDocumentationTests.cs`, `Pointframe.Tests/Services/ActivationTelemetryServiceTests.cs`.

**Files.** `Pointframe/Services/Infrastructure/ITelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/NullTelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs`, `Pointframe/Services/Infrastructure/ActivationTelemetryService.cs`, `Pointframe/appsettings.json`.

### Update flow

**Responsibility.** Find newer releases on GitHub, tell the user, download the installer, and hand off to it.

**Flow.**

1. `AutoUpdateService` is a singleton registered three ways: as itself, as `IAutoUpdateService`, and as an `IHostedService`, so the host starts it and the tray can call it. It polls `IUpdateService` on the `UpdateCheckInterval` from settings (`EveryDay`, `Every2Days`, `Every3Days`, `Never`).
2. `GitHubUpdateService.CheckForUpdates` compares the latest release with the running version from `IAppVersionService` (Nerdbank.GitVersioning) and returns an `UpdateCheckResult`.
3. A newer version publishes `UpdateAvailableMessage`; the tray and `AboutViewModel` surface it. `AboutViewModel` can also trigger a manual check.
4. `IAutoUpdateService.ConfirmAndInstall(result)` opens `UpdateDownloadWindow` through `IUpdateDownloadService` (`UpdateDownloadWindowService`), the testable seam. `UpdateDownloadViewModel` streams the installer with a shared `HttpClient` and launches it through `IProcessService`.

**Invariants.**

- Window services ignore UI events that arrive after their window closed; late progress callbacks after close are a crash source.
- The installer asset name and the `v<version>` tag are produced by the CD workflow; the updater's expectations and the workflow change together. See [CI, CD, and versioning](#ci-cd-and-versioning).

**Tests.** `Pointframe.Tests/Services/AutoUpdateServiceTests.cs`, `Pointframe.Tests/Services/GitHubUpdateServiceTests.cs`, `Pointframe.Tests/Services/UpdateDownloadWindowServiceTests.cs`, `Pointframe.Tests/Services/AppVersionServiceTests.cs`, `Pointframe.Tests/ViewModels/UpdateDownloadViewModelTests.cs`, `Pointframe.Tests/ViewModels/AboutViewModelTests.cs`.

**Files.** `Pointframe/Services/Update/IUpdateService.cs`, `Pointframe/Services/Update/GitHubUpdateService.cs`, `Pointframe/Services/Update/IAutoUpdateService.cs`, `Pointframe/Services/Update/AutoUpdateService.cs`, `Pointframe/Services/Update/IUpdateDownloadService.cs`, `Pointframe/Services/Update/UpdateDownloadWindowService.cs`, `Pointframe/ViewModels/UpdateDownloadViewModel.cs`, `Pointframe/Views/UpdateDownloadWindow.xaml.cs`, `Pointframe/ViewModels/AboutViewModel.cs`, `Pointframe/Models/UpdateCheckResult.cs`, `Pointframe/Models/UpdateCheckInterval.cs`, `Pointframe/Services/Infrastructure/AppVersionService.cs`, `Pointframe/Services/Messaging/UpdateAvailableMessage.cs`.

### Recording transcription

**Responsibility.** After a recording that captured microphone audio, produce `.srt` and `.txt` transcripts next to the MP4, entirely on the local machine. Nothing is uploaded and no API key exists. English only, narration only: the recorder captures a microphone, never system audio, so a meeting or a played video produces nothing.

**Entry points.**

| Trigger | Path |
|---|---|
| A recording finishes | `App.HandleRecordingCompleted` enqueues when `RecordingTranscriptEnabled` and the message's `HadMicrophoneAudio` are both true |
| The user asks for the model | `SettingsViewModel.DownloadTranscriptModelCommand` in the Settings Recording section |
| Setup optional component | The `whispermodel` task in `installer/Pointframe.iss` |

**Flow.**

1. `RecordingHudViewModel.Stop` reads `IScreenRecordingService.IsRecordingMicrophoneEnabled` *before* awaiting `Stop()`, because the flag resets inside it, and passes it on `RecordingCompletedMessage`.
2. `ITranscriptionQueue` (`TranscriptionQueue`) accepts the path and runs jobs serially on one background consumer built on `Channel<string>`.
3. `TranscriptionService` resolves the model through `ITranscriptModelService`. A missing model returns a skip result before ffmpeg is ever started.
4. `IAudioExtractor` (`FfmpegAudioExtractor`) writes a temp 16 kHz mono `pcm_s16le` WAV, the only format Whisper accepts. The temp file is deleted in a `finally`.
5. `ISpeechRecognizer` (`WhisperSpeechRecognizer`) is handed the already-resolved model path and streams `TranscriptSegment` values.
6. `SubtitleFormatter` renders both sidecars; `TranscriptionService` writes them without a byte-order mark.
7. `App.HandleTranscriptionCompleted` marshals to the dispatcher, then reports through `ITrayIconManager.ShowTranscriptBalloon` and the telemetry catalog.

**Key types.** `TranscriptionResult(Success, SrtPath, TxtPath, SkipReason, ErrorMessage, SegmentCount)` distinguishes an expected skip from a genuine failure; `TranscriptionSkipReasons` holds the two skip strings so the service and `App` cannot drift. `TranscriptSegment(Start, End, Text)`.

**Invariants.**

- The model path is resolved once, by `ITranscriptModelService`, and passed to the recognizer. Two independent resolutions previously disagreed: one skipped gracefully, the other threw.
- `TranscriptModelResolver` returns a path only when the file exists, the `AppContext` override included. A stale override otherwise reports a model that is not there and fails inside Whisper instead of skipping.
- Jobs are queued, never cancelled by a newer recording, or a second clip silently discards the first clip's transcript.
- The queue runs on a thread pool thread; every tray call hops back through `Dispatcher.InvokeAsync` because `TaskbarIcon` has dispatcher affinity.
- Every non-success outcome is reported. Reporting only `ErrorMessage` made a missing model look like the feature doing nothing at all.
- `.srt` is UTF-8 without a BOM and CRLF; blank segments are skipped rather than written as empty-bodied cues, or strict parsers drop every cue that follows.

**Tests.** `Pointframe.Tests/Services/TranscriptionServiceTests.cs`, `Pointframe.Tests/Services/SubtitleFormatterTests.cs`, `Pointframe.Tests/Services/TranscriptionQueueTests.cs`, `Pointframe.Tests/Services/FfmpegAudioExtractorTests.cs`, `Pointframe.Tests/Services/TranscriptModelResolverTests.cs`, `Pointframe.Tests/ViewModels/TranscriptSettingsTests.cs`.

**Files.** `Pointframe/Services/Transcription/ITranscriptionService.cs`, `Pointframe/Services/Transcription/TranscriptionService.cs`, `Pointframe/Services/Transcription/ITranscriptionQueue.cs`, `Pointframe/Services/Transcription/TranscriptionQueue.cs`, `Pointframe/Services/Transcription/IAudioExtractor.cs`, `Pointframe/Services/Transcription/FfmpegAudioExtractor.cs`, `Pointframe/Services/Transcription/ISpeechRecognizer.cs`, `Pointframe/Services/Transcription/WhisperSpeechRecognizer.cs`, `Pointframe/Services/Transcription/SubtitleFormatter.cs`, `Pointframe/Services/Transcription/TranscriptModelResolver.cs`, `Pointframe/Services/Transcription/ITranscriptModelService.cs`, `Pointframe/Services/Transcription/TranscriptModelService.cs`, `Pointframe/Services/Transcription/NullTranscriptModelService.cs`, `Pointframe/Models/TranscriptSegment.cs`, `Pointframe/Models/TranscriptionResult.cs`. See [Recording pipeline](#recording-pipeline) and [D-005](#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app).

**Lessons.**

- Lesson: Encoding.UTF8 emits a BOM, which corrupts the first SRT cue

## Decisions

### D-001 MVVM plus DI is the composition model

Decided 2026-04-09.

**Context.** WPF invites putting behavior in window code-behind, which cannot be tested without starting the app. Pointframe's flows (overlay, recording, settings, updates) need unit tests that run without a desktop session.

**Decision.**

- ViewModels (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` from CommunityToolkit.Mvvm) own state and commands.
- Every public service has an `I<Name>` interface and is registered in `AddPointframeAppServices`.
- Window code-behind holds only view-specific work: layout, HWND interop, DPI reads, focus.
- Tests target ViewModels and services with Moq; the WPF app is never started in `Pointframe.Tests`.

**Consequences.** New user-facing behavior starts as a ViewModel command plus a service, then gets a thin view binding. Constructor injection everywhere; a window with many dependencies gets a factory in the registration file (see `CreateOverlayWindow`). Manual `OnPropertyChanged()` calls are a smell; use the source generators.

**Alternatives rejected.** Code-behind-first WPF: fast to write, impossible to test without UI automation. A static service locator: hides dependencies and defeats Moq-based tests.

**Files.** `Pointframe/AppServiceRegistration.cs`. See [App bootstrap](#app-bootstrap-di-and-messaging) and [Register a service](#register-a-service).

### D-002 One knowledge base file, checked by script

Decided 2026-09-05. Replaces the 2026-04-09 decision that kept one project-wide file plus ad hoc focused docs, and a same-day trial of one file per topic with frontmatter and a generated index.

**Context.** The previous single file went stale within months: it still named `SnippingTool/` paths after the rename and linked to two docs that no longer existed, because nothing checked it against the code. The one-file-per-topic trial fixed staleness but added more files and metadata than the project needs to maintain.

**Decision.** One file, `docs/knowledge-base/knowledge-base.md`, with five fixed groups (subsystems, decisions, invariants, how-tos, references), a generated table of contents, a `**Files.**` line per section, and `- Lesson:` references into `lessons.md`. `pwsh .claude/skills/knowledge-base/knowledge-base.ps1` refreshes the table of contents and fails when a repo path, lesson heading, or internal link no longer resolves. The `/knowledge-base` skill (`add`, `update`) is how agents change the file. `CLAUDE.md` stays a pointer; `lessons.md` stays the post-mortem log.

**Consequences.** One place to read and one place to edit. Staleness is caught mechanically for paths, lessons, and links, but not for prose; running `/knowledge-base update` after a subsystem change is the human step. The file grows, so sections stay under about 80 lines and prefer tables.

**Alternatives rejected.** One file per topic with frontmatter and an index: more to maintain. A wiki or external tool: not versioned with the code and invisible to agents on a clone. Docs generated from comments: the project bans XML doc comments, and the valuable facts (why, invariants) are not in the code.

**Files.** `docs/knowledge-base/knowledge-base.md`, `.claude/skills/knowledge-base/SKILL.md`, `.claude/skills/knowledge-base/knowledge-base.ps1`.

### D-003 Recording uses one authoritative session geometry

Decided 2026-04-09.

**Context.** Mixed-DPI multi-monitor bugs kept recurring: the border, the HUD, the annotation surface, and the recorder each converted DIPs to pixels with their own idea of the scale, so they drifted apart by a few pixels or by a whole monitor offset.

**Decision.** `RecordingSessionGeometry` is computed once per recording session and carries host bounds and capture bounds in both physical pixels and DIPs, the work area, the monitor name, and the X and Y scale. Consumers call its `Map*` methods (`MapHostDipPointToScreenPixels`, `MapScreenPixelRectToHostDips`, `MapCaptureLocalDipRectToScreenPixels`, and the rest). No consumer reads `PresentationSource` DPI or `SystemParameters` to place recording visuals.

**Consequences.** A new recording visual takes the geometry as input and adds a `Map*` method if none fits. The geometry is computed after the monitor-scoped host window has settled, or every consumer inherits the wrong scale. `Pointframe.Tests/Models/RecordingSessionGeometryTests.cs` pins the mapping math; extend it with each new method.

**Alternatives rejected.** Per-window DPI reads with shared helper functions: still produced disagreements because each window's HWND could sit on a different monitor at read time.

**Files.** `Pointframe/Models/RecordingSessionGeometry.cs`, `Pointframe/Services/Recording/ScreenRecordingService.cs`. See [Recording pipeline](#recording-pipeline) and [DPI coordinate systems](#dips-and-physical-pixels-are-converted-explicitly-per-monitor).

**Lessons.**

- Lesson: Recording mode must use one authoritative geometry model
- Lesson: Mixed-DPI multi-monitor capture features need PerMonitorV2 process DPI awareness

### D-004 Native libraries ship loose and the installer packages them

Decided 2026-09-06.

**Context.** Whisper.net resolves its native runtime by probing `runtimes\win-x64` on disk, which the self-extract directory of a single-file build is not. Setting `IncludeNativeLibrariesForSelfExtract` to `false` in `Pointframe/Properties/PublishProfiles/win-x64.pubxml` fixes that, but the flag is all-or-nothing: it pushes *every* native out of the bundle, not just Whisper's.

**Decision.** Keep the flag `false` and make the installer package the publish output: `{#PublishDir}\*.dll` for the six loose WPF and SQLite natives, plus `{#PublishDir}\runtimes\win-x64\*` for the four Whisper DLLs. The arm64 and x86 copies the SDK emits are not shipped; this is an x64 build and they would only add weight.

**Consequences.** A publish-property change that alters what lands next to the exe now has to change the installer file list in the same commit. `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs` asserts the natives exist after install, so the failure names the missing file rather than a vague launch error. `{app}\runtimes` is removed on uninstall.

**Alternatives rejected.** Leaving the flag `true` and letting Whisper load from the self-extract directory: its loader does not look there. Copying only Whisper's DLLs out of the bundle: the flag has no per-library granularity.

**Files.** `Pointframe/Properties/PublishProfiles/win-x64.pubxml`, `installer/Pointframe.iss`, `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs`. See [Everything emitted next to the exe must be in the installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list) and [CI, CD, and versioning](#ci-cd-and-versioning).

**Lessons.**

- Lesson: Turning off single-file native bundling silently breaks the installer, not the dev build

### D-005 The speech model is delivered by both the installer and the app

Decided 2026-09-06.

**Context.** `ggml-base.en.bin` is about 141 MB, far too large to bundle. Delivering it only as an unchecked installer component means anyone who skips the checkbox has no way to get it later: they enable transcripts, record, and nothing happens. Delivering it only in-app leaves setup unable to prepare a machine up front.

**Decision.** Ship both. The installer's optional `whispermodel` task downloads to `{app}\models\`; `SettingsViewModel.DownloadTranscriptModelCommand` downloads to `%LOCALAPPDATA%\Pointframe\models\`. `TranscriptModelResolver` probes, in order: the `AppContext` override, `{app}\models\`, next to the binary, then the per-user folder.

**Consequences.** The per-user copy survives upgrades and reinstalls because it lives outside `{app}`; the installer copy does not, and is removed on uninstall. The installer runs elevated, so it must not write to `{localappdata}` — that would resolve to the administrator's profile, not the installing user's. Settings shows which prerequisite is missing and offers the download, so a skipped component is recoverable. Model URLs live in two places, `installer/Pointframe.iss` and `TranscriptModelService`, and change together.

**Alternatives rejected.** Installer-only, the original plan: unchecked by default, so most installs would never have had the model, with no in-app remedy. In-app only: setup cannot pre-provision a machine, which matters for managed deployments.

**Files.** `Pointframe/Services/Transcription/TranscriptModelResolver.cs`, `Pointframe/Services/Transcription/TranscriptModelService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `installer/Pointframe.iss`. See [Recording transcription](#recording-transcription) and [Runtime paths and external binaries](#runtime-paths-and-external-binaries).

## Invariants

### Undo groups are added only on commit

**Rule.** The undo stack in `AnnotationViewModel` grows in exactly one place: when a drag commits. A shape handler calls the `trackElement` callback only from `Commit`, never from `Begin` or `Update`. Redo re-adds the same group; nothing else adds to the stack.

**Why.** A drag is one user-visible action. If the draft element is tracked at `Begin`, Ctrl+Z restores half-drawn shapes and the redo stack fills with junk. Text and Callout replace their `TextBox` with a `TextBlock` through `ReplaceTrackedElement`, which relies on the group containing only committed elements.

**Enforced by.** `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs` and `Pointframe.Tests/Services/AnnotationCanvasRendererTests.cs`. There is no analyzer; review any new call to `TrackElement` or the undo stack by hand.

**Symptoms when violated.** Undo restores a partial shape or removes two shapes at once. `UndoCount` disagrees with what the user drew. Number badges renumber incorrectly after undo because the counter reset runs per group.

**Files.** `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs`, `Pointframe/Services/Annotation/Handlers/IAnnotationShapeHandler.cs`. See [Annotation engine](#annotation-engine) and [Add an annotation tool](#add-an-annotation-tool).

### Settings are read at the point of use and persisted through three files

**Rule.**

1. Read `IUserSettingsService.Current.X` where the value is used. Do not copy it into a field or a constructor parameter.
2. Partial changes go through `Update(settings => settings.X = ...)`, which clones, mutates, and saves.
3. A new property exists in two places in one change: the `UserSettings` property with its default, and the read-back in `SettingsViewModel.Save()`. `Clone` needs no edit — it round-trips through the persistence serializer. The Settings window binding is a third, UI-only step.

**Why.** The settings service is a singleton and the user can change values while the app runs, so a cached field goes stale until restart. `Clone` serializes and deserializes through the same converters as save and load, so it covers new properties automatically and drops exactly what the on-disk format would drop. A property missing from `SettingsViewModel.Save()` is still reset to its default the next time the user presses Save.

**Enforced by.** `SettingsRoundTripTests` populates every property of `UserSettings` by reflection and round-trips it through save, load, `Update`, and `SettingsViewModel.Save`. A property missing from `Save()`, or one whose populated value equals its default, fails this test. Reading at the point of use is not enforced by tests; review for `Current` captured in fields.

**Symptoms when violated.** A setting reverts after restart or after saving an unrelated setting. A hotkey or theme change takes effect only after restart.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`. See [User settings](#user-settings) and [Add a user setting](#add-a-user-setting).

### Recording width and height are even

**Rule.** `ScreenRecordingService.Start` truncates an odd width or height by one pixel and aborts with a logged error if the result is too small. Anything that positions a visual against the capture (border, annotation surface, cursor mapping) uses the same even size, not the user's raw selection.

**Why.** Frames are handed to ffmpeg as JPEG, whose minimum coded unit needs even dimensions, and the MP4 encoder's 4:2:0 chroma subsampling needs the same. An odd dimension makes the encoder fail or produce corrupt output.

**Enforced by.** `Pointframe.Tests/Services/ScreenRecordingServiceTests.cs`: `Start_WithOddDimensions_TruncatesToEven` and `Start_OddDimensions_TruncatesToEvenBeforeFactory`. `RecordingSessionGeometry` does not round for you; callers pass the truncated size in.

**Symptoms when violated.** ffmpeg exits immediately, or the MP4 has green or shifted edges. The recording border is one pixel wider than the recorded area.

**Files.** `Pointframe/Services/Recording/ScreenRecordingService.cs`, `Pointframe/Models/RecordingSessionGeometry.cs`. See [Recording pipeline](#recording-pipeline) and [D-003](#d-003-recording-uses-one-authoritative-session-geometry).

### DIPs and physical pixels are converted explicitly per monitor

**Rule.**

```
physical_px = dip * scale
dip         = physical_px / scale
```

`scale` belongs to one monitor. Get it from the window's `PresentationSource` after the HWND exists, or from `MonitorDpiHelper.GetMonitorScale(point)` for a screen location. Assign a window's bounds before `Show()` so WPF creates the HWND on the intended monitor. Recording visuals do not convert at all; they call `RecordingSessionGeometry`.

**Why.** The process declares `PerMonitorV2` in `Pointframe/app.manifest`, so each monitor has its own scale and a window's DPI changes when it moves. `SelectionSession` creates one window per monitor precisely so each can use its own scale. The virtual-desktop-wide selection runs system-aware through `DpiAwarenessScope` because one window spanning monitors cannot have one correct scale.

**Enforced by.** `Pointframe.Tests/DpiAwarenessScopeTests.cs`, `Pointframe.Tests/Models/RecordingSessionGeometryTests.cs`, `Pointframe.Tests/OverlayWindowLayoutTests.cs`. Mixed-DPI behavior has no CI coverage; test on a real two-monitor setup with different scales before merging overlay, pin, or recording placement changes.

**Symptoms when violated.** The overlay is cut off or offset on the secondary monitor when the primary has a higher scale. The recording border or HUD lands on the wrong monitor or is off by the scale ratio. Active-window capture selects a region shifted by the difference between two monitors' scales.

**Files.** `Pointframe/Native/MonitorDpiHelper.cs`, `Pointframe/Native/DpiAwarenessScope.cs`, `Pointframe/Views/OverlayWindow.xaml.cs`, `Pointframe/Services/Capture/SelectionSession.cs`, `Pointframe/Models/RecordingSessionGeometry.cs`, `Pointframe/app.manifest`. See [Capture overlay](#capture-overlay-and-selection), [Recording pipeline](#recording-pipeline), and [D-003](#d-003-recording-uses-one-authoritative-session-geometry).

**Lessons.**

- Lesson: WPF PerMonitorV2: set window bounds before Show(), not in OnSourceInitialized
- Lesson: Mixed-DPI multi-monitor capture features need PerMonitorV2 process DPI awareness
- Lesson: Full-desktop selection overlays are safer in a system-aware DPI context while monitor-scoped recording hosts stay PerMonitorV2
- Lesson: Active-window capture must map Win32 screen coordinates into overlay space instead of dividing by one overlay DPI

### Everything emitted next to the exe must be in the installer file list

**Rule.** Whatever `dotnet publish` leaves in `Pointframe/bin/publish/win-x64/` beside `Pointframe.exe` is required at runtime and must appear in the `[Files]` section of `installer/Pointframe.iss`. Changing a publish property that alters that set changes the installer in the same commit.

**Why.** The build is self-contained and single-file, so it is tempting to read the installer as needing only the exe — the script said exactly that in a comment for months. It is only true while every native is bundled. `IncludeNativeLibrariesForSelfExtract` controls that for all natives at once; see [D-004](#d-004-native-libraries-ship-loose-and-the-installer-packages-them).

**Enforced by.** `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs` installs silently, asserts each required native exists under the install directory, launches the app, and uninstalls. It is opt-in: set `POINTFRAME_RUN_INSTALLER_SMOKE=1` and run elevated.

**Symptoms when violated.** Nothing fails on a developer machine, where the DLLs resolve from other locations, and CI stays green because it never installs. On a clean machine the installed app dies at startup: without `e_sqlite3.dll` the EF Core migration throws and the user is told the database migration failed, which points away from the real cause. Verify by inspecting the *installed* directory, never `bin\`.

**Files.** `installer/Pointframe.iss`, `Pointframe/Properties/PublishProfiles/win-x64.pubxml`, `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs`, `installer/build-installer.ps1`.

**Lessons.**

- Lesson: Turning off single-file native bundling silently breaks the installer, not the dev build

## How-tos

### Add an annotation tool

**When.** A new drawing primitive the user picks from the toolbar. Not for style presets (those are `AnnotationStylePreset`) and not for actions that do not draw (those are `OverlayViewModel` commands).

**Steps.**

1. Add the value to the `AnnotationTool` enum in `Pointframe/Models/AnnotationTool.cs`.
2. Add a sealed record to `Pointframe/Models/ShapeParameters.cs` with the geometry and style the tool needs.
3. Return it from `AnnotationViewModel.TryGetShapeParameters()` for the new tool.
4. Create `Pointframe/Services/Annotation/Handlers/<Name>ShapeHandler.cs` implementing `IAnnotationShapeHandler`. Draft in `Begin` and `Update`; add final elements and call `trackElement` only in `Commit`; remove the draft in `Cancel`. Copy `RectShapeHandler` for a simple drag shape or `TextShapeHandler` for an editable one.
5. Register it in the `_handlers` dictionary in `AnnotationCanvasRenderer`, passing `GetShapeParameters` and any ViewModel callbacks it needs.
6. Put pure math in `IAnnotationGeometryService` and `AnnotationGeometryService`, not in the handler, so it is unit-testable without WPF.
7. Add the toolbar button in `Pointframe/Views/OverlayWindow.xaml` with an `AutomationProperties.AutomationId`. Decide whether the tool is allowed during recording; the HUD derives its list from the annotation allowlist.
8. Tests: a handler test under `Pointframe.Tests/Services/Handlers/`, a `TryGetShapeParameters` case in `AnnotationViewModelTests`, geometry cases in `AnnotationGeometryServiceTests`.
9. Smoke coverage: add the id to `Pointframe.AutomationTests/Support/AutomationIds.cs` and the tool to `AnnotationToolSmokeTests.cs` (and `RecordingAnnotationToolSmokeTests.cs` if allowed while recording).
10. Telemetry records the tool name through the existing `annotation_tool` property; check `TelemetryEventCatalog` only if the event constrains allowed values.

**Verify.**

```powershell
dotnet format Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~Annotation"
```

Then draw with the tool, undo once, redo once, and export. The shape must survive export, and undo must remove exactly that one shape.

**Files.** `Pointframe/Models/AnnotationTool.cs`, `Pointframe/Models/ShapeParameters.cs`, `Pointframe/ViewModels/AnnotationViewModel.cs`, `Pointframe/Services/Annotation/Handlers/IAnnotationShapeHandler.cs`, `Pointframe/Services/Annotation/AnnotationCanvasRenderer.cs`, `Pointframe/Services/Annotation/IAnnotationGeometryService.cs`, `Pointframe/Services/Annotation/AnnotationGeometryService.cs`, `Pointframe/Views/OverlayWindow.xaml`, `Pointframe.AutomationTests/Support/AutomationIds.cs`, `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs`. See [Annotation engine](#annotation-engine) and [Undo groups are added only on commit](#undo-groups-are-added-only-on-commit).

### Add a user setting

**When.** Any value the user chooses once and expects to survive restart.

**Steps.**

1. `Pointframe/Models/UserSettings.cs`: add the property with its default in the initializer. Use an enum for choices, not strings.
2. `Pointframe/ViewModels/SettingsViewModel.cs`: add an `[ObservableProperty]` field, load it from `Current` in the constructor, write it back in `Save()`.
3. `Pointframe/Views/SettingsWindow.xaml`: bind a control in the right section (Capture, Recording, Annotation, Shortcuts, App) and give it an `AutomationProperties.AutomationId` that matches a new constant in `Pointframe.AutomationTests/Support/AutomationIds.cs`.
4. Consumers read `IUserSettingsService.Current.<Name>` at the point of use.
5. If the setting has a hidden or derived companion value, make Restore Defaults reset it directly.

**Verify.**

```powershell
dotnet format Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~Settings"
```

`SettingsRoundTripTests` fails if step 2 was skipped, and also if the test fixture does not set the new property to a non-default value. Then change the value in the running app, restart, and confirm it persisted.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe/Views/SettingsWindow.xaml`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`. See [Settings persistence](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) and [User settings](#user-settings).

### Register a service

**When.** Any class that touches the OS, the file system, a process, the network, or holds app-wide state. If a ViewModel would otherwise call a static API, wrap the API in a service.

**Steps.**

1. Create `I<Name>.cs` and `<Name>.cs` in the matching folder under `Pointframe/Services/` (`Annotation`, `Capture`, `Infrastructure`, `Messaging`, `Recording`, `Update`). Namespaces do not follow folders: most services use `Pointframe.Services`, messaging uses `Pointframe.Services.Messaging`, shape handlers use `Pointframe.Services.Handlers`. Follow the neighbors.
2. Register in `AddPointframeAppServices` in `Pointframe/AppServiceRegistration.cs`:
   - `AddSingleton` for state, caches, OS handles, hooks, and anything a hosted service uses.
   - `AddTransient` for per-operation objects, disposables, ViewModels, and windows.
   - Never `AddScoped` in the app project; scoped is reserved for EF Core in `Pointframe.Data`.
   - Needs runtime arguments? Register a `Func<TArg, TService>` factory like the existing `TrimViewModel` and `RecordingHudViewModel` ones.
3. Inject through the constructor. If `OverlayWindow` needs it, add the parameter to `CreateOverlayWindow` in the same file.
4. Tests: `new Mock<I<Name>>()`, `Setup(...)`, `Verify(..., Times.Once)`. The service's own tests go in `Pointframe.Tests/Services/<Name>Tests.cs`.
5. Logging: inject `ILogger<T>`; Serilog is configured on the host.

**Verify.**

```powershell
dotnet build Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~AppTests"
```

`Pointframe.Tests/AppTests.cs` builds the container and resolves the core services, so a missing registration fails there before it fails at runtime.

**Files.** `Pointframe/AppServiceRegistration.cs`. See [App bootstrap](#app-bootstrap-di-and-messaging) and [D-001](#d-001-mvvm-plus-di-is-the-composition-model).

## References

### Runtime paths and external binaries

**Per-user files.** Root: `%LOCALAPPDATA%\Pointframe` (`AppPaths.LocalAppDataDirectory`).

| File | Purpose | Owner |
|---|---|---|
| `logs\pointframe-<date>.log` | Serilog rolling log; `Logging:RetainedFileCountLimit` in `appsettings.json` caps retained files | `AppPaths.RollingLogPath` |
| `settings.json` | User settings | `UserSettingsService` |
| `pointframe.db` | SQLite database for the capture text cache | `AppPaths.PointframeDatabasePath`, `Pointframe.Data` |

Screenshots and recordings go to the folder chosen in settings.

**ffmpeg.** `FfmpegResolver.Resolve()` order:

1. `AppContext` data key `SnippingTool.FfmpegPath` (set by tests or a host).
2. `ffmpeg.exe` next to the application binary. The installer's optional "ffmpeg" component downloads a GPL win64 build to this location.
3. `Assets\ffmpeg\ffmpeg.exe` under the binary folder.
4. Bare `ffmpeg.exe`, resolved through `PATH`.

`ResolveRequired(purpose)` throws `FileNotFoundException` with a user-facing message when an explicit location is missing. MP4 recording, GIF export, trim, and video watermark all need it.

**Configuration and overrides.**

| Key | Where | Effect |
|---|---|---|
| `ApplicationInsights:ConnectionString` | `appsettings.json` | Empty in source; CD injects the real value |
| `Logging:RetainedFileCountLimit` | `appsettings.json` | Rolling log retention |
| Any key above | `appsettings.Local.json` | Optional local override, loaded after `appsettings.json` and copied to output only if present |
| `SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH` | environment | Redirects `settings.json` for automation tests |
| Automation launch options | command line, parsed by `AutomationLaunchOptions.Parse` | Drives `Pointframe.AutomationTests` scenarios |

Identifiers still prefixed `SnippingTool` are pre-rename names kept for compatibility. Renaming one touches the automation project and the installer together.

**Files.** `Pointframe/Services/Infrastructure/AppPaths.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/Services/Recording/FfmpegResolver.cs`, `Pointframe/appsettings.json`, `Pointframe/App.xaml.cs`, `Pointframe/Automation/AutomationLaunchOptions.cs`, `installer/Pointframe.iss`.

### CI, CD, and versioning

**Workflows.**

| Workflow | Trigger | Does |
|---|---|---|
| `.github/workflows/ci.yml` | push to `master`, `feature/**`, `fix/**`; PR to `master` | `dotnet tool restore`, build `Pointframe.Tests` in Release, `dotnet format Pointframe/Pointframe.csproj --verify-no-changes`, `dotnet test` with `--filter "Category!=Integration"`, upload Cobertura to Codecov |
| `.github/workflows/cd.yml` | `workflow_run` after a successful CI on `master` | compute the version with nbgv, inject the App Insights connection string and verify it, publish self-contained single-file, sign the exe when the certificate secret exists, build the Inno Setup installer from `installer/Pointframe.iss`, sign it, upload `Pointframe-<version>-x64-Setup`, create the GitHub Release tagged `v<version>` |
| `.github/workflows/desktop-automation.yml` | manual (`workflow_dispatch`) | runs `Pointframe.AutomationTests` UI automation on a Windows runner |
| `.github/workflows/winget-release.yml` | after CD completes on `master`, or manual with a version | submits the winget manifest update |
| `.github/workflows/codeql.yml`, `.github/workflows/release-drafter.yml`, `.github/workflows/dependabot-auto-merge.yml` | as named | static analysis, release-notes draft, Dependabot merges |
| `.github/workflows/pages.yml` | push to `master` | deploys the website from `website/` |

**Gates a change must pass locally.**

```powershell
dotnet format Pointframe/Pointframe.csproj --verify-no-changes
dotnet test Pointframe.Tests/Pointframe.Tests.csproj
```

The format gate covers the main project only. Do not run `dotnet format` on `Pointframe.Tests`; it would rewrite many unrelated files.

**Versioning.**

- `version.json` holds `major.minor`. Nerdbank.GitVersioning adds the patch from commit height, so a full clone (`fetch-depth: 0`) is required.
- `publicReleaseRefSpec` marks `master` and `v*` tags as public; other branches get a pre-release suffix.
- Bump `version.json` to start a new minor; never hand-edit a patch.
- `dotnet-tools.json` pins `nbgv` and `dotnet-ef`; run `dotnet tool restore` after cloning.

**Installer and packaging.**

- `installer/Pointframe.iss` is the Inno Setup script; `installer/build-installer.ps1` and `installer/test-installer.ps1` build and check it locally. Its ARP `AppPublisher` is the source of truth that the winget manifests must mirror.
- `winget/` holds the winget manifests; `packaging/scoop/` holds the scoop manifest.
- Renaming anything in the delivery path (exe name, installer name, package id) touches the workflows, the installer, the winget manifests, and the updater's asset-name expectation together.

**Files.** `.github/workflows/ci.yml`, `.github/workflows/cd.yml`, `.github/workflows/desktop-automation.yml`, `.github/workflows/winget-release.yml`, `version.json`, `dotnet-tools.json`, `installer/Pointframe.iss`. See [Update flow](#update-flow) and [Telemetry](#telemetry).

**Lessons.**

- Lesson: Rename migrations must update hardcoded delivery paths in workflows and installer assets together
- Lesson: WinGet ARP publisher matching must follow the installer metadata exactly
- Lesson: Winget package renames need a one-time upstream bootstrap before automated updates can work
- Lesson: Renamed winget packages need a distinct installer identity if they are published as a new package ID
- Lesson: Coverage workflows should run for fix branches and not hard-require a Codecov token on public repos
