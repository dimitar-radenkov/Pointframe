# Recipes

Step-by-step recipes any area uses: when to follow one, the steps, how to verify, and the files it touches.

Part of the [Pointframe knowledge base](knowledge-base.md).

## How-tos

### Add a user setting

**When.** Any value the user chooses once and expects to survive restart.

**Steps.**

1. `Pointframe/Models/UserSettings.cs`: add the property with its default in the initializer. Use an enum for choices, not strings.
2. `Pointframe/ViewModels/SettingsViewModel.cs`: add an `[ObservableProperty]` field, load it from `Current` in the constructor, write it back in `Save()`, and reset it in its section's `Reset<Section>Section` method. Both "Reset section" and "Restore defaults" call those methods, so one edit covers both.
3. `Pointframe/Views/SettingsWindow.xaml`: bind a control in the right section (Capture, Recording, Annotation, Shortcuts, App) and give it an `AutomationProperties.AutomationId` that matches a new constant in `Pointframe.AutomationTests/Support/AutomationIds.cs`.
4. Consumers read `IUserSettingsService.Current.<Name>` at the point of use.
5. If the setting has a hidden or derived companion value, make Restore Defaults reset it directly.

**Verify.**

```powershell
dotnet format Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~Settings"
```

`SettingsRoundTripTests` fails if step 2 was skipped, and also if the test fixture does not set the new property to a non-default value. Then change the value in the running app, restart, and confirm it persisted.

**Files.** `Pointframe/Models/UserSettings.cs`, `Pointframe/Services/Infrastructure/UserSettingsService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `Pointframe/Views/SettingsWindow.xaml`, `Pointframe.Tests/Services/SettingsRoundTripTests.cs`. See [Settings persistence](composition.md#settings-are-read-at-the-point-of-use-and-persisted-through-three-files) and [User settings](features/settings.md#user-settings).

### Register a service

**When.** Any class that touches the OS, the file system, a process, the network, or holds app-wide state. If a ViewModel would otherwise call a static API, wrap the API in a service.

**Steps.**

1. Create `I<Name>.cs` and `<Name>.cs` in the matching folder under `Pointframe/Services/` (`Annotation`, `Capture`, `Infrastructure`, `Messaging`, `Recording`, `Update`). Namespaces do not follow folders: most services use `Pointframe.Services`, messaging uses `Pointframe.Services.Messaging`, shape handlers use `Pointframe.Services.Handlers`. Follow the neighbors.
2. Register in `AddPointframeAppServices` in `Pointframe/AppServiceRegistration.cs`:
   - `AddSingleton` for state, caches, OS handles, hooks, and anything a hosted service uses.
   - `AddTransient` for per-operation objects, disposables, ViewModels, and windows.
   - Never `AddScoped` in the app project; scoped is reserved for EF Core in `Pointframe.Data`.
   - Needs runtime arguments? Register a `Func<TArg, TService>` factory like the existing `TrimViewModel` and `RecordingHudViewModel` ones.
3. Inject through the constructor. If `OverlayWindow` needs it, add the parameter to `CreateOverlayWindow` in the same file.
4. Tests: `new Mock<I<Name>>()`, `Setup(...)`, `Verify(..., Times.Once)`. The service's own tests go in `Pointframe.Tests/Services/<Name>Tests.cs`.
5. Logging: inject `ILogger<T>`; Serilog is configured on the host.

**Verify.**

```powershell
dotnet build Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~AppTests"
```

`Pointframe.Tests/AppTests.cs` builds the container and resolves the core services, so a missing registration fails there before it fails at runtime.

**Files.** `Pointframe/AppServiceRegistration.cs`. See [App bootstrap](composition.md#app-bootstrap-di-and-messaging) and [D-001](decisions.md#d-001-mvvm-plus-di-is-the-composition-model).
