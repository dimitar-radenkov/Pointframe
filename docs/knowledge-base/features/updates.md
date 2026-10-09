# Updates and About

Checking for, downloading, and installing new releases, and the About window.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-32 | Update check and install | Tray "Check for Updates" or "Install update"; periodic check; update card; Settings "Install" | `Pointframe/Services/Update/AutoUpdateService.cs`, `Pointframe/Services/Update/GitHubUpdateService.cs`, `Pointframe/Services/Update/UpdateDownloadWindowService.cs`, `Pointframe/Views/UpdateCardWindow.xaml.cs` | `update_available`, `update_check_manual`, `update_confirmed`, `update_dismissed`, `update_card_shown`, `update_card_dismissed`, `update_download_failed`, `update_installer_launched`, `update_applied` | `Pointframe.Tests/Services/AutoUpdateServiceTests.cs`, `Pointframe.Tests/Services/GitHubUpdateServiceTests.cs`, `Pointframe.Tests/ViewModels/UpdateDownloadViewModelTests.cs`, `Pointframe.Tests/Services/UpdateOfferPolicyTests.cs` | [Update flow](#update-flow) |
| F-33 | About window | Tray "About" | `Pointframe/ViewModels/AboutViewModel.cs`, `Pointframe/Views/AboutWindow.xaml.cs` | `about_opened`, `about_closed`, `about_url_opened` | `Pointframe.Tests/ViewModels/AboutViewModelTests.cs` | [Update flow](#update-flow) |

## Update flow

**Responsibility.** Find newer releases on GitHub, tell the user, download the installer, and hand off to it.

**Flow.**

1. `AutoUpdateService` is a singleton registered three ways: as itself, as `IAutoUpdateService`, and as an `IHostedService`, so the host starts it and the tray can call it. It polls `IUpdateService` on the `AutoUpdateCheckInterval` from settings (default `EveryTwoHours`; `Never` disables automatic checks). The startup check runs only when `LastAutoUpdateCheckUtc` is older than that interval.
2. `GitHubUpdateService.CheckForUpdates` compares the latest release with the running version from `IAppVersionService` (Nerdbank.GitVersioning) and returns an `UpdateCheckResult`.
3. Every check publishes `UpdateAvailableMessage`, also when no update exists. `App` stores the result in the singleton `IUpdateStateService`, which holds the known update until a check replaces it. The tray item ("Install update vX.Y.Z"), the Settings App section ("Update available ... Install"), and the update card all read it. Balloon click routing no longer owns the update, so a transcript balloon cannot drop it.
4. The update card (`UpdateCardWindow`: non-modal, `ShowActivated=False`, topmost, bottom-right of the primary work area) is the visible offer. `App.TryShowUpdateCard` shows it only when `UpdateOfferPolicy` says an offer is due: two minutes after startup or after a capture completes, and at most once per 72 hours through the persisted `LastUpdateOfferUtc`. While an `OverlayWindow` (capture and editor), `ScrollingCaptureProgressWindow`, or `RecordingOverlayWindow` is visible, it retries every 30 seconds instead. There is no update balloon any more: Windows shows a tray balloon as a toast in the same bottom-right corner and keeps it there while the user is away, so it covered the card (seen on the desktop, 2026-10-09). "Update now" calls `InstallWithoutConfirmation`, because the click is the consent; "Later" or closing the card sends `update_card_dismissed`.
5. `IAutoUpdateService.ConfirmAndInstall(result)` (tray, Settings) asks first, then opens `UpdateDownloadWindow` through `IUpdateDownloadService` (`UpdateDownloadWindowService`), the testable seam. `UpdateDownloadViewModel` streams the installer with a shared `HttpClient` and launches it through `IProcessService`.
6. Update telemetry carries `from_version` (the last run version) and `target_version`, never the common `version` property. `App.TrackAppliedUpdate` records `LastRunVersion` at each start and sends `update_applied` once when the running version is newer. That event, not `update_installer_launched`, is the success signal.

**Invariants.**

- Window services ignore UI events that arrive after their window closed; late progress callbacks after close are a crash source.
- `LastUpdateOfferUtc` and `LastRunVersion` are app-owned state: Settings Save copies them from the current settings and Restore defaults keeps them, like `InstallId`.
- The installer asset name and the `v<version>` tag are produced by the CD workflow; the updater's expectations and the workflow change together. See [CI, CD, and versioning](../knowledge-base.md#ci-cd-and-versioning).

**Tests.** `Pointframe.Tests/Services/AutoUpdateServiceTests.cs`, `Pointframe.Tests/Services/GitHubUpdateServiceTests.cs`, `Pointframe.Tests/Services/UpdateDownloadWindowServiceTests.cs`, `Pointframe.Tests/Services/AppVersionServiceTests.cs`, `Pointframe.Tests/ViewModels/UpdateDownloadViewModelTests.cs`, `Pointframe.Tests/ViewModels/AboutViewModelTests.cs`, `Pointframe.Tests/Services/UpdateOfferPolicyTests.cs`, `Pointframe.Tests/Services/TrayIconManagerTests.cs`, `Pointframe.Tests/AppTests.cs`.

**Files.** `Pointframe/Services/Update/IUpdateService.cs`, `Pointframe/Services/Update/GitHubUpdateService.cs`, `Pointframe/Services/Update/IAutoUpdateService.cs`, `Pointframe/Services/Update/AutoUpdateService.cs`, `Pointframe/Services/Update/IUpdateDownloadService.cs`, `Pointframe/Services/Update/UpdateDownloadWindowService.cs`, `Pointframe/ViewModels/UpdateDownloadViewModel.cs`, `Pointframe/Views/UpdateDownloadWindow.xaml.cs`, `Pointframe/ViewModels/AboutViewModel.cs`, `Pointframe/Models/UpdateCheckResult.cs`, `Pointframe/Models/UpdateCheckInterval.cs`, `Pointframe/Services/Infrastructure/AppVersionService.cs`, `Pointframe/Services/Messaging/UpdateAvailableMessage.cs`, `Pointframe/Services/Update/IUpdateStateService.cs`, `Pointframe/Services/Update/UpdateStateService.cs`, `Pointframe/Services/Update/UpdateOfferPolicy.cs`, `Pointframe/Views/UpdateCardWindow.xaml.cs`.

**Lessons.**

- Lesson: Modal window services must guard against late UI events after close
