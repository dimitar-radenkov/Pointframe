# SnippingTool

A lightweight Windows screen-capture and annotation tool built with WPF on .NET 10.

## Features

- **Region capture** — Press `Print Screen` to draw a selection on screen
- **Annotation tools** — Arrow, line, rectangle, ellipse, pen, highlighter, text, numbered steps
- **Undo / redo** — Full undo/redo stack during annotation
- **Copy & auto-save** — Copy to clipboard; optional auto-save to a configurable folder
- **Full-screen capture** — One-click capture of the entire virtual screen via the tray menu
- **Active window capture** — Capture the currently focused window via the tray menu
- **Screen recording** — Record a selected region to a video file (AVI)
- **Capture delay** — Configurable countdown (0 / 3 / 5 / 10 s) before the selection overlay appears, useful for capturing menus and hover states
- **Check for updates** — Checks GitHub Releases and downloads the latest installer
- **System tray** — Runs silently in the background; all actions accessible from the tray icon

## Requirements

- Windows 10 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installation

Download the latest `SnippingTool-Setup-*.exe` from the [Releases](https://github.com/dimitar-radenkov/SnippingTool/releases) page and run it.

## Building from source

```powershell
git clone https://github.com/dimitar-radenkov/SnippingTool.git
cd SnippingTool

dotnet build SnippingTool/SnippingTool.csproj
dotnet run   --project SnippingTool/SnippingTool.csproj
```

## Running tests

```powershell
dotnet test SnippingTool.Tests/SnippingTool.Tests.csproj
```

## Settings

Open **Settings** from the tray icon to configure:

| Setting | Description |
|---|---|
| Screenshot save folder | Where auto-saved screenshots are written |
| Auto-save on copy | Automatically save every screenshot when copied |
| Capture delay | Countdown (sec) before the selection overlay opens |
| Recording output folder | Where recorded videos are saved |
| Frames per second | Recording frame rate |
| JPEG quality | Compression quality for recording frames |
| Default annotation colour | Pre-selected colour when the overlay opens |
| Stroke thickness | Default pen/shape width |

## Project structure

```
SnippingTool/           Main WPF application
  App.xaml.cs           DI setup, tray icon, global hotkey
  AnnotationTool.cs     Enum of all annotation tool types
  CountdownWindow       Fullscreen countdown overlay
  OverlayWindow         Region-selection and annotation UI
  ViewModels/           MVVM view models
  Services/             Screen capture, geometry, update check
  Models/               Immutable data records and settings

SnippingTool.Tests/     xUnit test project
  Services/             Service unit tests
  ViewModels/           ViewModel unit tests
```

## Versioning

Versions are managed automatically by [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning).

- The base version (`major.minor`) is declared in [`version.json`](version.json).
- The patch number is derived from the **commit height** — it increments automatically with every commit, so you never need to touch it manually.
- On a tagged release (`v*`) the version has no pre-release suffix (e.g. `1.2.5`). On non-release builds a short commit hash is appended (e.g. `1.2.5-g1a2b3c4`).

To bump the version:

| Goal | Action |
|---|---|
| Bug-fix / patch | Nothing — commit height auto-increments |
| New feature (minor) | Edit `version.json` → `"version": "1.3"` |
| Breaking change (major) | Edit `version.json` → `"version": "2.0"` |

## Tech stack

- **WPF / .NET 10**
- **CommunityToolkit.Mvvm** — `[ObservableProperty]`, `[RelayCommand]`
- **Microsoft.Extensions.DependencyInjection** — constructor injection throughout
- **Serilog** — file + debug logging (`%LOCALAPPDATA%\SnippingTool\logs\`)
- **SharpAvi** — screen recording
- **Hardcodet.Wpf.TaskbarNotification** — system tray icon
- **Nerdbank.GitVersioning** — automatic semantic versioning from git history
- **xUnit** — unit tests
