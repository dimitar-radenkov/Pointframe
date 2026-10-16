# Composition and invariants

How the app is composed (startup, DI lifetimes, messaging) and the invariants every area keeps.

Part of the [Pointframe knowledge base](knowledge-base.md).

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

- Every public service has an `I<Name>` interface and is registered here. See [Register a service](how-tos.md#register-a-service).
- The keyboard hook is unhooked in `OnExit`; an orphaned hook routes every keystroke on the machine through a dead process.
- Nothing reads settings into a field at startup; read `IUserSettingsService.Current` at the point of use. See [Settings persistence](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files).
- Windows launched from a tray callback are deferred until the tray menu unwinds and get a real owner window, or they lose focus and vanish.

**Tests.** `Pointframe.Tests/AppTests.cs` builds the container and resolves the core services and factories, so a missing registration fails there first. Also `Pointframe.Tests/Services/TrayIconManagerTests.cs`, `Pointframe.Tests/Services/GlobalHotkeyServiceTests.cs`, and `Pointframe.AutomationTests/Smoke/LaunchModeSmokeTests.cs` for launch modes.

**Files.** `Pointframe/App.xaml.cs`, `Pointframe/AppServiceRegistration.cs`, `Pointframe/Services/Messaging/IEventAggregator.cs`, `Pointframe/Services/Messaging/DefaultEventAggregator.cs`, `Pointframe/Services/Infrastructure/GlobalHotkeyService.cs`, `Pointframe/Services/Infrastructure/TrayIconManager.cs`, `Pointframe/Services/Capture/CaptureLaunchService.cs`, `Pointframe/Automation/AutomationLaunchOptions.cs`, `Pointframe.Data/DependencyInjection.cs`.

**Lessons.**

- Lesson: Automation-mode window replacement should not rely on OnLastWindowClose
- Lesson: Tray-launched file dialog can lose focus in a tray-only WPF app

## Invariants

### Settings are read at the point of use and persisted through three files

**Rule.**

1. Read `IUserSettingsService.Current.X` where the value is used. Do not copy it into a field or a constructor parameter.
2. Partial changes go through `Update(settings => settings.X = ...)`, which clones, mutates, and saves.
3. A new property exists in two places in one change: the `UserSettings` property with its default, and the read-back in `SettingsViewModel.Save()`. `Clone` needs no edit — it round-trips through the persistence serializer. The Settings window binding is a third, UI-only step.

**Why.** The settings service is a singleton and the user can change values while the app runs, so a cached field goes stale until restart. `Clone` serializes and deserializes through the same converters as save and load, so it covers new properties automatically and drops exactly what the on-disk format would drop. A property missing from `SettingsViewModel.Save()` is still reset to its default the next time the user presses Save.

**Enforced by.** `SettingsRoundTripTests` populates every property of `UserSettings` by reflection and round-trips it through save, load, `Update`, and `SettingsViewModel.Save`. A property missing from `Save()`, or one whose populated value equals its default, fails this test. Reading at the point of use is not enforced by tests; review for `Current` captured in fields.

**Symptoms when violated.** A setting reverts after restart or after saving an unrelated setting. A hotkey or theme change takes effect only after restart.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`. See [User settings](features/settings.md#user-settings) and [Add a user setting](how-tos.md#add-a-user-setting).

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
