# Developer Guide — Pointframe

Pointframe is the current product name. The solution, project files, namespaces, and top-level source folders use `Pointframe`. A few compatibility identifiers (an environment variable, an `AppContext` key) still use the pre-rename name on purpose.

## 1. Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 10.0 or later |
| Windows | 10 (build 1903+) or 11 |
| Visual Studio | 2022 17.12+ **or** VS Code with C# Dev Kit |
| ffmpeg.exe | Required at runtime for MP4 recording, GIF export, and trim. Resolved from next to the app binary, then `Assets\ffmpeg\`, then `PATH` |

Install the SDK from https://dotnet.microsoft.com/download/dotnet/10.0.

---

## 2. Getting the Source

```powershell
git clone https://github.com/dimitar-radenkov/Pointframe.git
cd Pointframe
dotnet tool restore
```

`nbgv` (Nerdbank.GitVersioning) derives the version from `version.json` and the Git history. A full clone is required for correct versioning; the CI pipeline sets `fetch-depth: 0` for this reason. `dotnet tool restore` installs `nbgv` and `dotnet-ef` from `dotnet-tools.json`.

---

## 3. Build & Run

```powershell
# Build the main project
dotnet build Pointframe/Pointframe.csproj

# Run the application
dotnet run --project Pointframe/Pointframe.csproj
```

The app launches minimised to the system tray. Press `Print Screen` to open the capture overlay.

---

## 4. Running Tests

```powershell
# Run all tests
dotnet test Pointframe.Tests/Pointframe.Tests.csproj

# Run a specific test class
dotnet test --filter "FullyQualifiedName~AnnotationViewModelTests"

# Run with verbose output
dotnet test Pointframe.Tests/Pointframe.Tests.csproj -v normal
```

The test project (`Pointframe.Tests`) does not start the application. Tests target ViewModels and services (plus a few window-layout tests); xUnit is the only test framework, with Moq for mocking. UI automation lives in `Pointframe.AutomationTests` and runs separately (see §14).

---

## 5. Code Formatting

CI will fail if formatting is not clean. Always run this before committing:

```powershell
# Fix all formatting issues
dotnet format Pointframe/Pointframe.csproj

# Verify formatting is clean (same check CI runs)
dotnet format Pointframe/Pointframe.csproj --verify-no-changes
```

Common causes of formatting failures:
- Trailing whitespace on blank lines
- Explicit type where `var` is preferred
- Missing or extra blank lines between members
- `if` bodies without braces (always required — see Coding Conventions)

The format gate covers the main project only; do not run `dotnet format` on `Pointframe.Tests`.

---

## 6. Project Structure

```
Pointframe/                        Main WPF application (tray-first, no main window)
  App.xaml.cs                      Generic Host, Serilog, EF migrations, tray icon, hotkeys
  AppServiceRegistration.cs        Every DI registration (AddPointframeAppServices)
  appsettings.json                 Log retention, App Insights connection string (empty in source)
  app.manifest                     PerMonitorV2 DPI awareness
  Views/                           Windows; OverlayWindow is split into partials
                                   (Selection, Layout, Recording, RecordingAnnotation, RecordingHud, ColorPicker)
  ViewModels/                      Overlay, Annotation, RecordingHud, Settings, Library, Trim, Beautifier, ...
  Services/
    Annotation/                    Canvas renderer, interaction controller, geometry, Handlers/ (one per tool)
    Capture/                       Selection session, screen/window capture, clipboard, image files, library
    Infrastructure/                Settings, hotkeys, tray, telemetry, OCR, dialogs, paths, testable wrappers
    Messaging/                     IEventAggregator and message records
    Recording/                     Recording service, ffmpeg writer, coordinators, GIF, trim, watermark
    Update/                        GitHub release check, auto-update, download window service
  Models/                          UserSettings, ShapeParameters, RecordingSessionGeometry, enums
  Native/                          Win32 interop and DPI helpers
  Automation/                      Launch options for the UI automation suite

