# Tray menu

The tray-first shell: recent captures and recordings, and folder shortcuts. Startup, DI, and hotkey wiring are cross-cutting and live in [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging).

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-27 | Recent captures and recordings menus | Tray submenus | `Pointframe/Services/Infrastructure/TrayIconManager.cs` | — | `Pointframe.Tests/Services/TrayIconManagerTests.cs` | [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |
| F-28 | Open snips, videos, and logs folders | Tray "Open Folders" | `Pointframe/Services/Infrastructure/TrayIconManager.cs` | — | — | [Runtime paths and external binaries](../knowledge-base.md#runtime-paths-and-external-binaries) |
| F-43 | First-run welcome | First eligible ordinary app launch, including login startup | `Pointframe/Services/Infrastructure/WelcomeEligibilityPolicy.cs`, `Pointframe/ViewModels/WelcomeViewModel.cs`, `Pointframe/Views/WelcomeWindow.xaml` | `onboarding_shown`, `onboarding_action`, `hotkey_status`; region snip uses `source=onboarding` | `Pointframe.Tests/Services/WelcomeEligibilityPolicyTests.cs`, `Pointframe.Tests/ViewModels/WelcomeViewModelTests.cs`, `Pointframe.AutomationTests/Smoke/WelcomeWindowSmokeTests.cs` | [User settings](settings.md#user-settings), [Capture overlay and selection](capture.md#capture-overlay-and-selection), [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |

## First-run welcome

**Eligibility.** Show once when first-capture completion and `WelcomeShown` are false, the catalog has no capture rows, and none of the configured screenshot import roots contains a supported image. Catalog and file lookup failures defer the welcome. Automation launches never show it. The filesystem check covers the startup reconciliation race, which runs asynchronously.

**Flow.** The welcome reads the current region shortcut from settings. If the low-level keyboard hook did not install, it points the user to the tray menu. Capture persists `WelcomeShown`, closes the welcome, yields the dispatcher, then starts the regular region snip with source `onboarding`. Canceling that snip does not mark first-capture activation. Dismiss and window close persist the same one-time flag.

**Tests.** Eligibility covers fresh installs, catalog and legacy-file evidence, lookup failures, automation, and persisted flags. ViewModel tests cover command telemetry and the unavailable-hook message. Desktop automation launches the Release app without automation arguments, verifies welcome-to-selection transition and cancellation, then relaunches with `WelcomeShown` set.

**Files.** `Pointframe/Services/Infrastructure/WelcomeEligibilityPolicy.cs`, `Pointframe/ViewModels/WelcomeViewModel.cs`, `Pointframe/Views/WelcomeWindow.xaml`, `Pointframe/Views/WelcomeWindow.xaml.cs`, `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/GlobalHotkeyService.cs`, `Pointframe.Engine/Library/CaptureCatalogContracts.cs`, `Pointframe.Engine/Library/CaptureCatalogService.cs`, `Pointframe.Tests/Services/WelcomeEligibilityPolicyTests.cs`, `Pointframe.Tests/ViewModels/WelcomeViewModelTests.cs`, `Pointframe.AutomationTests/Smoke/WelcomeWindowSmokeTests.cs`.
