Start every session with `/kb-init` (Codex: `pwsh scripts/kb.ps1 init`; coordinators add `-Role coordinator`).

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

- **`kb init` prints the workflow rules for your role; read the sections it names** ([workflow.md](docs/knowledge-base/workflow.md)). They set roles, review, and merge rules for every agent.
- **Know the past bugs before changing UI flow or window-lifecycle code** (overlay, dialog, hotkey, capture, tray, recording, multi-monitor/DPI). `kb-read <path>` ranks the `lessons.md` entries for the files you will touch, and the project hook shows the top five the first time you open a file in an area; read the full entry by its heading. When you diagnose a reusable trap, add it to `lessons.md` and link it from the owning knowledge base section.
- **`/kb-init` prints `docs/knowledge-base/knowledge-base.md`; open `docs/knowledge-base/features/<area>.md` only for the area the task touches.** The core holds cross-cutting knowledge and a Feature index; each area file lists its features (`F-NN`: trigger, entry point, telemetry, tests) and how the area works. The `kb-read`, `kb-write`, and `kb-check` skills are yours to call when needed.
- **Before finishing any change under `Pointframe/` or `Pointframe.Data/`, state in your final message whether the knowledge base needs an add, an update, or nothing.** If it needs one, write it, or ask when unsure. A renamed or moved file always needs an update. Finish every session with `/kb-done`; CI runs the same checks.
- **A task is done only when `pwsh scripts/verify.ps1` passes** (`VERIFY PASSED`, exit 0) on the final working tree. Iterate on its failure details until it does; use `-Filter` and `-Skip` only while iterating, never for the final run. If it still fails after several honest attempts, or passing would need weakening a test, stop and report what fails and why.
- **Executors never commit or push.** The coordinating agent commits, opens PRs, and merges per the owner's charter; see [Agent workflow](docs/knowledge-base/workflow.md#pr-and-merge). Run `dotnet format` and tests before handing back.
- Run `dotnet format Pointframe/Pointframe.csproj` after every C# edit — CI fails on any style/whitespace violation.

## Architecture

The rules every change keeps (composition, MVVM, settings, undo, DPI, new annotation tools) are the [Architecture rules](docs/knowledge-base/knowledge-base.md#architecture-rules) in the knowledge base core, which `/kb-init` prints; the why, the flows, and the recipes are in the sections they link.

- **`pointframe verify` is not `scripts/verify.ps1`.** The product feature (F-39 in `docs/knowledge-base/features/verify.md`) lives in `Pointframe.Cli/Verification/` with tests in `Pointframe.Agent.Tests/Cli/`; `pwsh scripts/verify.ps1` is only this repo's done-gate. For manual trials use an installed or copied CLI, never `Pointframe.Cli/bin`.

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
- `docs/knowledge-base/knowledge-base.md` — knowledge base core, cross-cutting: architecture rules, feature and decision indexes; next to it `composition.md` (composition and shared invariants), `how-tos.md`, `maintenance.md`, `file-map.md`, `references.md`, `tooling.md`, `workflow.md`, and `decisions.md` (decisions that span areas); `docs/knowledge-base/features/*.md` — one file per feature area; start with `/kb-init`, finish with `/kb-done`
- `lessons.md` — reusable lessons from past bugs and workflow traps
- `plan/` — roadmap and feature plans (local-only, not in git)
