# Pointframe

<p align="center">
  <a href="https://dimitar-radenkov.github.io/SnippingTool/">
    <img src="website/app-icon.png" alt="Pointframe icon" width="48" height="48">
  </a>
</p>

<h1 align="center">Pointframe</h1>


<p align="center">
  <b>Pointframe is a screen capture and recording tool for Windows, built for bug reports, tutorials, and fast feedback.</b><br>
  Capture, annotate, blur, record, and share what matters without breaking your flow.
</p>

<p align="center">
  <b>🌐 <a href="https://dimitar-radenkov.github.io/SnippingTool/">Visit the Official Website</a></b>
</p>

<p align="center">
  <a href="https://github.com/dimitar-radenkov/SnippingTool/actions/workflows/ci.yml"><img src="https://github.com/dimitar-radenkov/SnippingTool/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://codecov.io/gh/dimitar-radenkov/SnippingTool"><img src="https://codecov.io/gh/dimitar-radenkov/SnippingTool/branch/master/graph/badge.svg" alt="codecov"></a>
  <a href="https://github.com/dimitar-radenkov/SnippingTool/releases/latest"><img src="https://img.shields.io/github/v/release/dimitar-radenkov/SnippingTool?color=success" alt="Latest release"></a>
  <a href="https://github.com/microsoft/winget-pkgs/tree/master/manifests/d/DimitarRadenkov/Pointframe"><img src="https://img.shields.io/winget/v/DimitarRadenkov.Pointframe?label=winget&color=blue" alt="winget"></a>
  <a href="https://github.com/dimitar-radenkov/SnippingTool/releases"><img src="https://img.shields.io/github/downloads/dimitar-radenkov/SnippingTool/total?label=downloads&color=purple" alt="Downloads"></a>
</p>

<p align="center">
  <video src="https://github.com/user-attachments/assets/d4e0c937-a845-4266-9454-2b816934f949" width="100%" controls autoplay loop muted></video>
</p>

## 🚀 Quick Start

Get up and running in seconds using the Windows Package Manager (winget):

Starting with the `5.0` release line, the winget package ID is `DimitarRadenkov.Pointframe`.

```powershell
winget install DimitarRadenkov.Pointframe
```