Pointframe.Data/                   EF Core + SQLite (capture OCR text cache) and migrations
Pointframe.Tests/                  xUnit unit tests (Services/, ViewModels/, Models/, window tests)
Pointframe.AutomationTests/        UI automation smoke tests (Smoke/, Support/AutomationIds.cs)
Pointframe.Benchmarks/             BenchmarkDotNet projects
installer/                         Inno Setup script plus build and test scripts
winget/, packaging/scoop/          Package manifests
website/                           GitHub Pages site
.github/workflows/                 CI, CD, desktop automation, winget, CodeQL, pages
```

Every service has an `I<ServiceName>` interface next to it. The knowledge base (§6.1) describes each subsystem in depth.

## 6.1 Project Knowledge Base

The knowledge base is one file, [knowledge-base.md](../docs/knowledge-base/knowledge-base.md), with five groups: subsystems, decisions, invariants, how-tos, references. Use its table of contents and read the sections that match your task. Its "How to maintain this file" section holds the templates and conventions.

Maintain it with the `/knowledge-base` skill (`add`, `update`) or directly, then run `pwsh .claude/skills/knowledge-base/knowledge-base.ps1`, which refreshes the table of contents and checks that every path, lesson reference, and internal link still resolves. Bug post-mortems stay in `lessons.md`; sections reference them by heading.

---

## 7. Coding Conventions

### 7.1 Naming

| Item | Convention | Example |
|---|---|---|
| Private fields | `_camelCase` | `_strokeThickness` |
| Public properties | `PascalCase` | `StrokeThickness` |
| Classes / Records | `PascalCase` | `AnnotationViewModel` |
| Interfaces | `IPascalCase` | `IAnnotationGeometryService` |
| Enum values | `PascalCase` | `AnnotationTool.Arrow` |

### 7.2 Braces

Always use braces — never write the condition and body on one line:

```csharp
// ✅ Correct
if (condition)
{
    DoSomething();
}

// ❌ Wrong
if (condition) DoSomething();
```

### 7.3 Nullable Reference Types

Enabled project-wide. All reference parameters and fields are non-nullable unless genuinely optional. Use `?` for optional references and guard at system boundaries.

### 7.4 MVVM with CommunityToolkit.Mvvm

- Use `[ObservableProperty]` on `private _camelCase` backing fields.
- Use `[RelayCommand]` on `private` methods to generate `IRelayCommand` properties.
- Never call `OnPropertyChanged()` manually when a source-generator attribute can do it.
- ViewModels inherit `ObservableObject`.

```csharp
[ObservableProperty] private Color _activeColor;

[RelayCommand]
private void Copy() { ... }
```

### 7.5 No XML Doc Comments

Do not add `/// <summary>` comments to any code. The codebase intentionally avoids them.

### 7.6 Services and Interfaces

Every public service must have a corresponding `I<ServiceName>` interface. This enables injection and unit-testability — tests mock these interfaces with Moq. Implementations are registered in `AppServiceRegistration.cs → AddPointframeAppServices()`.

---

## 8. Adding a New Annotation Tool

Follow these steps in order:

**Step 1** — Add a value to the `AnnotationTool` enum in [Models/AnnotationTool.cs](../Pointframe/Models/AnnotationTool.cs):

```csharp
public enum AnnotationTool { Arrow, Rectangle, ..., MyNewTool }
```

**Step 2** — Add a matching sealed record in [Models/ShapeParameters.cs](../Pointframe/Models/ShapeParameters.cs):

```csharp
public sealed record MyNewToolShapeParameters(
    Point P1,
    Color Color,
    double Thickness) : ShapeParameters;
```

**Step 3** — Handle the new case in `AnnotationViewModel.TryGetShapeParameters()` so the ViewModel can produce a snapshot of the committed shape.

**Step 4** — Create `Services/Annotation/Handlers/MyNewToolShapeHandler.cs` implementing `IAnnotationShapeHandler`:
- `Begin(point, brush, thickness, canvas)` creates the draft element.
- `Update(point)` mutates the draft only.
- `Commit(canvas, trackElement)` adds the final elements and calls `trackElement` for each.
- `Cancel(canvas)` removes the draft.

