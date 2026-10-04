# Settings

The settings window and what it controls: hotkeys, theme, and per-area preferences. The persistence rule and the add-a-setting recipe are cross-cutting and live in the [knowledge base](../knowledge-base.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files).

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-29 | Settings window | Tray "Settings" | `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe/Views/SettingsWindow.xaml.cs` | `settings_opened`, `settings_saved`, `settings_canceled`, `settings_section_changed`, `settings_section_reset`, `settings_defaults_restored` | `Pointframe.Tests/ViewModels/SettingsViewModelTests.cs`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`, `Pointframe.Tests/SettingsWindowTests.cs`, `Pointframe.AutomationTests/Smoke/SettingsWindowSmokeTests.cs`, `Pointframe.AutomationTests/Smoke/SettingsSectionNavigationTests.cs`, `Pointframe.AutomationTests/Smoke/McpSettingsPersistenceTests.cs` | [User settings](#user-settings), [Settings invariant](../knowledge-base.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files), [Add a user setting](../knowledge-base.md#add-a-user-setting) |
| F-30 | Custom global hotkeys | Settings, Capture and Shortcuts sections | `Pointframe/Services/Infrastructure/GlobalHotkeyService.cs`, `Pointframe/Models/HotkeyBinding.cs` | — | `Pointframe.Tests/Models/HotkeyBindingTests.cs`, `Pointframe.Tests/Services/GlobalHotkeyServiceTests.cs` | [User settings](#user-settings), [App bootstrap](../knowledge-base.md#app-bootstrap-di-and-messaging) |
| F-31 | Light and dark theme | Settings, App section | `Pointframe/Services/Infrastructure/ThemeService.cs` | — | `Pointframe.Tests/Services/ThemeServiceTests.cs` | [User settings](#user-settings) |

## User settings

**Responsibility.** Hold every user preference, persist it as JSON, and expose it through one singleton so every consumer sees the current value.

**Key types.**

| Type | Role |
|---|---|
| `UserSettings` | Mutable POCO with defaults in property initializers; serialized as-is |
| `IUserSettingsService` | `Current` (never cache it), `Save(settings)` replaces the whole object, `Update(Action<UserSettings>)` clones, mutates, and saves |
| `UserSettingsService` | Loads on construction; a missing or corrupt file falls back to defaults with a log line; `Clone` copies every property for `Update` |
| `SettingsViewModel` | One observable property per setting, grouped into `SettingsSection` items for the navigation rail; `Save()` writes them all back |
| `SettingsWindow` | Sectioned window: Capture, Recording, Annotation, Shortcuts, App; automation ids live in `AutomationIds.cs` |
| `HotkeyBinding`, `HotkeyModifiers` | Persisted hotkey model consumed by `GlobalHotkeyService` |
| `IThemeService`, `AppTheme` | Applies the light, dark, or system theme from settings |
| `ScreenshotWatermarkSettings`, `VideoWatermarkSettings` | Separate watermark settings; on load a missing video watermark is cloned from the screenshot one |

**Storage.** `settings.json` in the Pointframe local app data folder. Automation can redirect `SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH` to a file path (existing behavior) or an existing directory, in which case the service uses `settings.json` inside it. Verification's real-app spec uses directory isolation so it does not touch owner settings. See [Runtime paths](../knowledge-base.md#runtime-paths-and-external-binaries).

**Invariants.**

- A new setting touches three files together or it is silently dropped on save. See [Settings persistence](../knowledge-base.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) and [Add a user setting](../knowledge-base.md#add-a-user-setting).
- Restore Defaults writes hidden persisted values directly; a mode flag that defers the write leaves stale values behind.
- Consumers read `IUserSettingsService.Current` at the point of use.

**Tests.** `Pointframe.Tests/Services/UserSettingsServiceTests.cs`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs` (reflection over every `UserSettings` property through save, load, `Update`, and `SettingsViewModel.Save`), `Pointframe.Tests/ViewModels/SettingsViewModelTests.cs`, `Pointframe.Tests/ViewModels/SettingsViewModelCharacterizationTests.cs` (which persisted properties each section reset and Restore Defaults change; which capture modes each hotkey capture command clears), `Pointframe.Tests/SettingsWindowTests.cs`, `Pointframe.Tests/Services/ThemeServiceTests.cs`, `Pointframe.Tests/Models/HotkeyBindingTests.cs`. Automation: `Pointframe.AutomationTests/Smoke/SettingsWindowSmokeTests.cs`, `Pointframe.AutomationTests/Smoke/SettingsSectionNavigationTests.cs`.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/IUserSettingsService.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe/Views/SettingsWindow.xaml`, `Pointframe/Models/SettingsSection.cs`, `Pointframe/Models/SettingsSectionItem.cs`, `Pointframe/Models/HotkeyBinding.cs`, `Pointframe/Models/HotkeyModifiers.cs`, `Pointframe/Models/AppTheme.cs`, `Pointframe/Services/Infrastructure/ThemeService.cs`, `Pointframe/Models/WatermarkSettings.cs`, `Pointframe/Models/ScreenshotWatermarkSettings.cs`, `Pointframe/Models/VideoWatermarkSettings.cs`.

**Lessons.**

- Lesson: Restore-defaults flows must update hidden persisted settings directly, not through a sticky mode flag
- Lesson: Window-local WPF resources must stay self-contained when tests instantiate windows directly
