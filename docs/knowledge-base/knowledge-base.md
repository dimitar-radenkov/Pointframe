# Pointframe Knowledge Base

Durable, agent-readable project knowledge. This file holds what is cross-cutting: how the app is composed, rules every area must keep, shared recipes, paths, and CI, plus generated indexes of features and decisions. Each feature area has its own file under `docs/knowledge-base/features/`; open one only when the task touches that area. Start with the [Feature index](#feature-index) or run `/kb-read <file, area, F-NN, or topic>` (`pwsh scripts/kb.ps1 read ...`).

Last full review against the code: 2026-09-05.

## How to maintain this file

**What belongs where.**

| Knowledge | Goes to |
|---|---|
| Composition, DI, messaging; rules that hold for every area; recipes any area uses; paths, pipelines, CI | This file |
| A decision that spans areas | `docs/knowledge-base/decisions.md` |
| One area's features, flows, entry points, area-only invariants, recipes, and decisions, its lessons | `docs/knowledge-base/features/<area>.md` |
| Bug post-mortems (Problem, Root cause, What fixed it, Takeaway) | `lessons.md`, then a `- Lesson:` line in the owning section |
| Roadmap, plans, task status | `plan/` (local-only, not in git) |
| Contributor setup | `docs/developer-guide.md` |
| Per-session instructions | `CLAUDE.md`, one line per topic pointing here |
| Test counts, PR numbers, "verified on my machine" | The PR description |

**This file's structure.** A generated [Feature index](#feature-index), the [File map](#file-map), then five fixed groups, each a `##` heading with one `###` section per topic (Decisions holds only a generated index):

| Group | Section layout |
|---|---|
| Subsystems | Responsibility, Entry points, Flow, Key types, Invariants, Tests, Files, Lessons |
| Decisions | A generated index only. Each decision is a `## D-NNN Title` section in `decisions.md` or its area file: decided date, Context, Decision, Consequences, Alternatives rejected, Files. Numbers are global and never reused; a reversed decision stays with "Superseded by D-NNN" on its first line |
| Invariants | Rule, Why, Enforced by, Symptoms when violated, Files |
| How-tos | When, Steps, Verify, Files |
| References | Tables or short lists, each fact with its source, Files |

**A feature area file's structure.** `# <Area>`, a one-line scope, then `## Features`: one row per user-facing feature, `| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |`, with a stable `F-NN` ID that is never renumbered or reused. Then one `##` section per topic, using the Subsystems, Invariants, or How-tos layout above. A new area gets a new file; split a file that passes about 150 lines.

**Conventions the script checks.** In every file: repo paths in backticks exist; `- Lesson: <exact heading from lessons.md>` bullets match `lessons.md`; links such as `[Recording pipeline](features/recording.md#recording-pipeline)` resolve to a heading in the target file; a section has at most one `**Files.**` line; no two headings in one file share an anchor. Feature IDs are unique across files; every feature names an entry point and links a section; every telemetry event named exists in `TelemetryEvents`, and every event there belongs to some feature; every smoke test in `Pointframe.AutomationTests/Smoke/` belongs to some feature. Every File map pattern matches a tracked file, and every tracked file matches a row. A `lessons.md` entry that no section links is reported as a warning, because `kb.ps1 read` and the hook never show it. The table of contents and the Feature index are generated. CI runs `kb.ps1 check -NoFix`, and the project hook in `.claude/settings.json` runs `kb.ps1 hook` when an agent reads or edits a file.

**Writing rules.** Say why, not only what. Replace stale text in place; never append a correction below an old statement. Prefer a table or a short list over prose. Keep a section under about 80 lines. Absolute dates only. No status badges or task journals.

**Maintenance.** Three skills, all backed by `scripts/kb.ps1`. Edit them only under `.claude/skills/kb-*`; `check` regenerates the Codex copies in `.agents/skills/`.

| Skill | Does | Script |
|---|---|---|
| `/kb-read <file, area, F-NN, or topic>` | Lists the sections, features, and lessons to read | `pwsh scripts/kb.ps1 read <target> [-All]` |
| `/kb-write [topic]` | Adds or updates knowledge; with no topic, sweeps the current diff | `pwsh scripts/kb.ps1 changed [-Base <ref>]` |
| `/kb-check` | Refreshes the generated blocks, checks every file, fixes what it reports | `pwsh scripts/kb.ps1 check` (CI: `check -NoFix`) |

When a change adds a folder, a project, or a file that belongs to a different section than its folder's row says, add or narrow a File map row in the same change. When it adds, removes, or rewires a user-facing feature, a telemetry event, or a smoke test, update the feature's row in its area file in the same change.

## Contents

<!-- toc -->

- [Feature index](#feature-index)
- [File map](#file-map)
- [Subsystems](#subsystems)
  - [App bootstrap, DI, and messaging](#app-bootstrap-di-and-messaging)
- [Decisions](#decisions)
- [Invariants](#invariants)
  - [Settings are read at the point of use and persisted through three files](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files)
  - [DIPs and physical pixels are converted explicitly per monitor](#dips-and-physical-pixels-are-converted-explicitly-per-monitor)
  - [Everything emitted next to the exe must be in the installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list)
- [How-tos](#how-tos)
  - [Add a user setting](#add-a-user-setting)
  - [Register a service](#register-a-service)
- [References](#references)
  - [Runtime paths and external binaries](#runtime-paths-and-external-binaries)
  - [CI, CD, and versioning](#ci-cd-and-versioning)

<!-- /toc -->

## Feature index

Generated from the area files under `docs/knowledge-base/features/`. Each feature's row there says how it is triggered, its entry point, telemetry events, tests, and what to read.

<!-- features -->

- [Capture](features/capture.md): F-01 Region snip; F-02 Whole-screen snip; F-03 Clean-window snip; F-04 Capture delay countdown; F-05 Open an image in the overlay; F-06 Copy, save, save as; F-07 Pin a screenshot; F-08 Copy text with an OCR lasso; F-09 Beautify a screenshot; F-10 Watermarks on screenshots and videos
- [Annotation](features/annotation.md): F-11 Annotation tools: arrow, line, rectangle, circle, pen, highlight, text, number, blur, callout, pixel ruler; F-12 Undo and redo; F-13 Color picker; F-14 Annotation style presets
- [Recording](features/recording.md): F-15 Region recording; F-16 Whole-screen recording; F-17 Recording HUD: pause, stop, minimize, expand; F-18 Microphone audio; F-19 Live annotation while recording; F-20 Blur redaction burned into the video; F-21 Cursor effects; F-22 Trim a recording; F-23 Export a recording as GIF
- [Transcription](features/transcription.md): F-24 Transcripts and subtitles
- [Capture library](features/library.md): F-25 Capture library: browse, search, open; F-26 Capture catalog indexing and import
- [Tray menu](features/tray.md): F-27 Recent captures and recordings menus; F-28 Open snips, videos, and logs folders
- [Settings](features/settings.md): F-29 Settings window; F-30 Custom global hotkeys; F-31 Light and dark theme
- [Updates and About](features/updates.md): F-32 Update check and install; F-33 About window
- [Telemetry](features/telemetry.md): F-34 App lifecycle and usage telemetry
- [CLI and MCP](features/cli-mcp.md): F-35 CLI: displays, windows, capture, ocr, capture-window, ocr-window, record; F-36 MCP capture, OCR, recording, and library tools; F-37 MCP desktop testing tools

<!-- /features -->

## File map

Answers "I am about to change this file: what must I read first?" Every tracked file matches at least one row; **every** matching row applies, and rows run from coarse to fine. A row names the sections to read before editing; the lessons are inside those sections.

Fastest route: let the script resolve it. It prints the matching rows, the features that use the file, the linked sections with their line numbers, every other section that names the exact file, and the most relevant lessons:

```powershell
pwsh scripts/kb.ps1 read Pointframe/Views/OverlayWindow.Recording.cs
```

**Pattern syntax** (anchored at the repo root like `.gitignore`; unlike CODEOWNERS, where the last match wins, every matching row applies): `*` matches within one folder, `**` crosses folders, `?` is one character. A pattern without `/` matches only root files. Several patterns in one cell are separated by commas. `—` in "Read first" means no section owns the area yet.

**Rules the script checks.** Every pattern matches at least one tracked file. Every tracked file matches at least one row, so a new top-level folder or project fails the check until it gets a row. No row is redundant: a row whose files and links all sit inside another row is an error. Descriptions stay under 160 characters; explanation belongs in a section. Every link points at a heading. Tests are mapped by folder only: a test file is named after the class it covers, so look up that class.

**Repository and build**

| Path | What lives here | Read first |
|---|---|---|
| `*`, `.vscode/**` | Root config: solution, central package versions, NBGV `version.json`, local tools, editor and MCP client config | [CI, CD, and versioning](#ci-cd-and-versioning) |
| `CLAUDE.md`, `CONTRIBUTING.md`, `lessons.md`, `docs/**`, `.claude/**`, `.agents/skills/kb-*/**`, `scripts/kb.ps1` | Agent and contributor docs, this file, the kb skills and their script, the project hook, and the generated Codex skill copies | [How to maintain this file](#how-to-maintain-this-file), [D-006](decisions.md#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area) |
| `docs/cli/**`, `docs/mcp-desktop-testing/**`, `README.md` | CLI and MCP user docs; the DocsSync tests fail when they drift from the code | [Standalone CLI and MCP automation](features/cli-mcp.md#standalone-cli-and-mcp-automation) |
| `docs/appinsights*` | Kusto queries and the workbook template | [Telemetry](features/telemetry.md#telemetry-pipeline) |
| `.github/**`, `winget/**`, `website/**` | Workflows, Dependabot, release drafter, winget manifests, the GitHub Pages site | [CI, CD, and versioning](#ci-cd-and-versioning) |
| `installer/**`, `Pointframe/Properties/**` | Inno Setup script, installer build and smoke scripts, publish profile | [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them), [Installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list), [D-005](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app) |
| `Directory.Packages.props`, `Pointframe/Pointframe.csproj` | Package versions and app project references; a package that ships a native binary changes the installer file list | [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them), [Installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list) |
| `packaging/**` | CLI and MCP zip packaging, scoop manifest, MCP stdio smoke script | [Standalone CLI and MCP automation](features/cli-mcp.md#standalone-cli-and-mcp-automation), [MCP command list](features/cli-mcp.md#the-mcp-command-list-resource-must-name-every-registered-tool) |

**WPF app: `Pointframe/`**

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe/*`, `Pointframe/Assets/**`, `Pointframe/Automation/**` | Host startup, DI registration, project file, automation launch options | [App bootstrap](#app-bootstrap-di-and-messaging), [D-001](decisions.md#d-001-mvvm-plus-di-is-the-composition-model), [Register a service](#register-a-service) |
| `Pointframe/app.manifest`, `Pointframe/Native/**` | PerMonitorV2 manifest, Win32 interop, per-monitor DPI helpers | [DIPs and physical pixels](#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/appsettings.json`, `Pointframe/Services/Infrastructure/AppPaths.cs` | Configuration and on-disk locations | [Runtime paths and external binaries](#runtime-paths-and-external-binaries) |
| `Pointframe/Services/Messaging/**` | Event aggregator and message records | [App bootstrap](#app-bootstrap-di-and-messaging) |
| `Pointframe/Services/Infrastructure/**` | Cross-cutting singletons: dialogs, files, processes, hotkeys, tray, theme, errors | [App bootstrap](#app-bootstrap-di-and-messaging), [Register a service](#register-a-service) |
| `Pointframe/Services/Infrastructure/*UserSettings*`, `Pointframe/Services/Infrastructure/*Theme*`, `Pointframe/Services/Infrastructure/*Hotkey*`, `Pointframe/Themes/**` | Settings persistence, theme switching, hotkey bindings | [User settings](features/settings.md#user-settings), [Settings invariant](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) |
| `Pointframe/Services/Infrastructure/*Telemetry*` | Telemetry client, event catalog, heartbeat, activation | [Telemetry](features/telemetry.md#telemetry-pipeline) |
| `Pointframe/Services/Infrastructure/*Ocr*` | WPF-side OCR for the overlay lasso; library search reads text indexed by the catalog worker | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection), [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe/Services/Infrastructure/*Microphone*`, `Pointframe/Services/Infrastructure/*MouseHook*` | WASAPI microphone enumeration; global mouse hook used by recording cursor effects, the overlay, and capture launch | [Recording pipeline](features/recording.md#recording-pipeline) |
| `Pointframe/Services/Infrastructure/*AppVersion*`, `Pointframe/Services/Update/**` | Release check, download, version source | [Update flow](features/updates.md#update-flow) |
| `Pointframe/Services/Capture/**` | Capture launch, selection session, screen and window capture, clipboard, image files | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection) |
| `Pointframe/Services/Capture/*CaptureLibrary*`, `Pointframe/Services/Capture/*CaptureTextLookup*` | Library listing, search, OCR text cache | [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe/Services/Annotation/**` | Canvas renderer, geometry, interaction controller, shape handlers | [Annotation engine](features/annotation.md#annotation-engine), [Undo invariant](features/annotation.md#undo-groups-are-added-only-on-commit), [Add an annotation tool](features/annotation.md#add-an-annotation-tool) |
| `Pointframe/Services/Recording/**` | Recording session, ffmpeg writer, HUD and overlay coordinators, redaction, GIF, trim, watermarks | [Recording pipeline](features/recording.md#recording-pipeline), [Even dimensions](features/recording.md#recording-width-and-height-are-even) |
| `Pointframe/Services/Recording/*Coordinator.cs`, `Pointframe/Services/Recording/RecordingOverlayNativeInterop.cs`, `Pointframe/Views/OverlayWindow.RecordingHud.cs`, `Pointframe/Views/RecordingOverlay*` | Placing the recording HUD, border, and annotation surface on the target monitor | [Recording pipeline](features/recording.md#recording-pipeline), [DIPs and physical pixels](#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/Services/Recording/*Ffmpeg*`, `Pointframe/Services/Recording/FFMpeg*` | ffmpeg discovery and the video writer process | [Runtime paths and external binaries](#runtime-paths-and-external-binaries) |
| `Pointframe/Services/Recording/Beautif*`, `Pointframe/Services/Recording/*ScreenshotWatermark*` | Screenshot post-processing reached from the overlay toolbar | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection), [User settings](features/settings.md#user-settings) |
| `Pointframe/Services/Transcription/**`, `Pointframe/Models/Transcript*` | Whisper transcription queue, model resolution, subtitles | [Recording transcription](features/transcription.md#recording-transcription), [D-005](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app) |
| `Pointframe/Models/**`, `Pointframe/ViewModels/**`, `Pointframe/Views/**` | Models, ViewModels, and windows; the finer rows below name the owning subsystem | [D-001](decisions.md#d-001-mvvm-plus-di-is-the-composition-model) |
| `Pointframe/Models/Annotation*`, `Pointframe/Models/ShapeParameters.cs`, `Pointframe/ViewModels/Annotation*`, `Pointframe/ViewModels/RecordingAnnotation*`, `Pointframe/Views/OverlayWindow.RecordingAnnotation.cs` | Tools, shape parameters, style presets, annotation state and undo | [Annotation engine](features/annotation.md#annotation-engine), [Undo invariant](features/annotation.md#undo-groups-are-added-only-on-commit), [Add an annotation tool](features/annotation.md#add-an-annotation-tool) |
| `Pointframe/Models/Selection*`, `Pointframe/Models/BeautifyBackground.cs`, `Pointframe/ViewModels/Overlay*`, `Pointframe/ViewModels/Beautifier*`, `Pointframe/Views/OverlayWindow*`, `Pointframe/Views/Overlay*`, `Pointframe/Views/Selection*`, `Pointframe/Views/PinnedScreenshot*`, `Pointframe/Views/Beautifier*` | Overlay window partials, selection windows, pin and beautify | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection), [DIPs and physical pixels](#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/Models/Recording*`, `Pointframe/Services/Messaging/Recording*`, `Pointframe/Services/Messaging/Trim*`, `Pointframe/ViewModels/RecordingHud*`, `Pointframe/ViewModels/Trim*`, `Pointframe/Views/OverlayWindow.Recording*`, `Pointframe/Views/RecordingOverlay*`, `Pointframe/Views/Countdown*`, `Pointframe/Views/Trim*` | Recording geometry, events, redaction regions, HUD, countdown, trim | [Recording pipeline](features/recording.md#recording-pipeline) |
| `Pointframe/Models/RecordingSessionGeometry.cs` | The one geometry model per recording session | [D-003](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry), [Even dimensions](features/recording.md#recording-width-and-height-are-even), [DIPs and physical pixels](#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/Models/UserSettings.cs`, `Pointframe/Models/Settings*`, `Pointframe/Models/Hotkey*`, `Pointframe/Models/AppTheme.cs`, `Pointframe/Models/*Watermark*`, `Pointframe/ViewModels/Settings*`, `Pointframe/Views/Settings*` | Settings model, settings window and its sections | [User settings](features/settings.md#user-settings), [Settings invariant](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files), [Add a user setting](#add-a-user-setting) |
| `Pointframe/Models/Capture*`, `Pointframe/ViewModels/Library*`, `Pointframe/Views/Library*` | Library window and capture items | [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe/Models/UpdateCheck*`, `Pointframe/Services/Messaging/Update*`, `Pointframe/ViewModels/UpdateDownload*`, `Pointframe/Views/UpdateDownload*`, `Pointframe/ViewModels/About*`, `Pointframe/Views/About*` | Update settings, download window, About window with the manual update check | [Update flow](features/updates.md#update-flow) |

**Other projects**

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe.Engine/**`, `Pointframe.Cli/**`, `Pointframe.Mcp/**`, `Pointframe.DesktopTestFixture/**` | WPF-free capture, recording, OCR, and desktop automation engine; the CLI and MCP hosts; the WinForms fixture the desktop tools drive | [Standalone CLI and MCP automation](features/cli-mcp.md#standalone-cli-and-mcp-automation) |
| `Pointframe.Mcp/Resources/**`, `Pointframe.Mcp/Tools/**` | Tool methods and the command list resource; a new tool also updates the contract test sets, `test-mcp-stdio.ps1`, and both READMEs | [MCP command list](features/cli-mcp.md#the-mcp-command-list-resource-must-name-every-registered-tool) |
| `Pointframe.Engine/Library/**`, `Pointframe.Data/**` | Capture catalog, import and index worker, EF Core context, repositories, migrations | [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe.Data/DependencyInjection.cs`, `Pointframe.Data/Services/**` | Data service registration and the startup migration | [App bootstrap](#app-bootstrap-di-and-messaging) |

**Tests**

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe.Tests/**` | xUnit and Moq unit tests; run in CI | Look up the class under test |
| `Pointframe.Tests/Mcp/**`, `Pointframe.Tests/Engine/**`, `Pointframe.Tests/Cli/**`, `Pointframe.Tests/DocsSync/**` | Engine, CLI, and MCP tests, and docs-sync tests that fail when docs drift from code | [Standalone CLI and MCP automation](features/cli-mcp.md#standalone-cli-and-mcp-automation), [MCP command list](features/cli-mcp.md#the-mcp-command-list-resource-must-name-every-registered-tool) |
| `Pointframe.Tests/Services/Handlers/**` | Annotation shape handler tests | [Annotation engine](features/annotation.md#annotation-engine) |
| `Pointframe.AutomationTests/**` | FlaUI smoke tests against the real app, and MCP desktop-tool tests against the WinForms fixture and Notepad; separate workflow | [Standalone CLI and MCP automation](features/cli-mcp.md#standalone-cli-and-mcp-automation), [CI, CD, and versioning](#ci-cd-and-versioning) |
| `Pointframe.AutomationTests/Installer/**` | Installer smoke tests | [Installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list), [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them) |
| `Pointframe.AutomationTests/Support/AutomationIds.cs`, `Pointframe.AutomationTests/Smoke/*AnnotationTool*` | Toolbar automation ids and per-tool smoke coverage | [Add an annotation tool](features/annotation.md#add-an-annotation-tool) |

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

## Decisions

A decision records why a choice was made over the obvious alternative, so nobody reverses it by accident. It lives with what it governs: a decision that only one area needs is in that area file, and one that spans areas is in [decisions.md](decisions.md). Numbers are global across files and never reused; a reversed decision stays, with "Superseded by D-NNN" on its first line. The list is generated.

<!-- decisions -->

- [D-001 MVVM plus DI is the composition model](decisions.md#d-001-mvvm-plus-di-is-the-composition-model)
- [D-002 One knowledge base file, checked by script](decisions.md#d-002-one-knowledge-base-file-checked-by-script) (superseded)
- [D-003 Recording uses one authoritative session geometry](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry)
- [D-004 Native libraries ship loose and the installer packages them](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them)
- [D-005 The speech model is delivered by both the installer and the app](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app)
- [D-006 Cross-cutting knowledge base plus one file per feature area](decisions.md#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area)

<!-- /decisions -->

## Invariants

### Settings are read at the point of use and persisted through three files

**Rule.**

1. Read `IUserSettingsService.Current.X` where the value is used. Do not copy it into a field or a constructor parameter.
2. Partial changes go through `Update(settings => settings.X = ...)`, which clones, mutates, and saves.
3. A new property exists in two places in one change: the `UserSettings` property with its default, and the read-back in `SettingsViewModel.Save()`. `Clone` needs no edit — it round-trips through the persistence serializer. The Settings window binding is a third, UI-only step.

**Why.** The settings service is a singleton and the user can change values while the app runs, so a cached field goes stale until restart. `Clone` serializes and deserializes through the same converters as save and load, so it covers new properties automatically and drops exactly what the on-disk format would drop. A property missing from `SettingsViewModel.Save()` is still reset to its default the next time the user presses Save.

**Enforced by.** `SettingsRoundTripTests` populates every property of `UserSettings` by reflection and round-trips it through save, load, `Update`, and `SettingsViewModel.Save`. A property missing from `Save()`, or one whose populated value equals its default, fails this test. Reading at the point of use is not enforced by tests; review for `Current` captured in fields.

**Symptoms when violated.** A setting reverts after restart or after saving an unrelated setting. A hotkey or theme change takes effect only after restart.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`. See [User settings](features/settings.md#user-settings) and [Add a user setting](#add-a-user-setting).

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

**Files.** `Pointframe/Native/MonitorDpiHelper.cs`, `Pointframe/Native/DpiAwarenessScope.cs`, `Pointframe/Views/OverlayWindow.xaml.cs`, `Pointframe/Services/Capture/SelectionSession.cs`, `Pointframe/Models/RecordingSessionGeometry.cs`, `Pointframe/app.manifest`. See [Capture overlay](features/capture.md#capture-overlay-and-selection), [Recording pipeline](features/recording.md#recording-pipeline), and [D-003](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry).

**Lessons.**

- Lesson: WPF PerMonitorV2: set window bounds before Show(), not in OnSourceInitialized
- Lesson: Mixed-DPI multi-monitor capture features need PerMonitorV2 process DPI awareness
- Lesson: Full-desktop selection overlays are safer in a system-aware DPI context while monitor-scoped recording hosts stay PerMonitorV2
- Lesson: Active-window capture must map Win32 screen coordinates into overlay space instead of dividing by one overlay DPI

### Everything emitted next to the exe must be in the installer file list

**Rule.** Whatever `dotnet publish` leaves in `Pointframe/bin/publish/win-x64/` beside `Pointframe.exe` is required at runtime and must appear in the `[Files]` section of `installer/Pointframe.iss`. Changing a publish property that alters that set changes the installer in the same commit.

**Why.** The build is self-contained and single-file, so it is tempting to read the installer as needing only the exe — the script said exactly that in a comment for months. It is only true while every native is bundled. `IncludeNativeLibrariesForSelfExtract` controls that for all natives at once; see [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them).

**Enforced by.** `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs` installs silently, asserts each required native exists under the install directory, launches the app, and uninstalls. It is opt-in: set `POINTFRAME_RUN_INSTALLER_SMOKE=1` and run elevated.

**Symptoms when violated.** Nothing fails on a developer machine, where the DLLs resolve from other locations, and CI stays green because it never installs. On a clean machine the installed app dies at startup: without `e_sqlite3.dll` the EF Core migration throws and the user is told the database migration failed, which points away from the real cause. Verify by inspecting the *installed* directory, never `bin\`.

**Files.** `installer/Pointframe.iss`, `Pointframe/Properties/PublishProfiles/win-x64.pubxml`, `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs`, `installer/build-installer.ps1`.

**Lessons.**

- Lesson: Turning off single-file native bundling silently breaks the installer, not the dev build

## How-tos

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

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe/Views/SettingsWindow.xaml`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`. See [Settings persistence](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) and [User settings](features/settings.md#user-settings).

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

**Files.** `Pointframe/AppServiceRegistration.cs`. See [App bootstrap](#app-bootstrap-di-and-messaging) and [D-001](decisions.md#d-001-mvvm-plus-di-is-the-composition-model).

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
| `.github/workflows/cd.yml` | `workflow_run` after a successful CI on `master` | compute the version with nbgv, inject the App Insights connection string and verify it, publish self-contained single-file, sign the exe when the certificate secret exists, build the CLI and MCP packages, create stable CLI/MCP asset aliases and their checksums, build the Inno Setup installer from `installer/Pointframe.iss`, sign it, upload `Pointframe-<version>-x64-Setup`, create the GitHub Release tagged `v<version>` |
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
- CD publishes both immutable versioned CLI/MCP assets and stable aliases (`Pointframe.Cli-win-x64.zip`, `Pointframe.Mcp-win-x64.mcpb`, and matching `.sha256` files). Use the aliases for `releases/latest/download` links and Shields.io badges; they prevent release-version changes from breaking those links.
- Renaming anything in the delivery path (exe name, installer name, package id) touches the workflows, the installer, the winget manifests, and the updater's asset-name expectation together.

**Files.** `.github/workflows/ci.yml`, `.github/workflows/cd.yml`, `.github/workflows/desktop-automation.yml`, `.github/workflows/winget-release.yml`, `version.json`, `dotnet-tools.json`, `installer/Pointframe.iss`. See [Update flow](features/updates.md#update-flow) and [Telemetry](features/telemetry.md#telemetry-pipeline).

**Lessons.**

- Lesson: Rename migrations must update hardcoded delivery paths in workflows and installer assets together
- Lesson: WinGet ARP publisher matching must follow the installer metadata exactly
- Lesson: Winget package renames need a one-time upstream bootstrap before automated updates can work
- Lesson: Renamed winget packages need a distinct installer identity if they are published as a new package ID
- Lesson: Coverage workflows should run for fix branches and not hard-require a Codecov token on public repos
- Lesson: CI publish of a self-contained exe needs `<RuntimeIdentifiers>` even with `--runtime win-x64` on the command line