Copy `RectShapeHandler` for a simple drag shape or `TextShapeHandler` for an editable one.

> **Critical:** Call `trackElement` only from `Commit`, and only for elements that should appear in the undo/redo stack. Tracking draft or preview elements corrupts undo groups.

**Step 5** — Register the handler in the `_handlers` dictionary in `AnnotationCanvasRenderer`, passing `GetShapeParameters` and any ViewModel callbacks it needs.

**Step 6** — Put pure math in `IAnnotationGeometryService` and `AnnotationGeometryService`, not in the handler, so it is unit-testable without WPF.

**Step 7** — Add a toolbar button in `Views/OverlayWindow.xaml` bound to `SelectedTool`, with an `AutomationProperties.AutomationId`.

**Step 8** — Tests: a handler test under `Pointframe.Tests/Services/Handlers/`, a `TryGetShapeParameters` case in `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs`, geometry cases in `Pointframe.Tests/Services/AnnotationGeometryServiceTests.cs`.

**Step 9** — Smoke coverage: add the id to `Pointframe.AutomationTests/Support/AutomationIds.cs` and the tool to `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs`.

The knowledge base section "Add an annotation tool" carries the same recipe with rationale.

---

## 9. Writing Tests

### 9.1 Framework

xUnit, with **Moq** for mocking dependencies. Mock any injected interface with `new Mock<IFoo>()`, stub calls with `Setup(...)`, and assert interactions with `Verify(..., Times.Once)`.

### 9.2 TestAnnotationViewModel

A `sealed partial` subclass in the test project provides access to protected state without changing the production class visibility.

### 9.3 PropertyChanged Testing Pattern

```csharp
var changed = new List<string>();
vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

vm.SelectedTool = AnnotationTool.Arrow;

Assert.Contains(nameof(vm.SelectedTool), changed);
```

### 9.4 File Layout

Mirror the main project structure:

```
Pointframe.Tests/
  ViewModels/
    AnnotationViewModelTests.cs
    OverlayViewModelTests.cs
    ...
  Services/
    GitHubUpdateServiceTests.cs
    Handlers/
    ...
  Models/
```

---

## 10. Adding a New Setting

1. Add a property to `UserSettings` in [Models/UserSettings.cs](../Pointframe/Models/UserSettings.cs) with a sensible default.
2. Copy the property in `UserSettingsService.Clone(...)`. `Update(...)` goes through this clone; a property missing here is silently dropped.
3. Add a corresponding `[ObservableProperty]` in `SettingsViewModel`, initialised from `Current` in the constructor, and write it back in `Save()`.
4. Add the UI control to `Views/SettingsWindow.xaml` in the right section, bound to the new VM property, with an `AutomationProperties.AutomationId`.
5. Consume the value by reading `IUserSettingsService.Current.NewProperty` at the point of use — never cache it at construction.

`Pointframe.Tests/Services/SettingsRoundTripTests.cs` round-trips every property by reflection and fails if step 2 or step 3 was skipped.

---

## 11. Registering a New Service

1. Create the interface `IMyService` and the implementation `MyService` in the matching folder under `Services/`.
2. Register in `AppServiceRegistration.cs → AddPointframeAppServices()`:
   ```csharp
   services.AddSingleton<IMyService, MyService>();   // or AddTransient
   ```
3. Inject via the constructor of any consumer. `OverlayWindow` is built by `CreateOverlayWindow` in the same file; add new parameters there.

**Lifetime guidance:**
- Use **Singleton** when the service holds long-lived state or OS handles (settings, hotkeys, tray, telemetry, geometry helpers).
- Use **Transient** when the service is stateful per operation (screen capture, video writer, recording service, ViewModels, windows).
- **Scoped** is reserved for EF Core in `Pointframe.Data`; resolve those inside `CreateScope()`.
- Need runtime arguments? Register a `Func<TArg, TService>` factory, as done for `TrimViewModel` and `RecordingHudViewModel`.

`Pointframe.Tests/AppTests.cs` builds the container, so a missing registration fails there before it fails at runtime.

---

## 12. DPI & Coordinate Systems

