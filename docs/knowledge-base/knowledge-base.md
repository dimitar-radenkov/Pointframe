# Pointframe Knowledge Base

Durable, agent-readable project knowledge. Start every session with `/kb-init` (Codex: `pwsh scripts/kb.ps1 init`; coordinators add `-Role coordinator`): it prints this file, the workflow rules for your role, environment facts, and, with a target, the sections and lessons for that file, feature, or topic.
This file holds what is cross-cutting: how the app is composed, rules every area must keep, and shared recipes, plus generated indexes of features and decisions.
Next to it: [file-map.md](file-map.md) (which sections to read before editing a path), [references.md](references.md) (runtime paths, binaries, configuration), [tooling.md](tooling.md) (CI, CD, versioning, merge and release scripts), [workflow.md](workflow.md) (agent roles and rules), [decisions.md](decisions.md), and one file per feature area under `docs/knowledge-base/features/`; open an area file only when the task touches that area.
Start with the [Feature index](#feature-index) or run `/kb-read <file, area, F-NN, or topic>` (`pwsh scripts/kb.ps1 read ...`).

Last full review against the code: 2026-09-05.

## How to maintain this file

**What belongs where.**

| Knowledge | Goes to |
|---|---|
| Composition, DI, messaging; architecture rules and rules that hold for every area; recipes any area uses | This file |
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

### Structure and conventions

**This file's structure.** A generated [Feature index](#feature-index), the [Architecture rules](#architecture-rules), then four fixed groups, each a `##` heading with one `###` section per topic (Decisions holds only a generated index). `references.md` and `tooling.md` follow the References layout, and the File map lives in `file-map.md`.

| Group | Section layout |
|---|---|
| Subsystems | Responsibility, Entry points, Flow, Key types, Invariants, Tests, Files, Lessons |
| Decisions | A generated index only. Each decision is a `## D-NNN Title` section in `decisions.md` or its area file: decided date, Context, Decision, Consequences, Alternatives rejected, Files. Numbers are global and never reused; a reversed decision stays with "Superseded by D-NNN" on its first line |
| Invariants | Rule, Why, Enforced by, Symptoms when violated, Files |
| How-tos | When, Steps, Verify, Files |
| References | Tables or short lists, each fact with its source, Files |

**A feature area file's structure.** `# <Area>`, a one-line scope, then `## Features`: one row per user-facing feature, `| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |`, with a stable `F-NN` ID that is never renumbered or reused. Then one `##` section per topic, using the Subsystems, Invariants, or How-tos layout above. A new area gets a new file; split a file that passes about 150 lines.

**Sections.** A section is a `##` or `###` heading and the text up to the next heading of any level. A `###` section also carries the text between its `##` heading and the first `###` (its parent preamble), so reading it costs both. A `##` heading that only groups `###` sections is a container: `kb read` and the File map point at the `###` sections, and a File map or feature row that links a container is an error. In an area file the `## Features` table is not a section.

**Conventions the script checks.** In every file: repo paths in backticks exist; `- Lesson: <exact heading from lessons.md>` bullets match `lessons.md`; links such as `[Recording pipeline](features/recording.md#recording-pipeline)` resolve to a heading in the target file; a section has at most one `**Files.**` line; no two headings in one file share an anchor.
Feature IDs are unique across files; every feature names an entry point and links a section; every telemetry event named exists in `TelemetryEvents`, and every event there belongs to some feature; every smoke test in `Pointframe.AutomationTests/Smoke/` belongs to some feature. Every File map pattern matches a tracked file, and every tracked file matches a row.
In `lessons.md` every lesson has exactly one Problem, What fixed it, and Takeaway subsection (Root cause is optional) and is reachable: a `- Lesson:` line in a section that a File map row, a feature row, or a task-suite task reaches. The table of contents, the Feature index, and the decision index are generated. CI runs `kb.ps1 check -NoFix`, `scripts/verify.ps1` runs `kb.ps1 selftest` first, and the project hook in `.claude/settings.json` runs `kb.ps1 hook` when an agent reads or edits a file; it says each section, feature, and lesson once per session.

**Writing rules.** Say why, not only what. Replace stale text in place; never append a correction below an old statement. Prefer a table or a short list over prose. Keep a section under 8 KB (checked, see Budgets). Absolute dates only. No status badges or task journals.

### Budgets, task suite, and rule IDs

**Budgets.** A section over 8192 bytes (a `###` section counts its parent preamble) and this file over 12288 bytes are errors unless `docs/knowledge-base/kb-baseline.json` lists them at their current size or more. `kb.ps1 check` lowers a listed entry when the section shrinks and drops it when the section fits, and never raises one; a new or grown violation fails. Write new knowledge into a section that stays under the budget, or split. Aim for 8-10 KB in this file.
Warnings, not errors: a prose line over 600 characters (tables and `**Files.**` lines are exempt), an area file over 40 KB, and changelog phrasing in an area file (`since YYYY-MM-DD`, `added YYYY-MM-DD`, `As of YYYY`; decision dates are exempt).

**Measuring.** `pwsh scripts/kb.ps1 stats [-Json]` prints bytes and estimated tokens (bytes / 4) per file and section, the bytes `init` prints per part, and, for each task in `docs/knowledge-base/kb-tasks.json` (a file path, an `F-NN`, or a topic), the required-section, lesson, and hook bytes and the recall of its expected sections and lessons. `check` fails when a task's recall drops below 1.0, so a routing change that hides a section or lesson is caught. `init` with a target aims at 25 KB in total and prints an overage line when a part passes its budget: core 12 KB, environment 1.5 KB, status 2 KB, target 12 KB, and rules 4 KB for an executor or reviewer but 16 KB for the coordinator, who owns review, merge, status, and model allocation and so carries most of `workflow.md`. A new rule that pushes a role past its budget should be shortened or moved into a section the role reads on demand.

**Workflow rule IDs.** `workflow.md` marks each canonical rule with `<!-- rule:W-NN roles=<all|coordinator|executor|reviewer list> -->` on the line before its bullet, paragraph, or table, and names extra sections a role should read with `<!-- rule-section roles=... -->` before a `##` heading. IDs are stable and never reused. `kb init` prints the rules of the agent's role verbatim, with their IDs; `check` fails on a missing or duplicate ID, an unknown role, or a trigger that points at no heading. Each marker needs a nonempty list of valid roles, and every block below the first `##` heading must be a marked rule or sit under a trigger. The ID and roles of every rule are also registered in `docs/knowledge-base/kb-baseline.json` (`workflowRules`): `check -NoFix` fails when a registered ID is missing, its roles changed, or an ID is unregistered, because a trigger can hide a deleted marker. `check` without `-NoFix` only adds new IDs; removing a rule or changing its roles is a hand edit of the JSON, visible in review.

### Maintenance commands

**Maintenance.** Four skills, all backed by `scripts/kb.ps1`. Edit them only under `.claude/skills/kb-*`; `check` regenerates the Codex copies in `.agents/skills/`.

| Skill | Does | Script |
|---|---|---|
| `/kb-init [coordinator\|executor\|reviewer] [target]` | Starts a session: this file, the role's workflow rules, environment facts, and the target's sections and top lessons (a menu for a broad topic) | `pwsh scripts/kb.ps1 init [-Role <role>] [target]` |
| `/kb-read <file, area, F-NN, or topic>` | Lists the sections, features, and lessons to read | `pwsh scripts/kb.ps1 read <target> [-All]` |
| `/kb-write [topic]` | Adds or updates knowledge within the budgets; with no topic, sweeps the current diff | `pwsh scripts/kb.ps1 changed [-Base <ref>]` |
| `/kb-check` | Refreshes the generated blocks and the baseline, checks every file, fixes what it reports | `pwsh scripts/kb.ps1 check` (CI: `check -NoFix`) |

`pwsh scripts/kb.ps1 selftest` runs the tool against synthetic fixtures in `scripts/kb-fixtures/` (section routing, hook dedup, baseline growth and shrinkage, rule IDs, `init` order and role filtering, the status allowlist, topic menus); `scripts/verify.ps1` runs it before `check -NoFix`.

When a change adds a folder, a project, or a file that belongs to a different section than its folder's row says, add or narrow a File map row in the same change; a row may not be broader than the section that owns its files. When it adds, removes, or rewires a user-facing feature, a telemetry event, or a smoke test, update the feature's row in its area file in the same change.

**Optional SessionStart hook.** To print `kb init` at the start of every Claude Code session, add this to your own `settings.local.json` in the `.claude` folder (it is gitignored); do not commit it to `settings.json`. The guard keeps a resumed or compacted session from printing it twice, because a `SessionStart` hook receives the session ID on standard input:

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -Command '$j = [Console]::In.ReadToEnd() | ConvertFrom-Json; $g = Join-Path $env:TEMP \"pointframe-kb-init-$($j.session_id)\"; if (-not (Test-Path $g)) { New-Item $g -Force | Out-Null; & ./scripts/kb.ps1 init }'"
          }
        ]
      }
    ]
  }
}
```
## Contents

<!-- toc -->

- [Feature index](#feature-index)
- [Subsystems](#subsystems)
  - [App bootstrap, DI, and messaging](#app-bootstrap-di-and-messaging)
- [Architecture rules](#architecture-rules)
- [Decisions](#decisions)
- [Invariants](#invariants)
  - [Settings are read at the point of use and persisted through three files](#settings-are-read-at-the-point-of-use-and-persisted-through-three-files)
  - [DIPs and physical pixels are converted explicitly per monitor](#dips-and-physical-pixels-are-converted-explicitly-per-monitor)
  - [Everything emitted next to the exe must be in the installer file list](#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list)
- [How-tos](#how-tos)
  - [Add a user setting](#add-a-user-setting)
  - [Register a service](#register-a-service)

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
- [CLI and MCP](features/cli-mcp.md): F-35 CLI: capture/OCR/recording plus managed MCP install, status, and doctor for VS Code, and `mcp serve` (the stdio launcher a project's agent config runs); F-36 MCP capture, OCR, recording, and library tools; F-37 MCP desktop testing tools, signed reports, and exportable scenarios; F-39 CLI: spec-driven verification (`verify init`, `setup`, `run`, `scenario add`, `status`, `trust`, `task start`, `hook stop`, `agent`): gates, desktop scenarios, frozen task criteria, project onboarding, interactive desktop tools for the spec's app; F-45 Claude plugin: the pinned MCP server plus the desktop-verification, verification-skill generator, and capture skills, installed from the Claude plugin directory (the skills also install with `npx skills add`)

<!-- /features -->

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

## Architecture rules

Rules to keep in every change. The why, the flows, and the recipes are in the sections they link.

- **Composition.** `App.xaml.cs` builds the Generic Host; every service and window is registered in `AppServiceRegistration.cs`. Singleton for long-lived state, transient per operation. See [App bootstrap](#app-bootstrap-di-and-messaging).
- **MVVM.** ViewModels inherit `ObservableObject`; `[ObservableProperty]` on `private _camelCase` fields, `[RelayCommand]` on private methods, never `OnPropertyChanged()` by hand. Every public service has an `I<ServiceName>` interface and a DI registration.
- **Settings.** Read `IUserSettingsService.Current` at the point of use; never cache it in a field. A new setting changes `UserSettings.cs` and `SettingsViewModel.Save()` together. See [Add a user setting](#add-a-user-setting).
- **Undo.** The undo stack grows only in `AnnotationViewModel.CommitGroup()`; shape handlers track elements only in `Commit`, never drafts. See [Undo groups](features/annotation.md#undo-groups-are-added-only-on-commit).
- **DPI.** WPF works in DIPs, screen and GDI in physical pixels, per monitor; `RecordingSessionGeometry.cs` is canonical, and recording width and height are even. See [DIPs and physical pixels](#dips-and-physical-pixels-are-converted-explicitly-per-monitor).
- **`pointframe verify` is not `scripts/verify.ps1`.** The product feature (F-39 in `docs/knowledge-base/features/cli-mcp.md`) lives in `Pointframe.Cli/Verification/` with tests in `Pointframe.Tests/Cli/`; `pwsh scripts/verify.ps1` is only this repo's done-gate. For manual trials use an installed or copied CLI, never `Pointframe.Cli/bin`.
- **New annotation tool.** Follow [Add an annotation tool](features/annotation.md#add-an-annotation-tool); it includes the automation ids and smoke tests.

## Decisions

A decision records why a choice was made over the obvious alternative, so nobody reverses it by accident. It lives with what it governs: a decision that only one area needs is in that area file, and one that spans areas is in [decisions.md](decisions.md). Numbers are global across files and never reused; a reversed decision stays, with "Superseded by D-NNN" on its first line. The list is generated.

<!-- decisions -->

- [D-001 MVVM plus DI is the composition model](decisions.md#d-001-mvvm-plus-di-is-the-composition-model)
- [D-002 One knowledge base file, checked by script](decisions.md#d-002-one-knowledge-base-file-checked-by-script) (superseded)
- [D-003 Recording uses one authoritative session geometry](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry)
- [D-004 Native libraries ship loose and the installer packages them](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them)
- [D-005 The speech model is delivered by both the installer and the app](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app)
- [D-006 Cross-cutting knowledge base plus one file per feature area](decisions.md#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area)
- [D-007 `verify run` drives the MCP server instead of sharing an Engine service](features/cli-mcp.md#d-007-verify-run-drives-the-mcp-server-instead-of-sharing-an-engine-service)
- [D-008 Verify approvals need a person at a terminal and live outside the repository](features/cli-mcp.md#d-008-verify-approvals-need-a-person-at-a-terminal-and-live-outside-the-repository) (superseded)
- [D-009 An approver agent behind fixed rules approves verify commands](features/cli-mcp.md#d-009-an-approver-agent-behind-fixed-rules-approves-verify-commands)
- [D-010 Verify's agent roles run on any agent](features/cli-mcp.md#d-010-verifys-agent-roles-run-on-any-agent)
- [D-011 Standard commands are approved by policy](features/cli-mcp.md#d-011-standard-commands-are-approved-by-policy)
- [D-012 Version compatibility](features/cli-mcp.md#d-012-version-compatibility)
- [D-013 Verification spec v1 is frozen](features/cli-mcp.md#d-013-verification-spec-v1-is-frozen)
- [D-014 Interactive desktop testing reuses verify trust](features/cli-mcp.md#d-014-interactive-desktop-testing-reuses-verify-trust)
- [D-015 Agent telemetry carries a random install ID](features/telemetry.md#d-015-agent-telemetry-carries-a-random-install-id)
- [D-016 Agents follow one workflow in the knowledge base](decisions.md#d-016-agents-follow-one-workflow-in-the-knowledge-base)
- [D-017 Agent outcomes separate failing verdicts and invalid input](features/telemetry.md#d-017-agent-outcomes-separate-failing-verdicts-and-invalid-input)

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
2. `Pointframe/ViewModels/SettingsViewModel.cs`: add an `[ObservableProperty]` field, load it from `Current` in the constructor, write it back in `Save()`, and reset it in its section's `Reset<Section>Section` method. Both "Reset section" and "Restore defaults" call those methods, so one edit covers both.
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
