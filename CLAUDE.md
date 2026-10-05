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

# Required interactive desktop regression selection (Windows signed-in session)
pwsh scripts/desktop-tests.ps1

# Repeat the required selection and run the real-app persistence verifier
pwsh scripts/desktop-tests.ps1 -Repeat 10 -IncludeVerifyApp

# Format (required before committing — CI fails without it)
dotnet format Pointframe/Pointframe.csproj

# Verify format without changes
dotnet format Pointframe/Pointframe.csproj --verify-no-changes

# Verify everything CI's unit job checks (build, format, tests, kb, workflows); writes artifacts/verify/verdict.json
pwsh scripts/verify.ps1
```

## Workflow

- **Know the past bugs before changing UI flow or window-lifecycle code** (overlay, dialog, hotkey, capture, tray, recording, multi-monitor/DPI). `/kb-read <path>` ranks the `lessons.md` entries for the files you will touch, and the project hook shows the top five the first time you open a file in an area; read the full entry by its heading. When you diagnose a reusable trap, add it to `lessons.md` and link it from the owning knowledge base section.
- **Read `docs/knowledge-base/knowledge-base.md` before architecture or cross-subsystem work; open `docs/knowledge-base/features/<area>.md` only for the area the task touches.** The main file holds cross-cutting knowledge and a Feature index; each area file lists its features (`F-NN`: trigger, entry point, telemetry, tests) and how the area works. `/kb-read <file, area, F-NN, or topic>` lists what to read.
- **Before finishing any change under `Pointframe/` or `Pointframe.Data/`, state in your final message whether the knowledge base needs an add, an update, or nothing.** If it needs one, run `/kb-write`, or ask when unsure. A renamed or moved file always needs `/kb-write`. Finish with `/kb-check`; CI runs it too.
- **A task is done only when `pwsh scripts/verify.ps1` passes** (`VERIFY PASSED`, exit 0) on the final working tree. Iterate on its failure details until it does; use `-Filter` and `-Skip` only while iterating, never for the final run. If it still fails after several honest attempts, or passing would need weakening a test, stop and report what fails and why.
- **Never commit or push on the user's behalf.** Prepare changes, run `dotnet format` and tests, then stop and let the user review and commit.
- Run `dotnet format Pointframe/Pointframe.csproj` after every C# edit — CI fails on any style/whitespace violation.

## Architecture

Rules to keep in every change. The why, the flows, and the recipes are in the knowledge base.

- **Composition.** `App.xaml.cs` builds the Generic Host; every service and window is registered in `AppServiceRegistration.cs`. Singleton for long-lived state, transient per operation. See [App bootstrap](docs/knowledge-base/knowledge-base.md#app-bootstrap-di-and-messaging).
- **MVVM.** ViewModels inherit `ObservableObject`; `[ObservableProperty]` on `private _camelCase` fields, `[RelayCommand]` on private methods, never `OnPropertyChanged()` by hand. Every public service has an `I<ServiceName>` interface and a DI registration.
- **Settings.** Read `IUserSettingsService.Current` at the point of use; never cache it in a field. A new setting changes `UserSettings.cs` and `SettingsViewModel.Save()` together. See [Add a user setting](docs/knowledge-base/knowledge-base.md#add-a-user-setting).
- **Undo.** The undo stack grows only in `AnnotationViewModel.CommitGroup()`; shape handlers track elements only in `Commit`, never drafts. See [Undo groups](docs/knowledge-base/features/annotation.md#undo-groups-are-added-only-on-commit).
- **DPI.** WPF works in DIPs, screen and GDI in physical pixels, per monitor; `RecordingSessionGeometry.cs` is canonical, and recording width and height are even. See [DIPs and physical pixels](docs/knowledge-base/knowledge-base.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor).
- **`pointframe verify` is not `scripts/verify.ps1`.** The product feature (F-39 in `docs/knowledge-base/features/cli-mcp.md`) lives in `Pointframe.Cli/Verification/` with tests in `Pointframe.Tests/Cli/`; `pwsh scripts/verify.ps1` is only this repo's done-gate. For manual trials use an installed or copied CLI, never `Pointframe.Cli/bin`.
- **New annotation tool.** Follow [Add an annotation tool](docs/knowledge-base/features/annotation.md#add-an-annotation-tool); it includes the automation ids and smoke tests.

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
- `docs/knowledge-base/knowledge-base.md` — knowledge base, cross-cutting: composition, shared invariants and how-tos, references, File map, feature and decision indexes; `docs/knowledge-base/decisions.md` — decisions that span areas; `docs/knowledge-base/features/*.md` — one file per feature area; use `/kb-read`, `/kb-write`, `/kb-check`
- `lessons.md` — reusable lessons from past bugs and workflow traps
- `plan/` — roadmap and feature plans (local-only, not in git)
