# Telemetry

Opt-in usage and diagnostic telemetry: the event catalog, lifecycle events, and analysis queries.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-34 | App lifecycle and usage telemetry | Startup, exit, heartbeat, unhandled errors | `Pointframe/App.xaml.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs` | `app_started`, `startup_completed`, `app_closed`, `app_heartbeat`, `unhandled_exception`, `hotkey_status` | `Pointframe.Tests/AppTests.cs`, `Pointframe.Tests/Services/TelemetryServiceTests.cs`, `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs` | [Telemetry](#telemetry-pipeline), [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |
| F-44 | CLI and MCP operation telemetry: one anonymous event per MCP tool call or CLI command, first-run notice, opt-out | Every CLI command and MCP `tools/call` in official release builds | `Pointframe.Telemetry/OperationTelemetryFactory.cs`, `Pointframe.Telemetry/OperationTelemetry.cs`, `Pointframe.Mcp/McpTelemetryFilter.cs`, `Pointframe.Cli/Application/CliApplication.cs` | — | `Pointframe.Tests/Telemetry/OperationTelemetryTests.cs`, `Pointframe.Tests/Mcp/McpTelemetryTests.cs`, `Pointframe.Tests/Cli/CliTelemetryTests.cs` | [CLI and MCP operation telemetry](#cli-and-mcp-operation-telemetry) |

Recording failures emit diagnostic `recording_failed` with required `phase` (`start`, `capture`, `encode`, `finalize`, `stop`), `reason`, and up to three distinct `inner_types`; `ffmpeg_exit_code` is optional. The event carries no exception messages or paths. `scripts/usage-report.ps1` groups these by version, phase, and reason.

The first-run welcome is documented under [F-43](tray.md#first-run-welcome). Its catalog properties are `onboarding_action.action` (`capture` or `dismiss`) and `hotkey_status.status` (`installed` or `failed`); `onboarding_shown` has no properties. The capture action's regular `snip_started` event uses `source=onboarding`.

## CLI and MCP operation telemetry

**Responsibility.** Emit the `agent_operation` event, which is deliberately outside `TelemetryEvents` and the desktop catalog. Count which MCP tools and CLI commands are used and how they end, without content or identity. The owner decision: allowlist only name, outcome, duration bucket, host, and version; no content, arguments, raw errors, or persistent identifiers; a visible first-run notice and a reliable opt-out. It measures operations, not unique users or retention.

**Key types.**

| Type | Role |
|---|---|
| `Pointframe.Telemetry` project | Shared by `Pointframe.Cli` and `Pointframe.Mcp`; references only Azure Monitor exporter and OpenTelemetry, never WPF |
| `TelemetryAllowlist` | Fixed command and tool names (unknown becomes `other`), outcome names, duration buckets (`lt_1s`, `1_5s`, `5_30s`, `gt_30s`), and the MCP client mapping |
| `TelemetryPreferences` | Opt-out from `POINTFRAME_TELEMETRY_OPTOUT`, `DO_NOT_TRACK`, or `agent-telemetry.json` (`{"optOut": true}`) in the Pointframe data directory; also the notice-shown marker file |
| `OperationTelemetryFactory` | Returns a no-op when the connection string is empty or the user opted out; otherwise shows the notice once and returns `OperationTelemetry` |
| `OperationTelemetry` | Emits `agent_operation` on a pool thread through the same OpenTelemetry log pipeline as the desktop app; `Flush` waits at most two seconds |
| `McpTelemetryFilter` | The MCP call-tool filter: measures every `tools/call`, maps `GlobalHotkeyNotApproved` and `ProfileNotFound` results to `denied` |

**Invariants.**

- The event carries exactly `name`, `outcome`, `duration_bucket`, `host`, `version`, and for MCP `client`. The README and `website/privacy.html` list every key declared in `OperationTelemetry.cs`; `scripts/check-agent-discovery.ps1` fails when one is missing.
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

**Configuration.** `Pointframe/appsettings.json` ships `ApplicationInsights:ConnectionString` empty, so source builds and CI send nothing. The CD workflow injects the real string and verifies the injection before publishing. A developer can set a personal string in `appsettings.Local.json`, which is loaded after `appsettings.json`. See [CI, CD, and versioning](../knowledge-base.md#ci-cd-and-versioning).

**Invariants.**

- Define every event in the catalog and emit it by its constant; the catalog is the single source for names and required properties.
- The product logger uses an empty OpenTelemetry resource with only `service.name=Pointframe` and `service.instance.id=desktop`; do not add environment, host, process, or OS detectors. `OTEL_*` environment overrides are ignored for this channel, Live Metrics is disabled, and the schema version is `2`.
- Before export, drop event properties not declared by the event definition. Keep schema warnings in the local log only; they report event and property names, never property values. The Azure exporter otherwise uses the machine name as `ai.cloud.roleInstance` when no resource is set explicitly.
- The outbound payload includes the event name, declared event properties, random install ID when available, per-run session ID, app version, `telemetry_channel`, and `telemetry_schema_version`. Azure derives country and city from the connection IP; the app does not send a machine name, user/domain name, file path, or automatic host details.
- The README section `### What is collected` lists every catalog event with its required properties and nothing else. `TelemetryDocumentationTests` parses that table and fails the build on drift, so an event change and its README row ship in the same change.
- Properties are labels (tool, capture type, URL host), never content. Do not add file paths, OCR text, or image data.

**Analysis.** Kusto queries and the workbook template: `docs/appinsights-feature-usage-queries.kql` and `docs/appinsights-pointframe-workbook.all-in-one.template.json`.
**Measuring usage.** `pwsh scripts/usage-report.ps1` (needs `az login`; `-Json`, `-OutFile`, `-From`/`-To`, `-SandboxView`) queries the Application Insights data API read-only and prints totals, 1/7/30-day active installs, weekly activation cohorts, OS and version splits, the never-activated funnel, retention, onboarding, failures, and website sessions. An install is `customDimensions.install_id`; app events are `customEvents` not starting with `website`. Activated means the install ever sent `capture_completed` or `recording_completed`; many never-activated installs are real users who tried it once, so raw numbers are always shown and the sandbox-like view (lifetime under 2 minutes, startup events only) is an optional heuristic. The owner machine is excluded with `-ExcludeRoleInstance` and `-ExcludeInstallId`; prefer install ids, because machine names are leaving telemetry. Cohorts younger than 7 days after their last day are labelled immature. `-SelfTest` checks the shaping offline against `scripts/tests/usage-report/`; KQL syntax is only proven by a live run. Do not use `first` or `real` as KQL names, and query the data API, not ARM `/api/query`.

**Tests.** `Pointframe.Tests/Services/TelemetryServiceTests.cs` captures the Azure exporter HTTP payload, including environment override checks; `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs`, `Pointframe.Tests/Services/TelemetryDocumentationTests.cs`, `Pointframe.Tests/Services/ActivationTelemetryServiceTests.cs`.

**Files.** `Pointframe/Services/Infrastructure/ITelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/NullTelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs`, `Pointframe/Services/Infrastructure/ActivationTelemetryService.cs`, `Pointframe/appsettings.json`, `scripts/usage-report.ps1`.

- Lesson: Azure Monitor's exporter sends the machine name as the role instance unless the resource is set explicitly
