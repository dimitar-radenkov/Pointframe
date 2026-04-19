# Contributing

Thanks for your interest in contributing to Pointframe.

The product is now branded as `Pointframe`, but the repository and project paths still use `SnippingTool` during the transition.

## Getting Started

**Prerequisites:** Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Visual Studio 2022 or VS Code.

```powershell
git clone https://github.com/dimitar-radenkov/SnippingTool.git
cd SnippingTool
dotnet build SnippingTool/SnippingTool.csproj
dotnet test  SnippingTool.Tests/SnippingTool.Tests.csproj
```

## Before You Submit

Run the formatter — CI will reject unformatted code:

```powershell
dotnet format SnippingTool/SnippingTool.csproj
```

## Key Conventions

- **MVVM:** Use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm. Never raise `PropertyChanged` manually.
- **DI:** Every service must have an interface (`IMyService`). Register new services in `App.xaml.cs → ConfigureServices()`.
- **Models:** Shape data belongs in `Models/ShapeParameters.cs` as an immutable `sealed record`, never a mutable class.
- **Braces:** Always use `{}` blocks for `if`/`else`/`for`/`foreach` — even single-line bodies.
- **Nullable:** All reference-type fields and parameters must be non-nullable unless genuinely optional (use `?`).
- **No XML doc comments** (`/// <summary>`).

## Adding a New Annotation Tool

1. Add a value to the `AnnotationTool` enum in `AnnotationTool.cs`.
2. Add a matching `sealed record` in `Models/ShapeParameters.cs`.
3. Handle the new case in `AnnotationViewModel.TryGetShapeParameters()`.
4. Render it in `AnnotationCanvasRenderer` (`UpdateDragFeedback` + `CommitShape`).
5. Add unit tests in `SnippingTool.Tests/ViewModels/AnnotationViewModelTests.cs`.

## Pull Request Tips

- Keep PRs focused — one feature or fix per PR.
- Add or update tests for any behaviour change.
- Reference related issues with `Fixes #123`.
- The CI pipeline runs build, tests, format check, and CodeQL on every PR — make sure it passes locally first.

## Good First Issues

New to the codebase? Look for issues labelled [`good first issue`](https://github.com/dimitar-radenkov/SnippingTool/issues?q=label%3A%22good+first+issue%22).
