# Telemetry

Opt-in usage and diagnostic telemetry: the event catalog, lifecycle events, and analysis queries.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-34 | App lifecycle and usage telemetry | Startup, exit, heartbeat, unhandled errors | `Pointframe/App.xaml.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs` | `app_started`, `startup_completed`, `app_closed`, `app_heartbeat`, `unhandled_exception`, `hotkey_status` | `Pointframe.Tests/AppTests.cs`, `Pointframe.Tests/Services/TelemetryServiceTests.cs`, `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs` | [Telemetry](#telemetry-pipeline), [App bootstrap](../composition.md#app-bootstrap-di-and-messaging) |
| F-44 | CLI and MCP operation telemetry: one anonymous event per MCP tool call or CLI command, first-run notice, opt-out | Every CLI command and MCP `tools/call` in official release builds | `Pointframe.Telemetry/OperationTelemetryFactory.cs`, `Pointframe.Telemetry/OperationTelemetry.cs`, `Pointframe.Mcp/McpTelemetryFilter.cs`, `Pointframe.Cli/Application/CliApplication.cs` | — | `Pointframe.Tests/Telemetry/OperationTelemetryTests.cs`, `Pointframe.Tests/Mcp/McpTelemetryTests.cs`, `Pointframe.Tests/Cli/CliTelemetryTests.cs`, `Pointframe.Tests/Cli/VerificationOutcomeTests.cs`, `Pointframe.Tests/Mcp/RestartWaitTelemetryTests.cs` | [CLI and MCP operation telemetry](#cli-and-mcp-operation-telemetry) |

Recording failures emit diagnostic `recording_failed` with required `phase` (`start`, `capture`, `encode`, `finalize`, `stop`), `reason`, and up to three distinct `inner_types`; `ffmpeg_exit_code` is optional. The event carries no exception messages or paths. `scripts/usage-report.ps1` groups these by version, phase, and reason.

The first-run welcome is documented under [F-43](tray.md#first-run-welcome). Its catalog properties are `onboarding_action.action` (`capture` or `dismiss`) and `hotkey_status.status` (`installed` or `failed`); `onboarding_shown` has no properties. The capture action's regular `snip_started` event uses `source=onboarding`.

## CLI and MCP operation telemetry

**Responsibility.** Emit the `agent_operation` event, which is deliberately outside `TelemetryEvents` and the desktop catalog. Count which MCP tools and CLI commands are used, how they end, and by how many installs, without content or personal identity. Allowlist only name, outcome, duration bucket, host, version, and a random install ID ([D-015](#d-015-agent-telemetry-carries-a-random-install-id)); no content, arguments, raw errors, user or machine names; a visible first-run notice and a reliable opt-out.

**Key types.**

| Type | Role |
|---|---|
| `Pointframe.Telemetry` project | Shared by `Pointframe.Cli` and `Pointframe.Mcp`; references only Azure Monitor exporter and OpenTelemetry, never WPF |
| `TelemetryAllowlist` | Fixed command and tool names (unknown becomes `other`), outcome names, duration buckets (`lt_1s`, `1_5s`, `5_30s`, `gt_30s`), and the MCP client mapping |
| `TelemetryPreferences` | Opt-out from `POINTFRAME_TELEMETRY_OPTOUT`, `DO_NOT_TRACK`, or `agent-telemetry.json` (`{"optOut": true}`) in the Pointframe data directory; the notice-shown marker file; the `agent-install-id` file (created on the first tracked run, deleted on opt-out) |
| `OperationTelemetryFactory` | Returns a no-op when the connection string is empty or the user opted out; otherwise shows the notice once and returns `OperationTelemetry` |
| `OperationTelemetry` | Emits `agent_operation` on a pool thread through the same OpenTelemetry log pipeline as the desktop app; `Flush` waits at most two seconds |
| `McpTelemetryFilter` | The MCP call-tool filter: measures every `tools/call`, maps `GlobalHotkeyNotApproved` and `ProfileNotFound` results to `denied` and the typed validation codes in `InvalidRequestCodes` (and protocol InvalidParams) to `invalid_request`; classifies other typed failures (top-level `error` or `success: false` in the structured content, `IsError` unset) as `error`, but a desktop check that ran and failed or was inconclusive, or a matched negative control (`NegativeControlMatched`), stays `success`. A call carrying the runner's restart-poll marker (`TelemetryHints`, request `_meta`) is not tracked when it returns `TargetStillRunning` |
| `InvalidRequestCodes`, `TelemetryHints` | The shared set of typed input-validation codes counted as `invalid_request`; the local `_meta` key for the scenario runner's restart poll, never exported |

**Invariants.**

- The event carries exactly `name`, `outcome`, `duration_bucket`, `host`, `version`, `agent_install_id` when the ID file is usable, and for MCP `client`. The key is not `install_id`, because `scripts/usage-report.ps1` counts that key as a desktop install. A change to what the notice says bumps `TelemetryPreferences.NoticeFileName`, so every user sees the new notice once. The README and `website/privacy.html` list every key declared in `OperationTelemetry.cs`; `scripts/check-agent-discovery.ps1` fails when one is missing.
- `outcome` is one of `success`, `error`, `cancelled`, `denied`, `verification_failed`, `invalid_request` ([D-017](#d-017-agent-outcomes-separate-failing-verdicts-and-invalid-input)). Classify from typed provenance, never from the exit code alone: `verification_failed` only when the checks ran against the app and the verdict is negative or not fresh; for scenarios that means every failed step has a code on the allowlist `DesktopScenarioRunner.VerificationResultCodes` (`ElementNotFound` and `NoVisibleWindow` only when the last observation succeeded), so an unknown code is an `error`; `invalid_request` only for recognized invalid input; infrastructure failures (gate not started, provider or I/O exceptions the scenario runner swallows, failed report read or cleanup, a session that did not start) stay `error` even when the verdict is fail, and so does anything unresolved. Exit codes and MCP results never change for telemetry.
- One scenario restart step counts as one `desktop_restart_app` call from the release that ships [D-017](#d-017-agent-outcomes-separate-failing-verdicts-and-invalid-input): the runner marks wait polls, and only a marked call refused with `TargetStillRunning` is dropped. When the last poll ends after the deadline, the runner makes one final unmarked (counted) attempt, so a failed wait emits exactly one error event; and a cancelled wait emits none (the CLI `verify` event records `cancelled`). Direct agent calls are always counted. Older versions count every poll.
- The resource is empty plus `service.name` (`Pointframe.Cli` or `Pointframe.Mcp`) and a constant `service.instance.id` (`cli` or `mcp`). Offline storage is disabled so nothing outlives an opt-out.
- The connection string lives in the embedded `Pointframe.Telemetry/telemetry.json`, empty in source and injected by `cd.yml` before the packages build. `POINTFRAME_TELEMETRY_CONNECTION_STRING` overrides it for tests and contributors.
- Telemetry never blocks or fails a tool call: `Track` queues work, every telemetry path catches its own exceptions, and the CLI and MCP host flush with a short timeout on exit.
- Standard output carries only protocol or command output. The notice goes to standard error, and once into the MCP server instructions. `help` and `version` are not tracked.
- The desktop app keeps its own pipeline and event catalog; this one is independent of `TelemetryEvents`.

**Tests.** `OperationTelemetryTests` captures the exported properties with a fake exporter, proves zero requests when opted out against a loopback listener, and inspects the real exporter payload. `McpTelemetryTests` hosts the real executable with telemetry on and asserts stdout is JSON-RPC only; it also holds the tool annotation contract (every tool has a title and explicit `readOnlyHint` and `destructiveHint`) and the allowlist-versus-advertised-tools check.

**Files.** `Pointframe.Telemetry/**`, `Pointframe.Mcp/McpTelemetryFilter.cs`, `website/privacy.html`.

- Lesson: A stdio MCP server must send nothing to standard output except protocol messages
- Lesson: A best-effort flush timeout makes export assertions flaky on CI

## Telemetry pipeline

**Responsibility.** Record product usage and diagnostics in Application Insights without collecting content, and keep the public privacy statement and the code in lockstep.

**Key types.**

| Type | Role |
|---|---|
| `ITelemetryService` | `TrackEvent(name, properties)`, `TrackException(...)`, `Flush()` |
| `TelemetryService` | Application Insights client; sends nothing when the connection string is empty |
| `NullTelemetryService` | Explicit no-op for tests and disabled builds |
| `TelemetryEventCatalog` | `TelemetryChannel` (Product, Diagnostic), `TelemetryPropertyKeys`, `TelemetryEvents` name constants, and `All` definitions with required properties |
| `TelemetryHeartbeatService` | Hosted service emitting `app_heartbeat` with uptime and session data |
| `ActivationTelemetryService` | First-run and activation funnel events |

**Configuration.** `Pointframe/appsettings.json` ships `ApplicationInsights:ConnectionString` empty, so source builds and CI send nothing. The CD workflow injects the real string and verifies the injection before publishing. A developer can set a personal string in `appsettings.Local.json`, which is loaded after `appsettings.json`. See [Workflows](../tooling.md#workflows).

**Invariants.**

- Define every event in the catalog and emit it by its constant; the catalog is the single source for names and required properties.
- The product logger uses an empty OpenTelemetry resource with only `service.name=Pointframe` and `service.instance.id=desktop`; do not add environment, host, process, or OS detectors. `OTEL_*` environment overrides are ignored for this channel, Live Metrics is disabled, and the schema version is `2`.
- Before export, drop event properties not declared by the event definition. Keep schema warnings in the local log only; they report event and property names, never property values. The Azure exporter otherwise uses the machine name as `ai.cloud.roleInstance` when no resource is set explicitly.
- The outbound payload includes the event name, declared event properties, random install ID when available, per-run session ID, app version, `telemetry_channel`, and `telemetry_schema_version`. Azure derives country and city from the connection IP; the app does not send a machine name, user/domain name, file path, or automatic host details.
- The `PRIVACY.md` section `### What is collected` lists every catalog event with its required properties and nothing else. `TelemetryDocumentationTests` parses that table and fails the build on drift, so an event change and its `PRIVACY.md` row ship in the same change.
- Properties are labels (tool, capture type, URL host), never content. Do not add file paths, OCR text, or image data.

**Analysis.** Kusto queries and the workbook template: `docs/appinsights-feature-usage-queries.kql` and `docs/appinsights-pointframe-workbook.all-in-one.template.json`.
**Measuring usage.** `pwsh scripts/usage-report.ps1` (needs `az login`; `-Json`, `-OutFile`, `-From`/`-To`, `-SandboxView`) queries the Application Insights data API read-only and prints totals, 1/7/30-day active installs, weekly activation cohorts, OS and version splits, the never-activated funnel, retention, onboarding, failures, and website sessions. An install is `customDimensions.install_id`; app events are `customEvents` not starting with `website`. Activated means the install ever sent `capture_completed` or `recording_completed`; many never-activated installs are real users who tried it once, so raw numbers are always shown and the sandbox-like view (lifetime under 2 minutes, startup events only) is an optional heuristic. Startup events are the ones the app sends without user input, including onboarding_shown, hotkey_status and update_applied; a new automatic startup event must join that list in the KQL, or sandbox installs stop being flagged (from 2026-10-05 the list missed these three and flagged none). The owner machine is excluded with `-ExcludeRoleInstance` and `-ExcludeInstallId`; prefer install ids, because machine names are leaving telemetry. Cohorts younger than 7 days after their last day are labelled immature. Capture events carry no `source`, so onboarding is attributed by session: for each install with a `snip_started` `source=onboarding`, the report looks at the same `session_id` after that snip and counts installs that copied or saved, that only annotated or pinned (no copy/save, so not activated), and that only cancelled. The agent section splits `agent_operation` by op, outcome, and version into internal (an `agent_install_id` listed in the private, gitignored telemetry-internal-agent-ids.json under plan/, or `-InternalAgentIdsFile`), external (provisional: identified and not listed), and unidentified (no `agent_install_id`, versions before [D-015](#d-015-agent-telemetry-carries-a-random-install-id)); it warns when the list is missing and never prints IDs. `-SelfTest` checks the shaping offline against `scripts/tests/usage-report/`; KQL syntax is only proven by a live run. Do not use `first`, `real` or `kind` as KQL names, and query the data API, not ARM `/api/query`.

**Tests.** `Pointframe.Tests/Services/TelemetryServiceTests.cs` captures the Azure exporter HTTP payload, including environment override checks; `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs`, `Pointframe.Tests/Services/TelemetryDocumentationTests.cs`, `Pointframe.Tests/Services/ActivationTelemetryServiceTests.cs`.

**Files.** `Pointframe/Services/Infrastructure/ITelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/NullTelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs`, `Pointframe/Services/Infrastructure/ActivationTelemetryService.cs`, `Pointframe/appsettings.json`, `scripts/usage-report.ps1`.

- Lesson: Azure Monitor's exporter sends the machine name as the role instance unless the resource is set explicitly

## D-015 Agent telemetry carries a random install ID

**Decided.** 2026-10-15, by the owner. Replaces the 2026-10-05 rule of no persistent identifiers in `agent_operation`.

**Context.** Without an identifier, 3,744 agent events in ten days could not be split into users: the owner's trials, CI runners, and outside users looked the same, and a tool that failed 39 of 39 times could be one agent retrying or a bug for everyone.

**Decision.** The CLI and the MCP server send `agent_install_id`, a random 32-hex ID stored in `%LOCALAPPDATA%\Pointframe\agent-install-id` and shared by both hosts on one Windows account. It is not derived from the user, machine, or account. Opting out deletes it. The notice, README, `PRIVACY.md`, privacy page, plugin README, and directory submission texts say so.

**Alternatives rejected.** No identifier: cannot count users or retention. A per-run ID: shows sessions but not users or returns.

**Files.** `Pointframe.Telemetry/TelemetryPreferences.cs`, `Pointframe.Telemetry/OperationTelemetry.cs`, `Pointframe.Telemetry/OperationTelemetryFactory.cs`, `website/privacy.html`.

## D-017 Agent outcomes separate failing verdicts and invalid input

**Decided.** 2026-10-15, by the owner (charter C: finer collected telemetry). Notice v3.

**Context.** Over 30 days almost all agent traffic, and every failure, came from the owner's own trials. `verify` showed 178 of 318 runs as `error` because every non-zero exit was an error, so a run that correctly failed the agent's code looked like a Pointframe failure; one restart wait in the scenario runner produced 39 errors in 10 seconds. No unfixed product bug was found.

**Decision.** Two new `outcome` values, `verification_failed` and `invalid_request`, classified from typed provenance; one event per scenario restart wait; the usage report separates internal, external, and unidentified agent traffic with a private ID list. No new keys, identifiers, content, or retention.

**Alternatives rejected.** Five values (`stale`, `unavailable`, `precondition_failed` too): no decision needs them yet. Classifying by exit code: exit 2 includes I/O failures, and provider exceptions surface as failed scenarios. Collapsing poll bursts in the report: events carry no correlation ID. A server-side held poll: shared state broke under parallel calls and changed the restart deadline. Opting trials out of telemetry: loses QA evidence.

**Files.** `Pointframe.Telemetry/TelemetryContracts.cs`, `Pointframe.Telemetry/InvalidRequestCodes.cs`, `Pointframe.Cli/Verification/VerificationApplication.cs`, `Pointframe.Cli/Verification/DesktopScenarioRunner.cs`, `Pointframe.Mcp/McpTelemetryFilter.cs`, `scripts/usage-report.ps1`.