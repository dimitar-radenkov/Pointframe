# Tray menu

The tray-first shell: recent captures and recordings, and folder shortcuts. Startup, DI, and hotkey wiring are cross-cutting and live in [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging).

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-27 | Recent captures and recordings menus | Tray submenus | `Pointframe/Services/Infrastructure/TrayIconManager.cs` | — | `Pointframe.Tests/Services/TrayIconManagerTests.cs` | [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |
| F-28 | Open snips, videos, and logs folders | Tray "Open Folders" | `Pointframe/Services/Infrastructure/TrayIconManager.cs` | — | — | [Runtime paths and external binaries](../knowledge-base.md#runtime-paths-and-external-binaries) |
