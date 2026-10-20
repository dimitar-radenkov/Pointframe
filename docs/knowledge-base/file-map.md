# Repository file map

Which sections to read before editing a file. The routing table moved out of [the core file](knowledge-base.md) so a session that does not need it does not load it; pwsh scripts/kb.ps1 read <path> resolves it for you.

Part of the [Pointframe knowledge base](knowledge-base.md).

## Reading the file map

Answers "I am about to change this file: what must I read first?" Every tracked file matches at least one row; **every** matching row applies, and rows run from coarse to fine. A row names the sections to read before editing; the lessons are inside those sections.

Fastest route: let the script resolve it. It prints the matching rows, the features that use the file, the linked sections with their line numbers, every other section that names the exact file, and the most relevant lessons:

```powershell
pwsh scripts/kb.ps1 read Pointframe/Views/OverlayWindow.Recording.cs
```

**Pattern syntax** (anchored at the repo root like `.gitignore`; unlike CODEOWNERS, where the last match wins, every matching row applies): `*` matches within one folder, `**` crosses folders, `?` is one character. A pattern without `/` matches only root files. Several patterns in one cell are separated by commas. `—` in "Read first" means no section owns the area yet.

**Rules the script checks.** Every pattern matches at least one tracked file. Every tracked file matches at least one row, so a new top-level folder or project fails the check until it gets a row. No row is redundant: a row whose files and links all sit inside another row is an error. Descriptions stay under 160 characters; explanation belongs in a section. Every link points at a heading. Tests are mapped by folder only: a test file is named after the class it covers, so look up that class.

## File map

### Repository root, docs, and coordinator scripts

