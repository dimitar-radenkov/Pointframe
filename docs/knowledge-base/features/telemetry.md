# Telemetry

Opt-in usage and diagnostic telemetry: the event catalog, lifecycle events, and analysis queries.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-34 | App lifecycle and usage telemetry | Startup, exit, heartbeat, unhandled errors | `Pointframe/App.xaml.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs` | `app_started`, `startup_completed`, `app_closed`, `app_heartbeat`, `unhandled_exception` | `Pointframe.Tests/AppTests.cs`, `Pointframe.Tests/Services/TelemetryServiceTests.cs`, `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs` | [Telemetry](#telemetry-pipeline), [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |

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
- The README section `### What is collected` lists every catalog event with its required properties and nothing else. `TelemetryDocumentationTests` parses that table and fails the build on drift, so an event change and its README row ship in the same change.
- Properties are labels (tool, capture type, URL host), never content. Do not add file paths, OCR text, or image data.

**Analysis.** Kusto queries and the workbook template: `docs/appinsights-feature-usage-queries.kql` and `docs/appinsights-pointframe-workbook.all-in-one.template.json`.

**Tests.** `Pointframe.Tests/Services/TelemetryServiceTests.cs`, `Pointframe.Tests/Services/TelemetryEventCatalogTests.cs`, `Pointframe.Tests/Services/TelemetryDocumentationTests.cs`, `Pointframe.Tests/Services/ActivationTelemetryServiceTests.cs`.

**Files.** `Pointframe/Services/Infrastructure/ITelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryService.cs`, `Pointframe/Services/Infrastructure/NullTelemetryService.cs`, `Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs`, `Pointframe/Services/Infrastructure/TelemetryHeartbeatService.cs`, `Pointframe/Services/Infrastructure/ActivationTelemetryService.cs`, `Pointframe/appsettings.json`.
