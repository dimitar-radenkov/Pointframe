# Updates and About

Checking for, downloading, and installing new releases, and the About window.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-32 | Update check and install | Tray "Check for Updates"; periodic check | `Pointframe/Services/Update/AutoUpdateService.cs`, `Pointframe/Services/Update/GitHubUpdateService.cs`, `Pointframe/Services/Update/UpdateDownloadWindowService.cs` | `update_available`, `update_check_manual`, `update_confirmed`, `update_dismissed` | `Pointframe.Tests/Services/AutoUpdateServiceTests.cs`, `Pointframe.Tests/Services/GitHubUpdateServiceTests.cs`, `Pointframe.Tests/ViewModels/UpdateDownloadViewModelTests.cs` | [Update flow](#update-flow) |
| F-33 | About window | Tray "About" | `Pointframe/ViewModels/AboutViewModel.cs`, `Pointframe/Views/AboutWindow.xaml.cs` | `about_opened`, `about_closed`, `about_url_opened` | `Pointframe.Tests/ViewModels/AboutViewModelTests.cs` | [Update flow](#update-flow) |

## Update flow

**Responsibility.** Find newer releases on GitHub, tell the user, download the installer, and hand off to it.

**Flow.**

1. `AutoUpdateService` is a singleton registered three ways: as itself, as `IAutoUpdateService`, and as an `IHostedService`, so the host starts it and the tray can call it. It polls `IUpdateService` on the `UpdateCheckInterval` from settings (`EveryDay`, `Every2Days`, `Every3Days`, `Never`).
2. `GitHubUpdateService.CheckForUpdates` compares the latest release with the running version from `IAppVersionService` (Nerdbank.GitVersioning) and returns an `UpdateCheckResult`.
3. A newer version publishes `UpdateAvailableMessage`; the tray and `AboutViewModel` surface it. `AboutViewModel` can also trigger a manual check.
4. `IAutoUpdateService.ConfirmAndInstall(result)` opens `UpdateDownloadWindow` through `IUpdateDownloadService` (`UpdateDownloadWindowService`), the testable seam. `UpdateDownloadViewModel` streams the installer with a shared `HttpClient` and launches it through `IProcessService`.

**Invariants.**

- Window services ignore UI events that arrive after their window closed; late progress callbacks after close are a crash source.
- The installer asset name and the `v<version>` tag are produced by the CD workflow; the updater's expectations and the workflow change together. See [CI, CD, and versioning](../knowledge-base.md#ci-cd-and-versioning).

**Tests.** `Pointframe.Tests/Services/AutoUpdateServiceTests.cs`, `Pointframe.Tests/Services/GitHubUpdateServiceTests.cs`, `Pointframe.Tests/Services/UpdateDownloadWindowServiceTests.cs`, `Pointframe.Tests/Services/AppVersionServiceTests.cs`, `Pointframe.Tests/ViewModels/UpdateDownloadViewModelTests.cs`, `Pointframe.Tests/ViewModels/AboutViewModelTests.cs`.

**Files.** `Pointframe/Services/Update/IUpdateService.cs`, `Pointframe/Services/Update/GitHubUpdateService.cs`, `Pointframe/Services/Update/IAutoUpdateService.cs`, `Pointframe/Services/Update/AutoUpdateService.cs`, `Pointframe/Services/Update/IUpdateDownloadService.cs`, `Pointframe/Services/Update/UpdateDownloadWindowService.cs`, `Pointframe/ViewModels/UpdateDownloadViewModel.cs`, `Pointframe/Views/UpdateDownloadWindow.xaml.cs`, `Pointframe/ViewModels/AboutViewModel.cs`, `Pointframe/Models/UpdateCheckResult.cs`, `Pointframe/Models/UpdateCheckInterval.cs`, `Pointframe/Services/Infrastructure/AppVersionService.cs`, `Pointframe/Services/Messaging/UpdateAvailableMessage.cs`.

**Lessons.**

- Lesson: Modal window services must guard against late UI events after close
