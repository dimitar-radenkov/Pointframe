# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Pointframe is a Windows-only WPF desktop app (.NET 10) for screen capture, annotation, and recording. It uses:
- **CommunityToolkit.Mvvm** (source generators: `[ObservableProperty]`, `[RelayCommand]`)
- **Microsoft.Extensions.DependencyInjection** for IoC
- **Serilog** for logging (to `%LOCALAPPDATA%\Pointframe\logs\`)
- **ffmpeg** (external binary) for MP4/GIF output
- **Nerdbank.GitVersioning** for automatic semantic versioning

## Commands

```powershell
# Build
dotnet build Pointframe/Pointframe.csproj

# Run
dotnet run --project Pointframe/Pointframe.csproj

# Test (unit only)
dotnet test Pointframe.Tests/Pointframe.Tests.csproj

# Single test class
dotnet test --filter "FullyQualifiedName~AnnotationViewModelTests"

# Format (required before committing — CI fails without it)
dotnet format Pointframe/Pointframe.csproj

# Verify format without changes
dotnet format Pointframe/Pointframe.csproj --verify-no-changes
```

## Workflow

- **Read `lessons.md` before changing UI flow or window-lifecycle code.** Treat this as a required first step for overlay, dialog, hotkey, capture, tray, recording, and multi-monitor/DPI changes — past bugs (e.g. PerMonitorV2 window sizing) are recorded there. When you diagnose a reusable trap, add a short note back to `lessons.md`.
- **Read `docs/knowledge-base/knowledge-base.md` before architecture or cross-subsystem work.** Use its table of contents and read the sections that match the task.
- **Before finishing any change under `Pointframe/` or `Pointframe.Data/`, state in your final message whether the knowledge base needs an add, an update, or nothing.** If it needs one, run `/knowledge-base add` or `/knowledge-base update`, or ask when unsure. A renamed or moved file always needs `/knowledge-base update`.
- **Never commit or push on the user's behalf.** Prepare changes, run `dotnet format` and tests, then stop and let the user review and commit.
- Run `dotnet format Pointframe/Pointframe.csproj` after every C# edit — CI fails on any style/whitespace violation.

## Architecture

### App Bootstrap

`App.xaml.cs` is the entry point: it builds the Generic Host, wires Serilog, applies EF Core migrations, and starts the tray icon and global hotkeys. Every service and window is registered in `AppServiceRegistration.cs` (`AddPointframeAppServices`); `OverlayWindow` is built by the `CreateOverlayWindow` factory in the same file.

**Lifetime rules:**
- **Singleton** — long-lived state: settings, hotkeys, telemetry, event aggregator, annotation geometry, OCR, update service
- **Transient** — per-operation: capture service, video writer, overlay/settings/about windows and their view models

### Core Flows

**Screenshot & annotation:** hotkey/tray → `OverlayWindow` opens full-screen → user selects region → annotation mode → `OverlayViewModel` coordinates copy/save/pin/record actions; `AnnotationViewModel` + `AnnotationCanvasRenderer` own drawing state.

**Recording:** user selects region from overlay → recording pipeline starts (`ScreenRecordingService` → `IVideoWriter`/`FFMpegVideoWriter`) → `RecordingOverlayWindow` provides live annotation surface → `RecordingHudViewModel` controls pause/resume/stop.

**Settings persistence:** never cache settings in fields — always read from `IUserSettingsService.Current` at the point of use. Adding a new setting requires updating two places together or the value is silently reset on save: `Pointframe/Models/UserSettings.cs` (the property + default) and `SettingsViewModel.Save()`. `UserSettingsService.Clone(...)` needs no edit — it round-trips through the serializer.

### MVVM Conventions

- ViewModels inherit `ObservableObject`. Use `[ObservableProperty]` on `private _camelCase` backing fields; use `[RelayCommand]` on private methods. Never call `OnPropertyChanged()` manually.
- Every public service must have an `I<ServiceName>` interface and be registered in DI.

### Undo/Redo Invariant

The undo stack grows only in `AnnotationViewModel.CommitGroup()`, at the end of a drag. Shape handlers call `trackElement` only from `Commit`, never for draft/in-progress elements — tracking a draft corrupts the undo stack.

### DPI & Coordinates

WPF uses Device-Independent Pixels (DIPs); screen/GDI operations use physical pixels. Conversion: `physical_px = dip * dpiScale`. DPI scale is read from `PresentationSource` in `OverlayWindow.OnSourceInitialized`. Recording geometry requires even width/height (JPEG MCU constraint). Multi-monitor/mixed-DPI changes should be validated carefully — the canonical source is `RecordingSessionGeometry.cs`.

### Adding a New Annotation Tool

1. Add enum value to `Pointframe/Models/AnnotationTool.cs`
2. Add sealed record to `Pointframe/Models/ShapeParameters.cs`
3. Handle case in `AnnotationViewModel.TryGetShapeParameters()`
4. Add `Pointframe/Services/Annotation/Handlers/<Name>ShapeHandler.cs` implementing `IAnnotationShapeHandler` (`Begin`/`Update` draft, `Commit` tracks final elements, `Cancel` removes the draft) and register it in the `_handlers` dictionary in `AnnotationCanvasRenderer`
5. Add geometry helpers to `IAnnotationGeometryService`
6. Add toolbar button in `Pointframe/Views/OverlayWindow.xaml` with an `AutomationId`
7. Add unit tests (handler, `TryGetShapeParameters`, geometry)
8. Keep overlay smoke coverage in sync: register the tool in `Pointframe.AutomationTests/Support/AutomationIds.cs` and `Pointframe.AutomationTests/Smoke/AnnotationToolSmokeTests.cs`

Full recipe with rationale: the "Add an annotation tool" section of `docs/knowledge-base/knowledge-base.md`.

## Code Style

- Allman braces, always use braces (no single-line `if` bodies)
- File-scoped namespaces
- Nullable reference types enabled — be explicit
- No XML doc comments (intentional project policy)
- Private fields: `_camelCase`; public properties/classes/interfaces: `PascalCase`

## Testing

- xUnit for tests; **Moq** for mocking dependencies — `new Mock<IFoo>()`, stub with `Setup(...)`, assert interactions with `Verify(..., Times.Once)`. Every service has an `I<ServiceName>` interface precisely so it can be mocked
- Unit tests only in main CI (`Category!=Integration`); automation tests in `Pointframe.AutomationTests/` run separately
- Tests operate against ViewModels/services directly — the WPF app is never started

## Telemetry

Disabled by default in source builds (`ApplicationInsights:ConnectionString` is empty in `appsettings.json`). The connection string is injected only in the official CD pipeline.

## Versioning

Base version is in `version.json` (major.minor); patch auto-increments with commit height via `Nerdbank.GitVersioning`. Git tags matching `v*` produce release builds (no pre-release suffix).

## Docs

- `docs/developer-guide.md` — setup, conventions, patterns
- `docs/knowledge-base/knowledge-base.md` — knowledge base: subsystems, decisions, invariants, how-tos, references; maintain with `/knowledge-base`
- `lessons.md` — reusable lessons from past bugs and workflow traps
- `plan/` — roadmap and feature plans (local-only, not in git)