| Path | What lives here | Read first |
|---|---|---|
| `*`, `.vscode/**` | Root config: solution, central package versions, NBGV `version.json`, local tools, editor and MCP client config | [Release qualification and versioning](tooling.md#release-qualification-and-versioning) |
| `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`, `lessons.md`, `docs/**`, `.claude/**`, `.codex/**`, `.agents/skills/kb-*/**`, `scripts/kb.ps1`, `scripts/agent-workflow/**` | Agent and contributor docs, this file, kb skills and their wrapper, the vendored workflow kit (never edited here), Claude Code and Codex hooks | [How to maintain this file](knowledge-base.md#how-to-maintain-this-file), [Agent workflow](workflow.md), [D-006](decisions.md#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area) |
| `docs/cli/**`, `docs/mcp/**`, `docs/mcp-desktop-testing/**` | CLI and MCP user docs; the DocsSync tests fail when they drift from the code | [Docs-sync tests](features/cli-mcp.md#docs-sync-tests) |
| `README.md`, `PRIVACY.md`, `docs/app.md`, `docs/images/**` | Landing page (at most 10,240 B, old anchors kept as stubs), privacy policy and telemetry catalog, desktop app guide, doc images | [Docs-sync tests](features/cli-mcp.md#docs-sync-tests) |
| `docs/appinsights*` | Kusto queries and the workbook template | [Telemetry](features/telemetry.md#telemetry-pipeline) |
| `scripts/update-download-badges.ps1` | Download totals across all releases as shields.io endpoint badges, built by the Pages workflow; offline `-SelfTest` | [Workflows](tooling.md#workflows) |
| `scripts/usage-report.ps1`, `scripts/tests/usage-report/**` | Usage report from Application Insights (activation, cohorts, retention, onboarding, failures, website) and the offline fixtures its `-SelfTest` shapes | [Telemetry](features/telemetry.md#telemetry-pipeline) |
| `scripts/measure-mcp-payloads.ps1` | Offline self-test and MCP stdio payload inventory with a per-run fixture desktop policy | [Embedded guide and discovery size](features/cli-mcp.md#embedded-guide-and-discovery-size) |
| `scripts/merge-pr.ps1`, `docs/knowledge-base/hooks/pin.ps1` | Agent merge tool wrapper (kit): receipt- and review-gated merge, `-PinOnly`, `-SyncMain`; the plugin pin hook | [Local gates and merging](tooling.md#local-gates-and-merging), [Agent workflow](workflow.md) |
| `scripts/record-review.ps1` | Wrapper (kit): validates and writes the independent review record for a head SHA under git's common directory | [Agent workflow](workflow.md) |
| `scripts/agent-run.ps1` | Wrapper (kit): checked Codex executor launcher with brief and policy validation, runtime budgets, token accounting, and cost records | [Local gates and merging](tooling.md#local-gates-and-merging), [Agent workflow](workflow.md), [Brief and acceptance criteria](workflow.md#brief-and-acceptance-criteria) |
| `scripts/package.ps1`, `scripts/package-review-prompt.txt`, `scripts/package-verdict.schema.json`, `scripts/tests/package-claude/**` | Work package runner: run, check, commit, host-checked review verdicts, fix, accept, ship; review prompt and verdict schema; recorded streams | [Local gates and merging](tooling.md#local-gates-and-merging), [Agent workflow](workflow.md) |
| `scripts/codex-ask.ps1` | Wrapper (kit): Codex consultation helper (explicit model, effort, session; rejects stale output) and the Codex policy it shares with `agent-run.ps1` | [Agent workflow](workflow.md) |
| `scripts/status.ps1` | Wrapper (kit): authoritative status store, GitHub #186 and handoff renderer | [Local gates and merging](tooling.md#local-gates-and-merging), [Agent workflow](workflow.md) |
| `scripts/check-agent-discovery.ps1`, `scripts/tests/agent-discovery/**` | Offline check of the agent page, llms.txt and drafts against the READMEs, `PRIVACY.md`, packaging and CD assets; `-Online`, `-Snapshot`; fixtures | [Embedded guide and discovery size](features/cli-mcp.md#embedded-guide-and-discovery-size) |

### Release, verification, and packaging scripts

| Path | What lives here | Read first |
|---|---|---|
| `scripts/check-released-desktop.ps1` | Coordinator's post-release or candidate desktop qualification of CLI/MCP packages; offline `-SelfTest` | [Release acceptance checks](features/cli-mcp.md#release-acceptance-checks) |
| `scripts/release-impact.ps1`, `scripts/check-release-channels.ps1`, `scripts/tests/release-channels/**` | Release risk classification and channel state tracking with offline fixtures | [Release qualification and versioning](tooling.md#release-qualification-and-versioning) |
| `scripts/test-scripts.ps1` | Offline `-SelfTest` of every coordinator and release script; fails on a git-ignored fixture | [Local gates and merging](tooling.md#local-gates-and-merging) |
| `scripts/trial.ps1`, `scripts/trials/**`, `scripts/tests/trial/**` | Fresh-agent trial harness: app definitions, isolated agent run, harness-side scoring; fixtures for its offline `-SelfTest` | [Fresh-agent trials](tooling.md#fresh-agent-trials) |
| `scripts/weekly-trial.ps1`, `scripts/tests/weekly-trial/**` | Weekly README canary: runs `trial.ps1`, compares with a retained baseline, one deduplicated issue; `-SelfTest` fixtures | [Fresh-agent trials](tooling.md#fresh-agent-trials) |
| `scripts/check-workflow-scripts.ps1` | Wrapper (kit): parses the PowerShell in workflow `run:` blocks | [Workflows](tooling.md#workflows) |
| `scripts/verify.ps1` | Wrapper (kit) for the one local verify command: gates from `agent-workflow.json`, kit doctor, JSON verdict; `-ReuseIfFresh` | [Local gates and merging](tooling.md#local-gates-and-merging) |
| `scripts/desktop-tests.ps1` | Interactive Windows Release desktop regression selection, repeated TRX evidence, and optional real-app verification | [Local gates and merging](tooling.md#local-gates-and-merging) |
| `scripts/test-agent-onboarding.ps1` | Agent onboarding acceptance: client registration, MCPB check, fixture capture over stdio; `-AgentSmoke`; `-Client claude-plugin` | [Packaging and install](features/cli-mcp.md#packaging-and-install) |
| `scripts/update-plugin-pin.ps1` | Pins `plugin/pointframe/server.lock.json` and the plugin version to a release (called by the pin hook `docs/knowledge-base/hooks/pin.ps1`) | [Claude plugin](features/cli-mcp.md#claude-plugin) |
| `scripts/sync-agent-guides.ps1` | Writes the generated skill copies of the desktop verification guide; `-Check` reports drift (run by `check-agent-discovery.ps1`) | [Claude plugin](features/cli-mcp.md#claude-plugin) |
| `plugin/**` | The Claude plugin for the Anthropic plugin directory: manifest, `.mcp.json`, start script, lock file, skills | [Claude plugin](features/cli-mcp.md#claude-plugin) |
| `.github/**`, `winget/**`, `website/**` | Workflows, Dependabot, release drafter, winget manifests, the GitHub Pages site | [Workflows](tooling.md#workflows), [Installer and packaging](tooling.md#installer-and-packaging) |
| `installer/**`, `Pointframe/Properties/**` | Inno Setup script, installer build and smoke scripts, publish profile | [Installer and packaging](tooling.md#installer-and-packaging), [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them), [Installer file list](composition.md#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list), [D-005](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app) |
| `Directory.Packages.props`, `Pointframe/Pointframe.csproj` | Package versions and app project references; a package that ships a native binary changes the installer file list | [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them), [Installer file list](composition.md#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list) |
| `packaging/**` | CLI and MCP zip packaging, MCP stdio smoke script, directory submission drafts (`packaging/directory-submissions/`) | [Packaging and install](features/cli-mcp.md#packaging-and-install), [MCP command list](features/cli-mcp.md#the-mcp-command-list-resource-must-name-every-registered-tool) |

### WPF app `Pointframe/`: host, infrastructure, and services

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe/*`, `Pointframe/Assets/**`, `Pointframe/Automation/**` | Host startup, DI registration, project file, automation launch options | [App bootstrap](composition.md#app-bootstrap-di-and-messaging), [D-001](decisions.md#d-001-mvvm-plus-di-is-the-composition-model), [Register a service](how-tos.md#register-a-service) |
| `Pointframe/app.manifest`, `Pointframe/Native/**` | PerMonitorV2 manifest, Win32 interop, per-monitor DPI helpers | [DIPs and physical pixels](composition.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/appsettings.json`, `Pointframe/Services/Infrastructure/AppPaths.cs` | Configuration and on-disk locations | [Runtime paths and external binaries](references.md#runtime-paths-and-external-binaries) |
| `Pointframe/Services/Messaging/**` | Event aggregator and message records | [App bootstrap](composition.md#app-bootstrap-di-and-messaging) |
| `Pointframe/Services/Infrastructure/**` | Cross-cutting singletons: dialogs, files, processes, hotkeys, tray, theme, errors | [App bootstrap](composition.md#app-bootstrap-di-and-messaging), [Register a service](how-tos.md#register-a-service) |
| `Pointframe/Services/Infrastructure/*UserSettings*`, `Pointframe/Services/Infrastructure/*Theme*`, `Pointframe/Services/Infrastructure/*Hotkey*`, `Pointframe/Themes/**` | Settings persistence, theme switching, hotkey bindings | [User settings](features/settings.md#user-settings), [Settings invariant](composition.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) |
| `Pointframe/Services/Infrastructure/*Telemetry*` | Telemetry client, event catalog, heartbeat, activation | [Telemetry](features/telemetry.md#telemetry-pipeline) |
| `Pointframe/Services/Infrastructure/*Ocr*` | WPF-side OCR for the overlay lasso; library search reads text indexed by the catalog worker | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection), [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe/Services/Infrastructure/*Microphone*`, `Pointframe/Services/Infrastructure/*MouseHook*` | WASAPI microphone enumeration; global mouse hook used by recording cursor effects, the overlay, and capture launch | [Recording flow and entry points](features/recording.md#recording-flow-and-entry-points), [Recording invariants, files, tests, and lessons](features/recording.md#recording-invariants-files-tests-and-lessons) |
| `Pointframe/Services/Infrastructure/*AppVersion*`, `Pointframe/Services/Update/**` | Release check, download, version source | [Update flow](features/updates.md#update-flow) |
| `Pointframe/Services/Capture/**` | Capture launch, selection session, screen and window capture, clipboard, image files | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection) |
| `Pointframe/Services/Share/**` | Configurable multipart capture upload and HTTPS link extraction | [Upload a capture and copy its link](features/capture.md#features), [Share destination settings](features/settings.md#share-destination-settings) |
| `Pointframe/Services/Capture/*CaptureLibrary*`, `Pointframe/Services/Capture/*CaptureTextLookup*` | Library listing, search, OCR text cache | [Capture library](features/library.md#capture-library-and-data-layer) |

### WPF app `Pointframe/`: annotation, recording, models, and windows

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe/Services/Annotation/**` | Canvas renderer, geometry, interaction controller, shape handlers | [Annotation engine](features/annotation.md#annotation-engine), [Undo invariant](features/annotation.md#undo-groups-are-added-only-on-commit), [Add an annotation tool](features/annotation.md#add-an-annotation-tool) |
| `Pointframe/Services/Recording/**` | Recording session, ffmpeg writer, HUD and overlay coordinators, redaction, GIF, trim, watermarks | [Recording flow and entry points](features/recording.md#recording-flow-and-entry-points), [Recording invariants, files, tests, and lessons](features/recording.md#recording-invariants-files-tests-and-lessons), [Even dimensions](features/recording.md#recording-width-and-height-are-even) |
| `Pointframe/Services/Recording/*Coordinator.cs`, `Pointframe/Services/Recording/RecordingOverlayNativeInterop.cs`, `Pointframe/Views/OverlayWindow.RecordingHud.cs`, `Pointframe/Views/RecordingOverlay*` | Placing the recording HUD, border, and annotation surface on the target monitor | [Recording flow and entry points](features/recording.md#recording-flow-and-entry-points), [DIPs and physical pixels](composition.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/Services/Recording/*Ffmpeg*`, `Pointframe/Services/Recording/FFMpeg*` | ffmpeg discovery and the video writer process | [Runtime paths and external binaries](references.md#runtime-paths-and-external-binaries) |
| `Pointframe/Services/Recording/Beautif*`, `Pointframe/Services/Recording/*ScreenshotWatermark*` | Screenshot post-processing reached from the overlay toolbar | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection), [User settings](features/settings.md#user-settings) |
| `Pointframe/Services/Transcription/**`, `Pointframe/Models/Transcript*` | Whisper transcription queue, model resolution, subtitles | [Recording transcription](features/transcription.md#recording-transcription), [D-005](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app) |
| `Pointframe/Models/**`, `Pointframe/ViewModels/**`, `Pointframe/Views/**` | Models, ViewModels, and windows; the finer rows below name the owning subsystem | [D-001](decisions.md#d-001-mvvm-plus-di-is-the-composition-model) |
| `Pointframe/Models/Annotation*`, `Pointframe/Models/ShapeParameters.cs`, `Pointframe/ViewModels/Annotation*`, `Pointframe/ViewModels/RecordingAnnotation*`, `Pointframe/Views/OverlayWindow.RecordingAnnotation.cs` | Tools, shape parameters, style presets, annotation state and undo | [Annotation engine](features/annotation.md#annotation-engine), [Undo invariant](features/annotation.md#undo-groups-are-added-only-on-commit), [Add an annotation tool](features/annotation.md#add-an-annotation-tool) |
| `Pointframe/Models/Selection*`, `Pointframe/Models/BeautifyBackground.cs`, `Pointframe/ViewModels/Overlay*`, `Pointframe/ViewModels/Beautifier*`, `Pointframe/Views/OverlayWindow*`, `Pointframe/Views/Overlay*`, `Pointframe/Views/Selection*`, `Pointframe/Views/PinnedScreenshot*`, `Pointframe/Views/Beautifier*` | Overlay window partials, selection windows, pin and beautify | [Capture overlay and selection](features/capture.md#capture-overlay-and-selection), [DIPs and physical pixels](composition.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/Models/Recording*`, `Pointframe/Services/Messaging/Recording*`, `Pointframe/Services/Messaging/Trim*`, `Pointframe/ViewModels/RecordingHud*`, `Pointframe/ViewModels/Trim*`, `Pointframe/Views/OverlayWindow.Recording*`, `Pointframe/Views/RecordingOverlay*`, `Pointframe/Views/Countdown*`, `Pointframe/Views/Trim*` | Recording geometry, events, redaction regions, HUD, countdown, trim | [Recording flow and entry points](features/recording.md#recording-flow-and-entry-points) |
| `Pointframe/Models/RecordingSessionGeometry.cs` | The one geometry model per recording session | [D-003](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry), [Even dimensions](features/recording.md#recording-width-and-height-are-even), [DIPs and physical pixels](composition.md#dips-and-physical-pixels-are-converted-explicitly-per-monitor) |
| `Pointframe/Models/UserSettings.cs`, `Pointframe/Models/Settings*`, `Pointframe/Models/Hotkey*`, `Pointframe/Models/AppTheme.cs`, `Pointframe/Models/*Watermark*`, `Pointframe/ViewModels/Settings*`, `Pointframe/Views/Settings*` | Settings model, settings window and its sections | [User settings](features/settings.md#user-settings), [Settings invariant](composition.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files), [Add a user setting](how-tos.md#add-a-user-setting) |
| `Pointframe/Models/Capture*`, `Pointframe/ViewModels/Library*`, `Pointframe/Views/Library*` | Library window and capture items | [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe/Models/UpdateCheck*`, `Pointframe/Services/Messaging/Update*`, `Pointframe/ViewModels/UpdateDownload*`, `Pointframe/Views/UpdateDownload*`, `Pointframe/ViewModels/About*`, `Pointframe/Views/About*` | Update settings, download window, About window with the manual update check | [Update flow](features/updates.md#update-flow) |

### Other projects

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe.Telemetry/**`, `website/privacy.html` | Operation telemetry shared by the CLI and MCP server (allowlist, opt-out, exporter) and the hosted privacy policy | [CLI and MCP operation telemetry](features/telemetry.md#cli-and-mcp-operation-telemetry) |
| `Pointframe.Engine/*`, `Pointframe.Engine/Automation/**`, `Pointframe.Engine/Capture/**`, `Pointframe.Engine/Ocr/**`, `Pointframe.Engine/Recording/**`, `Pointframe.Mcp/*`, `Pointframe.Mcp/Automation/**`, `Pointframe.Mcp/Configuration/**`, `Pointframe.Mcp/Guides/**`, `Pointframe.Mcp/Mappers/**`, `Pointframe.Mcp/Models/**`, `Pointframe.Mcp/Services/**`, `Pointframe.DesktopTestFixture/**` | WPF-free capture, recording, OCR, and desktop automation engine; the MCP host; the WinForms fixture the desktop tools drive | [Overview, entry points, and invariants](features/cli-mcp.md#overview-entry-points-and-invariants), [MCP tools and resources](features/cli-mcp.md#mcp-tools-and-resources), [Desktop-testing driver and policy](features/cli-mcp.md#desktop-testing-driver-and-policy) |
| `Pointframe.Engine/Capture/**`, `Pointframe.Engine/Ocr/**`, `Pointframe.Engine/Recording/**` | Direct monitor and window capture, OCR, and recording services shared by the CLI and the MCP server | [Capture, OCR, and recording](features/cli-mcp.md#capture-ocr-and-recording) |
| `Pointframe.Engine/Automation/**`, `Pointframe.Mcp/Mappers/**` | Desktop observation, input, report, and proof services and the MCP result mappers | [Desktop observation and synthesized input](features/cli-mcp.md#desktop-observation-and-synthesized-input), [Desktop test reports, evidence, and scenarios](features/cli-mcp.md#desktop-test-reports-evidence-and-scenarios) |
| `Pointframe.Mcp/Automation/DesktopSelectorResolver.cs`, `Pointframe.Mcp/Automation/DesktopKeyChordParser.cs`, `Pointframe.Mcp/Automation/WorkerUiCheckSource.cs`, `Pointframe.Mcp/Automation/WindowsDesktopWindowBoundsProvider.cs` | Selector resolution, key chords, UI checks, and window bounds for the desktop tools | [Desktop UI checks and selector actions](features/verify.md#desktop-ui-checks-and-selector-actions) |
| `Pointframe.Mcp/Guides/**` | Embedded guides served by the MCP server | [Embedded guide and discovery size](features/cli-mcp.md#embedded-guide-and-discovery-size) |
| `Pointframe.Cli/*`, `Pointframe.Cli/Application/**`, `Pointframe.Cli/Commands/**`, `Pointframe.Cli/McpManagement/**` | CLI host, command parser, and the managed MCP install, status, and doctor commands | [CLI commands](features/cli-mcp.md#cli-commands), [Packaging and install](features/cli-mcp.md#packaging-and-install) |
| `Pointframe.Cli/Verification/**` | The `verify` commands: spec, gates, desktop scenarios, command approval, agent runners, and the stop hook | [Overview and run flow](features/verify.md#overview-and-run-flow), [Tasks, stop hook, and files](features/verify.md#tasks-stop-hook-and-files) |
| `Pointframe.Cli/Verification/DesktopToolsApplication.cs`, `Pointframe.Cli/Verification/VerificationInit.cs`, `Pointframe.Cli/Verification/PathContainment.cs` | `verify init`, `verify setup`, and the interactive desktop tools the CLI serves for the spec's app | [Initialization, setup, and scenario import](features/verify.md#initialization-setup-and-scenario-import), [D-014](features/verify.md#d-014-interactive-desktop-testing-reuses-verify-trust) |
| `Pointframe.Cli/Verification/DesktopScenarioRunner.cs`, `Pointframe.Cli/Verification/McpStdioToolClient.cs` | The desktop scenario runner and the MCP stdio client it drives | [Desktop UI checks and selector actions](features/verify.md#desktop-ui-checks-and-selector-actions), [D-007](features/verify.md#d-007-verify-run-drives-the-mcp-server-instead-of-sharing-an-engine-service) |
| `.pointframe/**` | Pointframe's fixture verification spec and real WPF app spec for `verify run` | [Overview and run flow](features/verify.md#overview-and-run-flow) |
| `.github/workflows/verify-samples.yml`, `samples/verify/**` | Release CLI verification samples and their Windows CI job | [Overview and run flow](features/verify.md#overview-and-run-flow) |
| `Pointframe.Mcp/Resources/**`, `Pointframe.Mcp/Tools/**` | Tool methods and the command list resource; a new tool also updates the contract test sets, `test-mcp-stdio.ps1`, and the two MCP READMEs | [MCP command list](features/cli-mcp.md#the-mcp-command-list-resource-must-name-every-registered-tool) |
| `Pointframe.Engine/Library/**`, `Pointframe.Data/**` | Capture catalog, import and index worker, EF Core context, repositories, migrations | [Capture library](features/library.md#capture-library-and-data-layer) |
| `Pointframe.Data/DependencyInjection.cs`, `Pointframe.Data/Services/**` | Data service registration and the startup migration | [App bootstrap](composition.md#app-bootstrap-di-and-messaging) |

### Tests

| Path | What lives here | Read first |
|---|---|---|
| `Pointframe.Tests/**` | xUnit and Moq unit tests; run in CI | Look up the class under test |
| `Pointframe.Tests/Mcp/**`, `Pointframe.Tests/Engine/**`, `Pointframe.Tests/Cli/**`, `Pointframe.Tests/DocsSync/**` | Engine, CLI, and MCP tests, and docs-sync tests that fail when docs drift from code | [Tests, files, and lessons](features/cli-mcp.md#tests-files-and-lessons), [Docs-sync tests](features/cli-mcp.md#docs-sync-tests), [MCP command list](features/cli-mcp.md#the-mcp-command-list-resource-must-name-every-registered-tool) |
| `Pointframe.Tests/Services/Handlers/**` | Annotation shape handler tests | [Annotation engine](features/annotation.md#annotation-engine) |
| `Pointframe.AutomationTests/**` | FlaUI smoke tests against the real app and MCP desktop observation/input tests; required local selection is driven by `scripts/desktop-tests.ps1` | [Desktop-testing driver and policy](features/cli-mcp.md#desktop-testing-driver-and-policy), [Local gates and merging](tooling.md#local-gates-and-merging) |
| `Pointframe.AutomationTests/Installer/**` | Installer smoke tests | [Installer file list](composition.md#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list), [D-004](decisions.md#d-004-native-libraries-ship-loose-and-the-installer-packages-them) |
| `Pointframe.AutomationTests/Support/AutomationIds.cs`, `Pointframe.AutomationTests/Smoke/*AnnotationTool*` | Toolbar automation ids and per-tool smoke coverage | [Add an annotation tool](features/annotation.md#add-an-annotation-tool) |
