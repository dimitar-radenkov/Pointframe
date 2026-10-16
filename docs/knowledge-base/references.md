# References

Reference facts shared by every area: where the app keeps files, which binaries it needs, and the configuration keys that override them.

Part of the [Pointframe knowledge base](knowledge-base.md).

## Runtime paths and external binaries

**Per-user files.** Root: `%LOCALAPPDATA%\Pointframe` (`AppPaths.LocalAppDataDirectory`).

| File | Purpose | Owner |
|---|---|---|
| `logs\pointframe-<date>.log` | Serilog rolling log; `Logging:RetainedFileCountLimit` in `appsettings.json` caps retained files | `AppPaths.RollingLogPath` |
| `settings.json` | User settings | `UserSettingsService` |
| `pointframe.db` | SQLite database for the capture text cache | `AppPaths.PointframeDatabasePath`, `Pointframe.Data` |

Screenshots and recordings go to the folder chosen in settings.

**ffmpeg.** `FfmpegResolver.Resolve()` order:

1. `AppContext` data key `SnippingTool.FfmpegPath` (set by tests or a host).
2. `ffmpeg.exe` next to the application binary. The installer's optional "ffmpeg" component downloads a BtbN GPL win64 build from the `n9.0` release branch to this location; the CD workflow bundles the same build into the MCP package and fails the release if it rejects the microphone recording arguments. Never point either at `master-latest`: its options change without notice (see [Recording pipeline](features/recording.md#recording-pipeline)).
3. `Assets\ffmpeg\ffmpeg.exe` under the binary folder.
4. Bare `ffmpeg.exe`, resolved through `PATH`.

`ResolveRequired(purpose)` throws `FileNotFoundException` with a user-facing message when an explicit location is missing. MP4 recording, GIF export, trim, and video watermark all need it.

**Configuration and overrides.**

| Key | Where | Effect |
|---|---|---|
| `ApplicationInsights:ConnectionString` | `appsettings.json` | Empty in source; CD injects the real value |
| `Logging:RetainedFileCountLimit` | `appsettings.json` | Rolling log retention |
| Any key above | `appsettings.Local.json` | Optional local override, loaded after `appsettings.json` and copied to output only if present |
| `SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH` | environment | Redirects `settings.json` for automation tests |
| `SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY` | environment | Redirects app data, database, logs, and the default screenshot import root for isolated desktop tests |
| Automation launch options | command line, parsed by `AutomationLaunchOptions.Parse` | Drives `Pointframe.AutomationTests` scenarios |

Identifiers still prefixed `SnippingTool` are pre-rename names kept for compatibility. Renaming one touches the automation project and the installer together.

**Files.** `Pointframe/Services/Infrastructure/AppPaths.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/Services/Recording/FfmpegResolver.cs`, `Pointframe/appsettings.json`, `Pointframe/App.xaml.cs`, `Pointframe/Automation/AutomationLaunchOptions.cs`, `installer/Pointframe.iss`.
