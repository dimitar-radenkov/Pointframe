# Contributing

Thanks for your interest in contributing to Pointframe.

The product is now branded as `Pointframe`, and the main solution, project files, namespaces, and top-level source folders use `Pointframe`.

## Getting Started

**Prerequisites:** Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Visual Studio 2022 or VS Code.

```powershell
git clone https://github.com/dimitar-radenkov/Pointframe.git
cd Pointframe
dotnet build Pointframe/Pointframe.csproj
dotnet test  Pointframe.Tests/Pointframe.Tests.csproj
```

## Before You Submit

Run the formatter — CI will reject unformatted code:

```powershell
dotnet format Pointframe/Pointframe.csproj
```

## Key Conventions

- **MVVM:** Use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm. Never raise `PropertyChanged` manually.
- **DI:** Every service must have an interface (`IMyService`). Register new services in `AppServiceRegistration.cs → AddPointframeAppServices()`.
- **Models:** Shape data belongs in `Pointframe/Models/ShapeParameters.cs` as an immutable `sealed record`, never a mutable class.
- **Braces:** Always use `{}` blocks for `if`/`else`/`for`/`foreach` — even single-line bodies.
- **Nullable:** All reference-type fields and parameters must be non-nullable unless genuinely optional (use `?`).
- **No XML doc comments** (`/// <summary>`).

## Adding a New Annotation Tool

1. Add a value to the `AnnotationTool` enum in `Pointframe/Models/AnnotationTool.cs`.
2. Add a matching `sealed record` in `Pointframe/Models/ShapeParameters.cs`.
3. Handle the new case in `AnnotationViewModel.TryGetShapeParameters()`.
4. Add a `<Name>ShapeHandler` under `Pointframe/Services/Annotation/Handlers/` implementing `IAnnotationShapeHandler`, and register it in `AnnotationCanvasRenderer`.
5. Add unit tests in `Pointframe.Tests/ViewModels/AnnotationViewModelTests.cs`. The [Developer Guide](docs/developer-guide.md) §8 and the [knowledge base](docs/knowledge-base/knowledge-base.md) carry the full recipe.

## Pull Request Tips

- Keep PRs focused — one feature or fix per PR.
- Add or update tests for any behaviour change.
- Reference related issues with `Fixes #123`.
- The CI pipeline runs build, tests, format check, and CodeQL on every PR — make sure it passes locally first.

## Good First Issues

New to the codebase? Look for issues labelled [`good first issue`](https://github.com/dimitar-radenkov/Pointframe/issues?q=label%3A%22good+first+issue%22).
