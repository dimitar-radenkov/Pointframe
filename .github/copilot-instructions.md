# Pointframe Copilot Instructions

Pointframe is a Windows-only WPF screen-capture, annotation, and recording application built on .NET 10. It uses CommunityToolkit.Mvvm, dependency injection, Serilog, and xUnit. The product name, solution, project files, namespaces, and top-level folders are `Pointframe`.

## Commands

```powershell
dotnet build Pointframe/Pointframe.csproj
dotnet run --project Pointframe/Pointframe.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj
dotnet test Pointframe.Tests/Pointframe.Tests.csproj --filter "FullyQualifiedName~<TestClass>"
dotnet format Pointframe/Pointframe.csproj
dotnet format Pointframe/Pointframe.csproj --verify-no-changes
```

## Required Reading And Documentation

- Before architectural or cross-subsystem work, read `docs/knowledge-base/knowledge-base.md`, using its contents list to load only relevant sections.
- Before changing UI flow or window lifecycle code (overlay, dialog, hotkey, capture, tray, recording, DPI, multi-monitor), read `lessons.md` first.
- Before finishing a change under `Pointframe/` or `Pointframe.Data/`, decide whether the knowledge base needs an add, an update, or nothing. State that decision in the final response.
- Use the `knowledge-base` skill when documenting architecture, subsystems, decisions, invariants, recurring how-tos, or durable references; use it after a completed feature, refactor, or fix when those facts changed.
- Record a reusable bug, edge case, or implementation trap in `lessons.md` with its problem, root cause, fix, and takeaway. Keep it durable and concise rather than a task journal.

## Architecture And Conventions

- `App.xaml.cs` owns startup and shutdown. Register app services and windows in `AppServiceRegistration.cs` (`AddPointframeAppServices`); add `OverlayWindow` dependencies through its `CreateOverlayWindow` factory.
- Use MVVM: ViewModels inherit `ObservableObject`; use `[ObservableProperty]` and `[RelayCommand]` rather than manually raising `PropertyChanged`.
- Every public service has an `I<Name>` interface, is registered through DI, and is constructor-injected. Use singleton for long-lived state or OS handles, transient for per-operation objects, ViewModels, and windows, and scoped only for EF Core data services.
- Window code-behind is limited to view-specific layout, HWND interop, DPI, and focus. Put application state and commands in ViewModels and OS, I/O, network, or process access behind services.
- Follow the knowledge base for cross-cutting invariants, especially committed-only annotation undo groups, point-of-use settings reads, per-monitor DIP/pixel conversion, recording geometry, and installer publishing.
- Use Allman braces, file-scoped namespaces, nullable reference types, `_camelCase` private fields, and `PascalCase` public symbols. Do not add XML documentation comments.
- `UserSettings` is mutable. A setting added to `UserSettings` also needs Settings ViewModel persistence and its UI binding; consumers read `IUserSettingsService.Current` at the point of use rather than caching values.

## Testing And Validation

- Add focused xUnit tests alongside changed behavior. Test ViewModels and services directly with Moq; unit tests do not start WPF.
- Run `dotnet format Pointframe/Pointframe.csproj` after every C# edit. Before review or CI readiness, also run `dotnet format Pointframe/Pointframe.csproj --verify-no-changes`.
- Run the narrowest applicable tests, then broaden validation when shared contracts, DI registration, or user-visible workflows change.
- Never commit or push. Prepare the changes and validation results for user review.