*Prefer a manual install? Download the latest installer from the [Releases](https://github.com/dimitar-radenkov/SnippingTool/releases) page.*

1. Install Pointframe with `winget install DimitarRadenkov.Pointframe` or download the latest installer from [Releases](https://github.com/dimitar-radenkov/SnippingTool/releases).
2. Press `Print Screen` to open the capture overlay and select the region you want.
3. Annotate, copy, save, pin, or record the region from the overlay and recording HUD.

## ✨ Why use this over the built-in Windows tool?

- **Live Video Annotations:** Draw, highlight, and redact *while* recording. No need for post-production video editing.
- **Privacy First (Live Blur):** Drag over sensitive content (passwords, emails, API keys) to apply a live Gaussian blur that stays hidden in the final export.
- **Built-in OCR:** Lasso any text on your screen (even in images or videos) to instantly copy it to your clipboard.
- **Pin to Screen:** Pin captured screenshots as floating, always-on-top windows for quick reference while coding or writing.

## Latest features

- **Show your point while recording** — Draw on the captured region as you record, then switch between interactive and drawing modes from the HUD.
- **Stay in control without breaking flow** — Pause, resume, stop, switch tools, clear annotations, and open the output folder from one floating HUD.
- **Redact sensitive content live** — Blur annotations now sample from the live recording region, so private details stay hidden in the final video.

## Why people use it

- **Show the problem, not just describe it** — Bugs and UI issues are easier to understand when the screenshot or recording already contains the important highlights.
- **Make tutorials easier to follow** — Arrows, text, and numbered steps keep people focused on what matters.
- **Hide private details before sharing** — Blur emails, passwords, tokens, and anything else you do not want on screen.
- **Work from one place** — Capture, annotate, copy, save, pin, and record without bouncing between tools.

## Features

- **Region capture** — Press the configured hotkey (default: `Print Screen`) to draw a selection on screen
- **Frozen screen snapshot** — The screen is captured instantly when the hotkey is pressed, freezing menus, tooltips, and popups exactly as they appear
- **Selection magnifier** — A zoomed loupe follows your cursor while drawing the capture region for pixel-accurate selection
- **Configurable capture hotkey** — Change the capture hotkey to any key you prefer from Settings
- **Annotation tools** — Arrow, line, rectangle, circle, pen, highlighter, text, numbered steps, blur
- **Blur tool** — Drag over sensitive content (faces, emails, passwords) to apply a Gaussian blur before sharing
- **OCR — Copy Text** — Draw a lasso around text in the screenshot to extract it via OCR and copy to clipboard (uses Windows.Media.Ocr, no external dependencies)
- **Open existing image** — Load a PNG, JPG/JPEG, or BMP from the tray menu and annotate it without taking a new screenshot
- **Pin screenshot** — Pin the captured screenshot as a floating, always-on-top, resizable window for quick reference while you work
- **Undo / redo** — Full undo/redo stack during annotation
- **Copy & auto-save** — Copy to clipboard; optional auto-save to a configurable folder
- **Screen recording** — Record a selected region to MP4 (H.264 via ffmpeg), with optional microphone audio from a selected Windows input device
- **Recording-time annotations** — Add shapes and text directly on top of a recording while it is in progress
- **Capture delay** — Configurable countdown (0 / 3 / 5 / 10 s) before the selection overlay appears, useful for capturing menus and hover states
- **Auto-updates** — A background service checks GitHub Releases on every launch and on a configurable schedule (every day / 2 days / 3 days). When a new version is found a tray balloon appears; click it to confirm, watch the progress bar, and the installer runs automatically — no browser, no manual downloads
- **System tray** — Runs silently in the background; all actions accessible from the tray icon
- **Theme support** — Choose Light, Dark, or follow the system theme from Settings

## Use cases

- **Bug reports** — Capture a precise region, annotate it, and copy or save the result for issue tracking and support requests
- **Documentation** — Create quick step-by-step screenshots with arrows, numbered steps, and text callouts for guides and tutorials
- **Live workflow capture** — Record a selected region while drawing annotations on top of the recording as you work
- **Sensitive content redaction** — Blur passwords, emails, and other private details before sharing screenshots or recordings
- **Text extraction** — Select text in a screenshot with OCR and copy it directly to the clipboard

## How does it compare?

| Feature | Pointframe | Windows Snipping Tool | Greenshot | ShareX |
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
| Recording-time annotations | ✅ | ❌ | ❌ | ❌ |
| Recording HUD controls (pause / resume / stop / tools) | ✅ | ❌ | ❌ | ❌ |
| Live blur capture during recording | ✅ | ❌ | ❌ | ❌ |
| Capture delay / countdown | ✅ | ✅ | ✅ | ✅ |
| Auto-save to folder | ✅ | ✅ | ✅ | ✅ |
| Auto-updates (background, in-app install) | ✅ | ✅ | ❌ | ❌ |
| Open source | ✅ | ❌ | ✅ | ✅ |

## Requirements

- Windows 10 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installation

**Via winget (recommended)**

```powershell
winget install DimitarRadenkov.Pointframe
```

**Manual installer**

Download the latest installer from the [Releases](https://github.com/dimitar-radenkov/SnippingTool/releases) page and run it. During setup you can choose to download `ffmpeg.exe`, which is required for MP4 recording and GIF export.

## Troubleshooting

- **Recording or GIF export does not start** — Pointframe requires `ffmpeg.exe` for MP4 recording and GIF export. If you skipped the ffmpeg download during setup, install `ffmpeg.exe` next to the app, under `Assets\ffmpeg`, or on `PATH`.
- **OCR is unavailable** — OCR uses Windows.Media.Ocr and requires a supported Windows build.
- **Hotkey seems ignored** — Make sure another app is not already using the same key and try changing the capture hotkey in Settings.
- **App is running but not visible** — Pointframe lives in the system tray after launch.

## Building from source

```powershell
git clone https://github.com/dimitar-radenkov/SnippingTool.git
cd SnippingTool

dotnet build SnippingTool/Pointframe.csproj
dotnet run   --project SnippingTool/Pointframe.csproj
```

## Running tests

```powershell
dotnet test SnippingTool.Tests/Pointframe.Tests.csproj
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
| Record microphone | Include audio from the selected Windows recording device when screen recording starts |
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
- **Serilog** — file + debug logging (`%LOCALAPPDATA%\Pointframe\logs\`)
- **ffmpeg** — external encoder used for MP4 recording and GIF export
- **Microsoft.Extensions.Hosting** — Generic Host + `BackgroundService` for the auto-update background loop
- **Windows.Media.Ocr** — built-in Windows OCR for text extraction
- **Hardcodet.Wpf.TaskbarNotification** — system tray icon
- **Nerdbank.GitVersioning** — automatic semantic versioning from git history
- **xUnit** — unit tests

## 🤝 Contributing

We welcome contributions! Whether it's reporting a bug, suggesting a feature, or submitting a pull request.
Pointframe is built on a very clean, modern stack (.NET 10, WPF, CommunityToolkit.Mvvm) making it a great jumping-off point for developers.

1. Check out our [Developer Guide](docs/developer-guide.md) and [Architecture Knowledge Base](docs/project-knowledge-base.md).
2. Browse our [Planned Features](docs/planned-features.md) or look for issues tagged `good first issue`.
3. Open a Pull Request!

## Privacy

- Pointframe is local-first and does not use telemetry.
- Screenshots, recordings, and OCR processing stay on your machine.
- Update checks only contact GitHub Releases to look for newer versions.

## Support

If you find this tool useful, consider buying me a beer 🍺

[![PayPal](https://img.shields.io/badge/PayPal-donate-blue?logo=paypal)](https://paypal.me/DimitarRadenkov)
[![Revolut](https://img.shields.io/badge/Revolut-donate-black?logo=revolut)](https://revolut.me/dimitarradenkov)
