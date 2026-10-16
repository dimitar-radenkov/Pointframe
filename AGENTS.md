Start every session with `/kb-init` (Codex: `pwsh scripts/kb.ps1 init`; coordinators add `-Role coordinator`).

# AGENTS.md

## Project

Pointframe is a Windows-only WPF screen-capture, annotation, and recording application on .NET 10. It uses CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Serilog, ffmpeg, EF Core, and Nerdbank.GitVersioning.

## Commands

```powershell
dotnet build Pointframe/Pointframe.csproj
dotnet run --project Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~<TestClass>"
dotnet format Pointframe/Pointframe.csproj
dotnet format Pointframe/Pointframe.csproj --verify-no-changes
pwsh scripts/verify.ps1
```

## Required reading and workflow

- `pwsh scripts/kb.ps1 init` prints the workflow rules for your role (`docs/knowledge-base/workflow.md`); read the sections it names.
- `init` also prints `docs/knowledge-base/knowledge-base.md`, the architecture and cross-subsystem rules; add a file, feature, or topic (`pwsh scripts/kb.ps1 init <target>`) for its sections and lessons.
- Before changing UI flow or window-lifecycle code—overlay, dialogs, hotkeys, capture, tray, recording, DPI, or multi-monitor behavior—read `lessons.md` first.
- After changes under `Pointframe/` or `Pointframe.Data/`, state in the final response whether the knowledge base needs an add, update, or nothing. Update it when durable architecture, behavior, invariants, or paths changed.
- Executors never commit or push. The coordinating agent commits, opens PRs, and merges per the owner's charter; see `docs/knowledge-base/workflow.md`.

## Architecture and conventions

- `App.xaml.cs` owns startup and shutdown. Register services and windows in `AppServiceRegistration.cs` through `AddPointframeAppServices`; construct `OverlayWindow` through its factory.
- Use MVVM: ViewModels inherit `ObservableObject`; use `[ObservableProperty]` and `[RelayCommand]`; do not manually raise `PropertyChanged`.
- Every public service has an `I<Name>` interface, is constructor-injected, and is registered through DI. Use singletons for long-lived state and OS handles; transients for per-operation objects, ViewModels, and windows.
- Keep application state and commands in ViewModels. Keep OS, I/O, network, and process access behind services; code-behind is for view-specific layout, HWND interop, DPI, and focus.
- Read `IUserSettingsService.Current` at the point of use instead of caching settings. A new setting requires the model/default, `SettingsViewModel.Save()`, and its UI binding.
- Preserve the committed-only annotation undo invariant: draft shapes must not be tracked before `AnnotationViewModel.CommitGroup()`.
- WPF uses DIPs while screen/GDI APIs use physical pixels; follow the existing per-monitor DPI and recording-geometry implementations.
- Use Allman braces, braces for control bodies, file-scoped namespaces, nullable reference types, `_camelCase` private fields, and `PascalCase` public symbols. Do not add XML documentation comments.

## Testing and validation

- Add focused xUnit tests for changed behavior. Test ViewModels and services directly with Moq; unit tests do not start WPF.
- Run `dotnet format Pointframe/Pointframe.csproj` after every C# edit and `--verify-no-changes` before handoff.
- Run the narrowest applicable tests, then broaden validation when shared contracts, DI registration, or user-visible workflows change.
- A task is done only when `pwsh scripts/verify.ps1` passes (`VERIFY PASSED`, exit 0) on the final working tree. It runs build, format, unit tests, the knowledge base check, and the workflow script check, and writes `artifacts/verify/verdict.json` with failure details. Iterate until it passes; `-Filter` and `-Skip` are for iterating only. If it still fails after several honest attempts, or passing would need weakening a test, stop and report.

## Documentation

- `docs/knowledge-base/knowledge-base.md` contains the architecture rules and the feature and decision indexes; `composition.md` (composition and invariants), `how-tos.md`, `maintenance.md`, `file-map.md`, `references.md`, `tooling.md`, and `workflow.md` sit next to it.
- `lessons.md` contains concise bug post-mortems and reusable implementation traps.
- `plan/` contains local-only plans and roadmap material.
- The product's `pointframe verify` feature is F-39 in `docs/knowledge-base/features/verify.md`; its code is in `Pointframe.Cli/Verification/` and its tests are in `Pointframe.Tests/Cli/`. `pwsh scripts/verify.ps1` is this repository's done-gate and is a different thing. For manual trials of `pointframe verify`, use an installed or copied CLI, never `Pointframe.Cli/bin`.
