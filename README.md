# SnippingTool

[![CI](https://github.com/dimitar-radenkov/SnippingTool/actions/workflows/ci.yml/badge.svg)](https://github.com/dimitar-radenkov/SnippingTool/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/dimitar-radenkov/SnippingTool/branch/master/graph/badge.svg)](https://codecov.io/gh/dimitar-radenkov/SnippingTool)
[![Downloads](https://img.shields.io/github/downloads/dimitar-radenkov/SnippingTool/total?label=downloads&color=blue)](https://github.com/dimitar-radenkov/SnippingTool/releases)
[![Latest release](https://img.shields.io/github/v/release/dimitar-radenkov/SnippingTool)](https://github.com/dimitar-radenkov/SnippingTool/releases/latest)
[![winget](https://img.shields.io/winget/v/DimitarRadenkov.SnippingTool?label=winget)](https://github.com/microsoft/winget-pkgs/tree/master/manifests/d/DimitarRadenkov/SnippingTool)

The open-source Windows snipping tool with OCR, screen recording, and annotation — no subscription, no telemetry.

## Demo

https://github.com/user-attachments/assets/d4e0c937-a845-4266-9454-2b816934f949

## Features

- **Region capture** — Press the configured hotkey (default: `Print Screen`) to draw a selection on screen
- **Frozen screen snapshot** — The screen is captured instantly when the hotkey is pressed, freezing menus, tooltips, and popups exactly as they appear
- **Selection magnifier** — A zoomed loupe follows your cursor while drawing the capture region for pixel-accurate selection
- **Configurable capture hotkey** — Change the capture hotkey to any key you prefer from Settings
- **Annotation tools** — Arrow, line, rectangle, circle, pen, highlighter, text, numbered steps, blur
- **Blur tool** — Drag over sensitive content (faces, emails, passwords) to apply a Gaussian blur before sharing
- **OCR — Copy Text** — Draw a lasso around text in the screenshot to extract it via OCR and copy to clipboard (uses Windows.Media.Ocr, no external dependencies)
- **Pin screenshot** — Pin the captured screenshot as a floating, always-on-top, resizable window for quick reference while you work
- **Undo / redo** — Full undo/redo stack during annotation
- **Copy & auto-save** — Copy to clipboard; optional auto-save to a configurable folder
- **Screen recording** — Record a selected region to MP4 (H.264 via FFmpeg) or AVI (MJPEG via SharpAvi)
- **Capture delay** — Configurable countdown (0 / 3 / 5 / 10 s) before the selection overlay appears, useful for capturing menus and hover states
- **Auto-updates** — A background service checks GitHub Releases on every launch and on a configurable schedule (every day / 2 days / 3 days). When a new version is found a tray balloon appears; click it to confirm, watch the progress bar, and the installer runs automatically — no browser, no manual downloads
- **System tray** — Runs silently in the background; all actions accessible from the tray icon
- **Theme support** — Choose Light, Dark, or follow the system theme from Settings

## How does it compare?

| Feature | SnippingTool | Windows Snipping Tool | Greenshot | ShareX |
|---|:---:|:---:|:---:|:---:|
| Region capture | ✅ | ✅ | ✅ | ✅ |
| Annotation (arrows, shapes, text) | ✅ | ✅ | ✅ | ✅ |
| Numbered steps annotation | ✅ | ❌ | ❌ | ❌ |
| Configurable capture hotkey | ✅ | ❌ | ✅ | ✅ |
| Frozen snapshot (menus & popups) | ✅ | ❌ | ❌ | ❌ |
| Blur / redact sensitive content | ✅ | ❌ | ❌ | ✅ |
| OCR — copy text from screenshot | ✅ | ❌ | ❌ | ✅ |
| Pin screenshot as floating window | ✅ | ❌ | ❌ | ❌ |
| Undo / redo during annotation | ✅ | ❌ | ❌ | ❌ |
| Screen recording (MP4) | ✅ | ✅ | ❌ | ✅ |
| Capture delay / countdown | ✅ | ✅ | ✅ | ✅ |
| Auto-save to folder | ✅ | ✅ | ✅ | ✅ |
| Auto-updates (background, in-app install) | ✅ | ✅ | ❌ | ❌ |
| Open source | ✅ | ❌ | ✅ | ✅ |

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
| Capture hotkey | The key that triggers the capture overlay (default: Print Screen) |
| Recording output folder | Where recorded videos are saved |
| Recording format | Output format: MP4 (H.264) or AVI (MJPEG) |
| HUD close delay | How long the recording HUD stays visible after stopping (0 / 3 / 5 / 10 / 15 / 30 s) |
| Default annotation colour | Pre-selected colour when the overlay opens |
| Stroke thickness | Default pen/shape width |
| Auto-update check interval | How often to check for new releases: Every day / Every 2 days / Every 3 days / Never |
| Theme | App appearance: Light, Dark, or System (follows Windows) |

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Z` | Undo last annotation |
| `Ctrl+Y` | Redo annotation |
| `Ctrl+C` | Copy screenshot to clipboard |
| `Escape` | Close the overlay / cancel current action |

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
- **SharpAvi** — AVI screen recording
- **FFMpegCore** — MP4 screen recording (wraps ffmpeg)
- **Microsoft.Extensions.Hosting** — Generic Host + `BackgroundService` for the auto-update background loop
- **Windows.Media.Ocr** — built-in Windows OCR for text extraction
- **Hardcodet.Wpf.TaskbarNotification** — system tray icon
- **Nerdbank.GitVersioning** — automatic semantic versioning from git history
- **xUnit** — unit tests

## Support

If you find this tool useful, consider buying me a beer 🍺

[![PayPal](https://img.shields.io/badge/PayPal-donate-blue?logo=paypal)](https://paypal.me/DimitarRadenkov)
[![Revolut](https://img.shields.io/badge/Revolut-donate-black?logo=revolut)](https://revolut.me/dimitarradenkov)
