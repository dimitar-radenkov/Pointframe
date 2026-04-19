# Changelog

## [3.2] – 2026-03-17

### Features
- **Callout annotation tool** – new speech-bubble shape with a triangular tail; drag to size the bubble, then type text inside it. Added 💬 toolbar button.
- **Number badge redesign** – numbered labels are now circular filled badges (disc + white numeral) instead of plain `(n)` text, making them more visible on any background.

### Fixes
- **Release build crash** – corrected a `size` → `Size` casing bug in `AnnotationCanvasRenderer` that caused a build failure in Release configuration.
- **Flaky CI test** – replaced `Task.Delay(50)` timing guard in `AutoUpdateServiceTests` with a `TaskCompletionSource`-based wait, eliminating a race condition on loaded CI runners.

### Code quality
- Replaced `== null` / `!= null` comparisons with `is null` / `is not null` across `AnnotationCanvasRenderer`, `OverlayWindow.xaml.cs`, `ThemeService`, and `WindowsOcrService`.
- Fixed number-counter undo/redo recount to use `FrameworkElement` instead of `TextBlock` (required after the badge redesign).

---

## [2.2.9] – 2026-03-05

### Features
- **Update download progress window** – new `UpdateDownloadWindow` and `UpdateDownloadViewModel` with real-time progress tracking during update installation.
- **About window** – new `AboutWindow` with app version info and a changelog link, backed by `AboutViewModel` and `IAppVersionService` following MVVM/DI patterns.
- **Automated versioning** – adopted Nerdbank.GitVersioning (`nbgv`) to replace manual version management; version is now derived from Git history and tags via `version.json` and `dotnet-tools.json`.

### Improvements
- **Service abstractions** – extracted `IMessageBoxService`, `IProcessService`, and `IUpdateDownloadWindowService` with concrete implementations (`MessageBoxService`, `ProcessService`, `UpdateDownloadWindowService`), removing direct `Process` and `MessageBox` calls from ViewModels.
- **`AppVersionService` refactor** – encapsulated all version-retrieval logic (including single-file publish location handling) into a dedicated `AppVersionService`.
- **`UpdateDownloadViewModel` refactor** – injected `HttpClient` via DI and simplified download orchestration for better readability and testability. `ConfigureAwait(false)` applied to async download/install calls.
- **`HttpClient` registration** – `HttpClient` is now registered in the DI container (`App.xaml.cs`) and injected into `UpdateDownloadViewModel` instead of being created ad-hoc.

### CI / CD
- **Installer code-signing** – added a step in `cd.yml` to sign the generated `SnippingTool-Setup.exe` using a certificate stored in GitHub secrets; signing condition syntax fixed to correctly detect the secret.
- **CD trigger** – CD pipeline now triggers on a successful CI `workflow_run` event instead of a direct push, improving coordination between pipelines.
- **Artifact actions** – pinned `actions/checkout` and `actions/upload-artifact` to `v4`; added `fetch-depth: 0` in both CI and CD for full Git history (required by `nbgv`).
- **Release permissions** – granted `contents:write` permission to the CD job to allow automated GitHub release creation.
- **Auto-release on every master push** – simplified `cd.yml` tag/release logic to auto-release using the `nbgv`-computed version tag on every master push.
- **Coverage path** – specified the Cobertura coverage file path in `ci.yml` for correct coverage reporting.

### Installer
- Added `CloseApplications` directive to `Pointframe.iss` so the installer prompts to close any running instance before upgrading.

### Tests
- Added `AppVersionServiceTests` (33 lines), `AboutViewModelTests` (54 lines), and `UpdateDownloadViewModelTests` (237 lines).
- Refactored existing tests to align with the new service-abstraction interfaces.
- Added `Moq` package reference to `SnippingTool.Tests.csproj`.

---

## Earlier Releases

See the [GitHub Releases page](https://github.com/dimitar-radenkov/Pointframe/releases) for versions prior to 2.2.