WPF operates in Device-Independent Pixels (DIPs). Screen/GDI operations use physical pixels.

Conversion:
```
physical_px = dip * dpiScale       // e.g., dpiScale = 1.5 at 144 DPI
dip         = physical_px / dpiScale
```

The process is PerMonitorV2 (`app.manifest`), so every monitor has its own scale. `DpiX` and `DpiY` are read in `OverlayWindow.OnSourceInitialized` from `PresentationSource`; `MonitorDpiHelper` gives the scale for a screen location. Assign a window's `Left/Top/Width/Height` before `Show()` so WPF creates it on the intended monitor. Recording visuals never convert on their own; they use `RecordingSessionGeometry`. Any code touching both coordinate systems must apply the conversion or visuals will be misaligned on mixed-DPI setups.

---

## 13. Known Pitfalls

| Pitfall | What goes wrong | Fix |
|---|---|---|
| Tracking draft elements | Pollutes undo stack; redo restores half-drawn shapes | Handlers call `trackElement` only from `Commit`; `AnnotationViewModel.CommitGroup()` is the only place the undo stack grows |
| Ending a drag without `Commit` or `Cancel` | Stale draft references throw on the next drag | Exactly one of the two runs per drag |
| Caching a setting value in a field | Value goes stale after the user changes settings | Read from `IUserSettingsService.Current` at point of use |
| Adding a setting without `Clone` and `Save()` | Value is silently dropped on the next save | Update `UserSettings`, `UserSettingsService.Clone`, and `SettingsViewModel.Save()` together |
| Not calling `ResetNumberCounter()` on undo | Numbered callouts are renumbered incorrectly | Sync counter after every undo/redo operation |
| Orphaning the keyboard hook | All keystrokes on the machine route through the dead process | Always call `UnhookWindowsHookEx` in `App.OnExit` |
| Skipping `LostFocus` on TextBox | Text annotations remain live `TextBox`es in the committed screenshot | `LostFocus` converts `TextBox` → `TextBlock` — do not remove the handler |
| Setting window bounds after `Show()` | Wrong size on a secondary monitor with a different scale | Assign `Left/Top/Width/Height` before `Show()` |
| Not running `dotnet format` | CI fails on whitespace/style violations | Run `dotnet format Pointframe/Pointframe.csproj` before every commit |
| Odd recording region dimensions | JPEG MCU codec crashes or produces corrupt output | `ScreenRecordingService.Start` truncates width/height to even numbers; use the truncated size everywhere |

---

## 14. CI / CD Pipeline

### CI (`ci.yml`)
- Triggered on pushes to `master`, `feature/**`, and `fix/**`, and on pull requests to `master`.
- Steps: checkout (full depth) → `dotnet tool restore` → restore → build `Pointframe.Tests` (Release) → `dotnet format Pointframe/Pointframe.csproj --verify-no-changes` → `dotnet test` with `--filter "Category!=Integration"` and Cobertura coverage → upload to Codecov.

### CD (`cd.yml`)
- Triggered by a successful CI run on `master` (`workflow_run`).
- Steps: compute the version with `nbgv` → inject the Application Insights connection string and verify it → publish self-contained single-file → sign the exe when a signing certificate secret exists → build the installer → sign it → upload `Pointframe-<version>-x64-Setup` → create a GitHub Release tagged `v<version>`.

### Other workflows
- `desktop-automation.yml` — runs `Pointframe.AutomationTests` on a Windows runner; manual trigger.
- `winget-release.yml` — submits the winget manifest after CD completes on `master`, or manually with a version.
- `codeql.yml`, `release-drafter.yml`, `dependabot-auto-merge.yml`, and `pages.yml` (deploys `website/`).

### Installer (`installer/Pointframe.iss`)
- Inno Setup script. Includes `CloseApplications` (prompts to close a running instance before upgrading) and an optional component that downloads ffmpeg next to the app.
- `installer\build-installer.ps1` builds it locally (requires Inno Setup 6); `installer\test-installer.ps1` checks the output.
- Its ARP `AppPublisher` is the source of truth that the winget manifests must match.
