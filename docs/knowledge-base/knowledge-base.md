# Pointframe Knowledge Base

Durable, agent-readable project knowledge. Start every session with `/kb-init` (Codex: `pwsh scripts/kb.ps1 init`; coordinators add `-Role coordinator`): it prints this file, the workflow rules for your role, environment facts, and, with a target, the sections and lessons for that file, feature, or topic.
This file holds the rules every area must keep, plus generated indexes of features and decisions.
Next to it: [file-map.md](file-map.md), [composition.md](composition.md), [how-tos.md](how-tos.md), [maintenance.md](maintenance.md), [references.md](references.md), [tooling.md](tooling.md), [workflow.md](workflow.md), [decisions.md](decisions.md), and one file per feature area under `docs/knowledge-base/features/` (open one only when the task touches that area). The table below says what goes where.
Start with the [Feature index](#feature-index) or run `/kb-read <file, area, F-NN, or topic>` (`pwsh scripts/kb.ps1 read ...`).

Last full review against the code: 2026-09-05.

## How to maintain this file

**What belongs where.**

| Knowledge | Goes to |
|---|---|
| Architecture rules that hold for every area, and the generated Feature and Decision indexes | This file |
| Composition, DI, messaging, and invariants that hold for every area | `docs/knowledge-base/composition.md` |
| Recipes any area uses | `docs/knowledge-base/how-tos.md` |
| How this knowledge base is structured, checked, budgeted, measured, and maintained | `docs/knowledge-base/maintenance.md` |
| Where files live on disk, external binaries, configuration keys | `docs/knowledge-base/references.md` |
| Workflows, local gates, merge and release scripts, versioning, installer and packaging rules | `docs/knowledge-base/tooling.md` |
| Which sections to read before editing a path | `docs/knowledge-base/file-map.md` |
| A decision that spans areas | `docs/knowledge-base/decisions.md` |
| One area's features, flows, entry points, area-only invariants, recipes, and decisions, its lessons | `docs/knowledge-base/features/<area>.md` |
| Bug post-mortems (Problem, Root cause, What fixed it, Takeaway) | `lessons.md`, then a `- Lesson:` line in the owning section |
| Roadmap, plans, task status, executor cost records | `plan/` |
| Contributor setup | `docs/developer-guide.md` |
| Per-session instructions | `CLAUDE.md`, one line per topic pointing here |
| How agents plan, assign, review, and merge work | `docs/knowledge-base/workflow.md` |
| Test counts, PR numbers, "verified on my machine" | The PR description |


Structure, conventions, budgets, the task suite, workflow rule IDs, and the maintenance commands are in [maintenance.md](maintenance.md).

## Contents

<!-- toc -->

- [Feature index](#feature-index)
- [Architecture rules](#architecture-rules)
- [Decisions](#decisions)

<!-- /toc -->

## Feature index

Generated from the area files under `docs/knowledge-base/features/`. Each feature's row there says how it is triggered, its entry point, telemetry events, tests, and what to read.

<!-- features -->

- [Capture](features/capture.md): F-01 Region snip; F-02 Whole-screen snip; F-03 Clean-window snip; F-04 Capture delay countdown; F-05 Open an image in the overlay; F-06 Copy, save, save as; F-07 Pin a screenshot; F-08 Copy text with an OCR lasso; F-09 Beautify a screenshot; F-38 Scrolling snip; F-10 Watermarks on screenshots and videos; F-41 Upload a capture and copy its link
- [Annotation](features/annotation.md): F-11 Annotation tools: arrow, line, rectangle, circle, pen, highlight, text, number, blur, callout, pixel ruler; F-12 Undo and redo; F-13 Color picker; F-14 Annotation style presets
- [Recording](features/recording.md): F-15 Region recording; F-16 Whole-screen recording; F-17 Recording HUD: pause, stop, minimize, expand; F-18 Microphone audio; F-19 Live annotation while recording; F-20 Blur redaction burned into the video; F-21 Cursor effects; F-22 Trim a recording; F-23 Export a recording as GIF
- [Transcription](features/transcription.md): F-24 Transcripts and subtitles
- [Capture library](features/library.md): F-25 Capture library: browse, search, open; F-26 Capture catalog indexing and import
- [Tray menu](features/tray.md): F-27 Recent captures and recordings menus; F-28 Open snips, videos, and logs folders; F-43 First-run welcome
- [Settings](features/settings.md): F-29 Settings window; F-30 Custom global hotkeys; F-31 Light and dark theme; F-42 Share destination settings
- [Updates and About](features/updates.md): F-32 Update check and install; F-33 About window
- [Telemetry](features/telemetry.md): F-34 App lifecycle and usage telemetry; F-44 CLI and MCP operation telemetry: one anonymous event per MCP tool call or CLI command, first-run notice, opt-out
- [CLI and MCP](features/cli-mcp.md): F-35 CLI: capture/OCR/recording plus managed MCP install, status, and doctor for VS Code, and `mcp serve` (the stdio launcher a project's agent config runs); F-36 MCP capture, OCR, recording, and library tools; F-37 MCP desktop testing tools, signed reports, and exportable scenarios; F-45 Claude plugin: the pinned MCP server plus the desktop-verification, verification-skill generator, and capture skills, installed from the Claude plugin directory (the skills also install with `npx skills add`)
- [Verify](features/verify.md): F-39 CLI: spec-driven verification (`verify init`, `setup`, `run`, `scenario add`, `status`, `trust`, `task start`, `hook stop`, `agent`): gates, desktop scenarios, frozen task criteria, project onboarding, interactive desktop tools for the spec's app

<!-- /features -->

## Architecture rules

Rules to keep in every change, one line each; the linked section holds the why, enforcement, and symptoms. Recipes: [add a user setting](how-tos.md#add-a-user-setting), [register a service](how-tos.md#register-a-service).

- **Composition and DI.** `App.xaml.cs` builds the Generic Host. Every public service has an `I<ServiceName>` interface and is registered in `AddPointframeAppServices` (`AppServiceRegistration.cs`): singleton for long-lived state and OS handles, transient per operation (ViewModels, windows), scoped only for EF Core, a `Func<...>` factory when it needs runtime arguments; new `OverlayWindow` dependencies go in `CreateOverlayWindow`. See [App bootstrap](composition.md#app-bootstrap-di-and-messaging).
- **Messaging.** Decouple tray, overlay, recording, and windows with `IEventAggregator` messages (`Pointframe/Services/Messaging/`), not window references.
- **Startup and shutdown.** The keyboard hook is unhooked in `OnExit`. Windows launched from a tray callback are deferred until the tray menu unwinds and get a real owner window, or they lose focus and vanish.
- **MVVM.** ViewModels inherit `ObservableObject`; `[ObservableProperty]` on `private _camelCase` fields, `[RelayCommand]` on private methods, never `OnPropertyChanged()` by hand.
- **Settings.** Read `IUserSettingsService.Current` at the point of use; never cache it in a field or constructor parameter. Partial changes go through `Update(settings => settings.X = ...)`, which clones, mutates, and saves. A new setting changes `UserSettings.cs` (with its default) and `SettingsViewModel.Save()` together. See [Settings persistence](composition.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files).
- **Undo.** The undo stack grows only in `AnnotationViewModel.CommitGroup()`; shape handlers track elements only in `Commit`, never drafts. See [Undo groups](features/annotation.md#undo-groups-are-added-only-on-commit).
- **DPI.** WPF works in DIPs, screen and GDI in physical pixels, per monitor (`physical_px = dip * scale`; scale from the window's `PresentationSource` or `MonitorDpiHelper.GetMonitorScale(point)`). Assign window bounds before `Show()`. Recording visuals do not convert; they call `RecordingSessionGeometry.cs`, which is canonical, and recording width and height are even. See [DIPs and physical pixels](composition.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor).
- **Installer file list.** Whatever `dotnet publish` leaves beside `Pointframe.exe` must be in the `[Files]` section of `installer/Pointframe.iss`; a publish property that changes that set changes the installer in the same commit. See [the installer invariant](composition.md#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list).
- **`pointframe verify` is not `scripts/verify.ps1`.** The product feature (F-39 in `docs/knowledge-base/features/verify.md`) lives in `Pointframe.Cli/Verification/` with tests in `Pointframe.Tests/Cli/`; `pwsh scripts/verify.ps1` is only this repo's done-gate. For manual trials use an installed or copied CLI, never `Pointframe.Cli/bin`.
- **New annotation tool.** Follow [Add an annotation tool](features/annotation.md#add-an-annotation-tool); it includes the automation ids and smoke tests.

## Decisions

A decision lives with what it governs (see [Structure and conventions](maintenance.md#structure-and-conventions)); a decision that spans areas is in [decisions.md](decisions.md). The list is generated.

<!-- decisions -->

- [D-001 MVVM plus DI is the composition model](decisions.md#d-001-mvvm-plus-di-is-the-composition-model)
- [D-002 One knowledge base file, checked by script](decisions.md#d-002-one-knowledge-base-file-checked-by-script) (superseded)
- [D-003 Recording uses one authoritative session geometry](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry)
- [D-004 Native libraries ship loose and the installer packages them](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them)
- [D-005 The speech model is delivered by both the installer and the app](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app)
- [D-006 Cross-cutting knowledge base plus one file per feature area](decisions.md#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area)
- [D-007 `verify run` drives the MCP server instead of sharing an Engine service](features/verify.md#d-007-verify-run-drives-the-mcp-server-instead-of-sharing-an-engine-service)
- [D-008 Verify approvals need a person at a terminal and live outside the repository](features/verify.md#d-008-verify-approvals-need-a-person-at-a-terminal-and-live-outside-the-repository) (superseded)
- [D-009 An approver agent behind fixed rules approves verify commands](features/verify.md#d-009-an-approver-agent-behind-fixed-rules-approves-verify-commands)
- [D-010 Verify's agent roles run on any agent](features/verify.md#d-010-verifys-agent-roles-run-on-any-agent)
- [D-011 Standard commands are approved by policy](features/verify.md#d-011-standard-commands-are-approved-by-policy)
- [D-012 Version compatibility](features/verify.md#d-012-version-compatibility)
- [D-013 Verification spec v1 is frozen](features/verify.md#d-013-verification-spec-v1-is-frozen)
- [D-014 Interactive desktop testing reuses verify trust](features/verify.md#d-014-interactive-desktop-testing-reuses-verify-trust)
- [D-015 Agent telemetry carries a random install ID](features/telemetry.md#d-015-agent-telemetry-carries-a-random-install-id)
- [D-016 Agents follow one workflow in the knowledge base](decisions.md#d-016-agents-follow-one-workflow-in-the-knowledge-base)
- [D-017 Agent outcomes separate failing verdicts and invalid input](features/telemetry.md#d-017-agent-outcomes-separate-failing-verdicts-and-invalid-input)

<!-- /decisions -->
