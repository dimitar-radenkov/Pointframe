# Pointframe

<p align="center">
  <a href="https://dimitar-radenkov.github.io/Pointframe/">
    <img src="website/app-icon.png" alt="Pointframe icon" width="48" height="48">
  </a>
</p>

<h1 align="center">Pointframe</h1>


<p align="center">
  <b>A free Windows screenshot and recording tool built for fast bug reports, walkthroughs, and support replies.</b><br>
  Capture, annotate, blur, record to MP4/GIF, and extract text with OCR in one lightweight tray app.
</p>

<p align="center">
  <b>🌐 <a href="https://dimitar-radenkov.github.io/Pointframe/">Visit the Official Website</a></b>
</p>

<p align="center">
  <a href="https://github.com/dimitar-radenkov/Pointframe/actions/workflows/ci.yml"><img src="https://github.com/dimitar-radenkov/Pointframe/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://codecov.io/gh/dimitar-radenkov/Pointframe"><img src="https://codecov.io/gh/dimitar-radenkov/Pointframe/branch/master/graph/badge.svg" alt="codecov"></a>
  <a href="https://github.com/dimitar-radenkov/Pointframe/releases/latest"><img src="https://img.shields.io/github/v/release/dimitar-radenkov/Pointframe?color=success" alt="Latest release"></a>
  <a href="https://github.com/microsoft/winget-pkgs/tree/master/manifests/d/DimitarRadenkov/Pointframe"><img src="https://img.shields.io/winget/v/DimitarRadenkov.Pointframe?label=winget&color=blue" alt="winget"></a>
  <a href="https://github.com/dimitar-radenkov/Pointframe/releases"><img src="https://img.shields.io/github/downloads/dimitar-radenkov/Pointframe/total?label=downloads&color=purple" alt="Downloads"></a>
</p>

<p align="center">
  <video src="https://github.com/user-attachments/assets/bb6387d7-5ab9-4e91-91d5-fbe539a7ad13" width="100%" controls autoplay loop muted></video>
</p>

## 🚀 Quick Start

Install in seconds with the Windows Package Manager:

Starting with the `5.0` release line, the winget package ID is `DimitarRadenkov.Pointframe`.

```powershell
winget install DimitarRadenkov.Pointframe
```

*Prefer a manual install? Download the latest installer from the [Releases](https://github.com/dimitar-radenkov/Pointframe/releases) page.*

1. Install Pointframe with `winget install DimitarRadenkov.Pointframe` or download the latest installer from [Releases](https://github.com/dimitar-radenkov/Pointframe/releases).
2. Press `Print Screen` to capture a region.
3. Add arrows, text, or blur and then copy, save, pin, or record.

You can complete your first capture workflow in under a minute.

## Pointframe MCP Server

Pointframe also ships a standalone MCP server for agents that need to inspect the Windows desktop and produce verifiable screenshot or recording artifacts. The MCP server uses `Pointframe.Engine` directly; it does not start the Pointframe tray application, create a WPF overlay, or require the full Pointframe installer.

The standalone host requires an interactive Windows desktop session. It is a local stdio server intended to be launched by VS Code, Copilot, or another MCP client.

### MCP capabilities

The server exposes:

- `list_displays` — return monitor identifiers, physical pixel bounds, and DPI scales.
- `capture_monitor` — capture a named monitor and return a PNG artifact plus metadata.
- `start_recording` — start a whole-monitor MP4 recording. Recording requires an explicit `redactionRegionsCaptureLocalPixels` array, even when it is empty.
- `stop_recording` — stop the active recording and return the finalized MP4 artifact, metadata, and event sidecar references.

Artifacts are written beneath `%LOCALAPPDATA%\Pointframe`:

```text
Screenshots\*.png
Screenshots\*.png.metadata.json
Recordings\*.mp4
Recordings\*.mp4.metadata.json
Recordings\*.mp4.events.jsonl
```

Metadata includes the artifact path, byte length, SHA-256, timestamp, monitor, DPI, and physical capture bounds. Recording event sidecars contain lifecycle and declared-redaction events without bitmap data, OCR text, clipboard contents, or prompts.

### Install a published MCP server

The standard release artifact is a versioned `.mcpb` bundle. It contains the
self-contained `win-x64` server, `ffmpeg.exe`, and an MCPB `manifest.json`.
Download the matching `Pointframe.Mcp-*-win-x64.mcpb` asset from the
[latest release](https://github.com/dimitar-radenkov/Pointframe/releases/latest)
and install it in an MCPB-compatible host. Verify the adjacent `.sha256` file
before installation when the host does not verify the bundle automatically.

For clients that use the MCP Registry, each release also publishes a matching
`*.server.json` file. It uses the `io.github.dimitar-radenkov/pointframe-mcp`
server name, pins the MCPB URL, and includes the bundle SHA-256.

To build the same artifacts locally:

```powershell
dotnet restore Pointframe.Mcp/Pointframe.Mcp.csproj
./packaging/build-mcp-package.ps1 `
  -Version "6.7.0" `
  -FfmpegPath "C:\path\to\ffmpeg.exe"
```

The script also emits a legacy ZIP, the MCPB bundle, a SHA-256 checksum, and
release-ready `server.json` metadata under `packaging/output`.

```powershell
Get-FileHash packaging/output/Pointframe.Mcp-*-win-x64.mcpb -Algorithm SHA256
```

The legacy ZIP remains useful for manual installation. Extract it to a
directory such as `C:\Program Files\Pointframe.Mcp`; it contains the standalone
MCP executable and `ffmpeg.exe`, not the WPF Pointframe application.

### Configure VS Code

Point VS Code at the published executable in `.vscode/mcp.json` or the user MCP configuration:

```json
{
  "servers": {
    "pointframe": {
      "type": "stdio",
      "command": "C:\\Program Files\\Pointframe.Mcp\\Pointframe.Mcp.exe"
    }
  }
}
```

For local development, the workspace configuration can point at the Debug executable instead. Rebuild `Pointframe.Mcp` after code changes before restarting the MCP server.

### Test the MCP server locally

Start the built executable directly:

```powershell
dotnet build Pointframe.Mcp/Pointframe.Mcp.csproj
& .\Pointframe.Mcp\bin\Debug\net10.0-windows10.0.18362.0\Pointframe.Mcp.exe
```

Then initialize the MCP stdio session and call `list_displays` or `capture_monitor` from the MCP client. A successful capture should have a matching `.metadata.json` sidecar whose SHA-256 and byte length agree with the image.

Recording currently captures a whole monitor without microphone audio. Redaction regions are capture-local physical pixels and are applied before ffmpeg receives the frame. The process must run in the logged-in interactive Windows session; Windows services running in session 0 cannot capture the user desktop.

If you find Pointframe useful, a ⭐ on GitHub helps others discover it — thank you!

## ✨ Key highlights

- **Live Video Annotations:** Draw, highlight, and redact *while* recording. No need for post-production video editing.
- **Privacy First (Live Blur):** Drag over sensitive content (passwords, emails, API keys) to apply a live Gaussian blur that stays hidden in the final export.
- **Built-in OCR:** Lasso any text on your screen (even in images or videos) to instantly copy it to your clipboard.
- **Pin to Screen:** Pin captured screenshots as floating, always-on-top windows for quick reference while coding or writing.

## 🆕 What shipped in recent releases

### Jul 2026

- **Tray menu UX refresh** — Improved command grouping, clearer labels, and iconized top-level actions.
- **Capture Library OCR hardening** — Better reliability and scale for OCR-backed library search.
- **Capture + recording hot-path optimizations** — Smoother performance in frequent capture/recording flows.

### Jun 2026

- **Clean Window Snip** — Capture cleaner active-window results via tray action and hotkey.
- **Video watermark support** — Configurable watermark overlays for recorded MP4 output.
- **Video trim workflow** — Trim recordings directly in-app from recent recordings actions.
- **Auto-update tray notification improvements** — Better update signaling and install flow behavior from tray.

### May 2026

- **Capture delay customization** — Adjustable delay presets to capture menus and transient UI states.
- **Screenshot watermark support** — Add configurable watermarking for screenshots.
- **Library and tray workflow upgrades** — Open folders submenu, richer recents actions, and improved tray ergonomics.
- **Expanded auto-update intervals** — Additional cadence options including short intervals and disable mode.

### Apr-Mar 2026 (foundation releases)

- **Whole-screen snip mode** and **whole-screen record hotkey**.
- **Recording HUD improvements** including compact mode and better in-recording controls.
- **GIF export**, **cursor highlight**, and **click ripple** for clearer instructional recordings.
- **Open existing image**, **callout tool**, **color picker**, **pixel ruler**, and **style presets**.
- **Telemetry and usage reporting foundation** for anonymous feature adoption metrics.

For full detail by version, see the [Releases](https://github.com/dimitar-radenkov/Pointframe/releases) page.

## Why people use it

- **Show the problem, not just describe it** — Bugs and UI issues are easier to understand when the screenshot or recording already contains the important highlights.
- **Make tutorials easier to follow** — Arrows, text, and numbered steps keep people focused on what matters.
- **Hide private details before sharing** — Blur emails, passwords, tokens, and anything else you do not want on screen.
- **Work from one place** — Capture, annotate, copy, save, pin, and record without bouncing between tools.

## Features

- **Region capture** — Press the configured hotkey (default: `Print Screen`) to draw a selection on screen
- **Whole-screen snip** — Instantly capture the entire screen from the tray icon or a dedicated hotkey
- **Clean window snip** — Capture a cleaner active-window result directly from tray and dedicated hotkey
- **Frozen screen snapshot** — The screen is captured instantly when the hotkey is pressed, freezing menus, tooltips, and popups exactly as they appear
- **Selection magnifier** — A zoomed loupe follows your cursor while drawing the capture region for pixel-accurate selection
- **Configurable capture hotkeys** — Change the region-capture hotkey and the whole-screen record hotkey independently from Settings
- **Annotation tools** — Arrow, line, rectangle, circle, pen, highlighter, text, numbered labels, blur/pixelate, callout (speech bubble), color picker, pixel ruler
- **Style presets** — Up to 5 named color-and-thickness shortcuts shown as quick-access dots in the annotation toolbar; fully configurable in Settings
- **Color picker tool** — Sample any pixel color from the frozen screenshot; the loupe zooms in with a hex preview and sets the active annotation color
- **Pixel ruler tool** — Draw a ruler across the screenshot to measure distances in pixels
- **Blur tool** — Drag over sensitive content (faces, emails, passwords) to apply a Gaussian blur before sharing
- **OCR — Copy Text** — Draw a lasso around text in the screenshot to extract it via OCR and copy to clipboard (uses Windows.Media.Ocr, no external dependencies)
- **Capture Library** — Browse your saved captures, filter by date range, and search by filename or OCR text from the tray Library entry
- **Open existing image** — Load a PNG, JPG/JPEG, or BMP from the tray menu and annotate it without taking a new screenshot
- **Pin screenshot** — Pin the captured screenshot as a floating, always-on-top, resizable window for quick reference while you work
- **Screenshot Beautifier** — Frame a capture on a gradient or solid background (seven presets) for a presentation-ready image
- **Screenshot watermark** — Optionally stamp a configurable text watermark on captured screenshots
- **Undo / redo** — Full undo/redo stack during annotation
- **Copy & auto-save** — Copy to clipboard; optional auto-save to a configurable folder
- **Screen recording** — Record a selected region to MP4 (H.264 via ffmpeg) or start a whole-screen recording instantly with `Ctrl+Shift+R` (default); optional microphone audio from a selected Windows input device
- **Recording-time annotations** — Add shapes and text directly on top of a recording while it is in progress; switch between draw mode and interact mode from the floating HUD
- **Video watermark** — Optionally burn a configurable watermark into MP4 recordings
- **Video trim** — Trim the start and end of a recent recording from the tray's Recent recordings menu (requires ffmpeg)
- **Tray menu icons** — Core tray actions now include consistent glyph icons for faster scanning
- **Cursor highlight** — Configurable glowing ring around the cursor during recording so viewers never lose track of your pointer
- **Click ripple** — Visual ripple effect on mouse clicks during recording to make interactions obvious
- **GIF export** — Export any recent recording to GIF directly from the tray's Recent recordings menu (requires ffmpeg)
- **Recording transcripts** — Automatically transcribe narrated recordings to `.txt` and `.srt` sidecar files. Runs entirely on your machine with Whisper — no cloud, no API key, nothing uploaded. English only; transcribes microphone narration, not system audio
- **Capture delay** — Configurable countdown (0 / 3 / 5 / 10 s) before the selection overlay appears, useful for capturing menus and hover states
- **Auto-updates** — A background service checks GitHub Releases on launch and on a configurable schedule (every 2 hours / 6 hours / 12 hours / day / 2 days / 3 days / never). When a new version is found a tray balloon appears; click it to confirm and install without opening the browser
- **System tray** — Runs silently in the background; all actions accessible from the tray icon
- **Theme support** — Choose Light, Dark, or follow the system theme from Settings

## Use cases

- **Bug reports** — Capture a precise region, annotate it, and copy or save the result for issue tracking and support requests
- **Documentation** — Create quick step-by-step screenshots with arrows, numbered steps, and text callouts for guides and tutorials
- **Live workflow capture** — Record a selected region while drawing annotations on top of the recording as you work
- **Sensitive content redaction** — Blur passwords, emails, and other private details before sharing screenshots or recordings
- **Text extraction** — Select text in a screenshot with OCR and copy it directly to the clipboard

## System tray menu

Right-click the tray icon to access all actions:

| Item | Description |
|---|---|
| New Snip | Open the region-capture overlay (same as the capture hotkey) |
| Whole Screen Snip | Instantly capture the entire screen |
| Clean Window Snip | Capture the active window with a cleaner result |
| Open Image... | Load a PNG / JPG / BMP file and open it in the annotation overlay |
| Recent Captures | Submenu listing the last 5 saved screenshots; each has **Open** and **Open folder** actions |
| Recent Recordings | Submenu listing the last 5 recordings; each has **Open**, **Trim**, **Export to GIF**, and **Open folder** actions |
| Library | Open the capture library window |
| Open Folders | Quick access to Snips Folder, Videos Folder, and Logs Folder |
| Settings | Open the Settings window |
| Check for Updates / Install Update | Manually check for updates or install a pending update directly from tray |
| About | Show version information |
| Quit Pointframe | Quit the application |

Left-clicking the tray icon triggers **New Snip** directly.

## Settings

Open **Settings** from the tray icon to configure:

### Capture

| Setting | Description |
|---|---|
| Screenshot save folder | Where auto-saved screenshots are written |
| Auto-save on copy | Automatically save every screenshot when copied |
| Capture delay | Countdown (sec) before the selection overlay opens: 0 / 3 / 5 / 10 |
| Capture hotkey | The key that triggers the region-capture overlay (default: `Print Screen`); supports modifier keys (Ctrl, Shift, Alt) |

### Recording

| Setting | Description |
|---|---|
| Recording output folder | Where recorded MP4 files are saved |
| Record hotkey | The key combination that starts a whole-screen recording (default: `Ctrl+Shift+R`) |
| Video watermark | Optional watermark overlay in MP4 recordings |
| Cursor highlight | Show a glowing ring around the cursor during recording; configurable size |
| Click ripple | Show a ripple effect on mouse clicks during recording |
| Microphone *(advanced)* | Include microphone audio when recording starts |
| Microphone device *(advanced)* | Which Windows audio input device to use |
| Transcript *(advanced)* | Generate a `.txt` and `.srt` transcript after a recording is saved (on by default). Requires microphone audio and the English speech model; the row shows which one is missing and offers to download it |
| GIF export FPS *(advanced)* | Frame rate for GIF exports: 5 / 8 / 10 / 15 / 20 |

### Annotation

| Setting | Description |
|---|---|
| Default annotation color | Pre-selected color when the overlay opens |
| Stroke thickness | Default pen/shape width |
| Style presets | Up to 5 named color-and-thickness shortcuts shown in the annotation toolbar |

### Shortcuts

| Setting | Description |
|---|---|
| Region capture hotkey | Opens the region capture overlay (default: `Print Screen`) |
| Whole-screen record hotkey | Starts whole-screen recording (default: `Ctrl+Shift+R`) |
| Clean window snip hotkey | Starts clean-window capture (default: `Ctrl+Shift+W`) |
| Overlay shortcuts | Configure copy, save-as, undo, redo, show-shortcuts, and close keys for the overlay |

### App

| Setting | Description |
|---|---|
| Theme | App appearance: Light, Dark, or System (follows Windows) |
| Auto-update check interval | How often to check for new releases: Every 2 hours / Every 6 hours / Every 12 hours / Every day / Every 2 days / Every 3 days / Never |

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Print Screen` (default, configurable) | Open region-capture overlay |
| `Ctrl+Shift+R` (default, configurable) | Start whole-screen recording |
| `Ctrl+Shift+W` (default, configurable) | Start clean-window snip |
| `Ctrl+Z` | Undo last annotation |
| `Ctrl+Y` | Redo annotation |
| `Ctrl+C` | Copy screenshot to clipboard |
| `Escape` | Close the overlay / cancel current action |

## Requirements

- Windows 10 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- ffmpeg — for MP4 recording and GIF export. The installer offers to download it; it can also be placed next to the app or on `PATH`
- English speech model (~141 MB) — only for recording transcripts. Tick the optional component during setup, or download it later from **Settings ▸ Recording**
- Standalone MCP recording additionally requires `ffmpeg.exe`; the published MCP package builder places it next to `Pointframe.Mcp.exe`

## Installation

**Via Scoop**

```powershell
scoop install pointframe
```

**Via winget (recommended)**

```powershell
winget install DimitarRadenkov.Pointframe
```

**Manual installer**

Download the latest installer from the [Releases](https://github.com/dimitar-radenkov/Pointframe/releases) page and run it. During setup you can choose to download `ffmpeg.exe`, which is required for MP4 recording and GIF export.

## Troubleshooting

- **Recording or GIF export does not start** — Pointframe requires `ffmpeg.exe` for MP4 recording and GIF export. If you skipped the ffmpeg download during setup, install `ffmpeg.exe` next to the app, under `Assets\ffmpeg`, or on `PATH`.
- **OCR is unavailable** — OCR uses Windows.Media.Ocr and requires a supported Windows build.
- **Hotkey seems ignored** — Make sure another app is not already using the same key and try changing the capture hotkey in Settings.
- **App is running but not visible** — Pointframe lives in the system tray after launch.

## Building from source

```powershell
git clone https://github.com/dimitar-radenkov/Pointframe.git
cd Pointframe

dotnet build Pointframe/Pointframe.csproj
dotnet run   --project Pointframe/Pointframe.csproj

# Build the standalone MCP host
dotnet build Pointframe.Mcp/Pointframe.Mcp.csproj
```

## Running tests

```powershell
dotnet test Pointframe.Tests/Pointframe.Tests.csproj
```

## Project structure

```
Pointframe/             Main WPF application
  App.xaml.cs           DI setup, tray icon, global hotkeys
  AnnotationTool.cs     Enum of all annotation tool types
  CountdownWindow       Fullscreen countdown overlay
  OverlayWindow         Region-selection and annotation UI
  RecordingOverlayWindow  Live annotation surface during recording
  ViewModels/           MVVM view models
  Services/             Screen capture, recording, geometry, update check
  Models/               Immutable data records and settings

Pointframe.Tests/       xUnit test project
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
- **Azure Monitor / OpenTelemetry** — anonymous usage telemetry (disabled when connection string is absent)
## 🤝 Contributing

We welcome contributions! Whether it's reporting a bug, suggesting a feature, or submitting a pull request.
Pointframe is built on a very clean, modern stack (.NET 10, WPF, CommunityToolkit.Mvvm) making it a great jumping-off point for developers.

1. Check out our [Developer Guide](docs/developer-guide.md) and [Architecture Knowledge Base](docs/knowledge-base/knowledge-base.md).
2. Browse the [open issues](https://github.com/dimitar-radenkov/Pointframe/issues) or look for ones tagged `good first issue`.
3. Open a Pull Request!

## Privacy & Telemetry

Pointframe collects **anonymous, privacy-safe usage telemetry** in official builds to help understand how the app is used and catch errors early. Screenshots, recordings, OCR output, file names, file paths, exception messages, and stack traces are not sent as telemetry.

### What is collected

Every event below is defined in [`TelemetryEventCatalog.cs`](Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs), which is the single source of truth. A unit test fails the build if this table and the catalog ever disagree.

**App lifecycle**

| Event | Properties |
|---|---|
| `app_started` | `os_build`, `screen_count` |
| `startup_completed` | `duration_ms` |
| `app_heartbeat` | `uptime_minutes` (sent every 4 hours while the tray app remains open) |
| `app_closed` | `session_minutes` |

**Capture**

| Event | Properties |
|---|---|
| `snip_started` | `type` (region / whole_screen), `source` (tray / hotkey) |
| `snip_cancelled` | `type` (region / whole_screen) |
| `capture_delay_used` | `delay_seconds` |
| `capture_completed` | `action` (copy / save / save_as / auto_save) |
| `capture_pinned` | — |
| `first_capture_completed` | `capture_type`, `first_action`, `time_from_install_minutes` when available |
| `open_image_used` | — |
| `annotation_committed` | `tool`, `count` (one event per tool, sent once when the annotation surface closes) |

**Recording**

| Event | Properties |
|---|---|
| `recording_started` | `type` (region / whole_screen) |
| `recording_completed` | `duration_seconds` when available |
| `transcript_completed` | `success`, `duration_seconds`, plus `segment_count` on success or `skip_reason` when skipped |
| `transcript_failed` | `exception_type` |
| `first_recording_completed` | `with_audio`, `duration_seconds` and `time_from_install_minutes` when available |
| `recording_hud_pause_toggled` | `state` |
| `recording_hud_stopped` | `duration_seconds` |
| `recording_hud_microphone_toggled` | `state` |
| `recording_hud_display_mode_changed` | `display_mode` |
| `recording_hud_annotation_input_toggled` | `annotation_input_state` |
| `recording_hud_tool_selected` | `annotation_tool` |
| `recording_hud_undo_annotations` | — |
| `recording_hud_clear_annotations` | — |
| `ffmpeg_missing` | — |
| `microphone_unavailable` | — |

**Export and editing**

| Event | Properties |
|---|---|
| `gif_export_started` | — |
| `gif_export_completed` | `success`, `duration_seconds` |
| `video_trim_opened` | — |
| `video_trim_started` | — |
| `video_trim_completed` | `success`, `canceled` |
| `beautify_opened` | — |
| `screenshot_beautified` | — |
| `screenshot_beautified_copied` | — |

**OCR and library**

| Event | Properties |
|---|---|
| `ocr_attempted` | `selection_width_px`, `selection_height_px` |
| `ocr_no_text` | `selection_width_px`, `selection_height_px` |
| `ocr_used` | `selection_width_px`, `selection_height_px` |
| `library_open_used` | — |
| `library_ocr_search_used` | — |

**Settings and About**

| Event | Properties |
|---|---|
| `settings_opened` | `app_section` |
| `settings_section_changed` | `app_section` |
| `settings_saved` | `app_section` |
| `settings_section_reset` | `app_section` |
| `settings_defaults_restored` | — |
| `settings_canceled` | — |
| `about_opened` | — |
| `about_closed` | — |
| `about_url_opened` | `url_host` (host name only, never a full URL) |

**Updates and diagnostics**

| Event | Properties |
|---|---|
| `update_check_manual` | — |
| `update_available` | `version` |
| `update_confirmed` | `version` |
| `update_dismissed` | `version` |
| `unhandled_exception` | `exception_type`, `context`, `last_action` when available |

Every event includes an app `version`, a per-run `session_id`, a `telemetry_channel` (`product` or `diagnostic`), a `telemetry_schema_version`, and an `install_id` when one is available. The install ID is a random GUID generated once on first launch and stored locally. It is used only to count unique installs; it is not tied to an account or identity.

Properties are allow-listed per event in the catalog: anything a caller passes that the event does not declare is reported as a schema violation, and every value is truncated to 200 characters. Both measures exist to keep paths, file names, and recognised text out of telemetry by construction rather than by convention.

The `last_action` value attached to `unhandled_exception` is the name of the most recent **product** event — background diagnostic events such as `app_heartbeat` never overwrite it.

**Nothing leaves your machine except these anonymised events.** Screenshots, recordings, OCR output, file names, and file paths are never transmitted. Local diagnostic logs are stored under `%LOCALAPPDATA%\Pointframe\logs\` and may include local paths to help troubleshoot issues; they are not uploaded automatically.

### Source builds

Telemetry is disabled automatically when the `ApplicationInsights:ConnectionString` value in `appsettings.json` is empty (which is the default in the source repository). Only official builds distributed via the installer include the real connection string.

### For contributors

To enable telemetry locally during development, create `Pointframe/appsettings.Local.json` (gitignored):

```json
{
  "ApplicationInsights": {
    "ConnectionString": "<your-connection-string>"
  }
}
```

To set up your own Azure Application Insights resource, follow the [Azure Monitor setup guide](https://learn.microsoft.com/en-us/azure/azure-monitor/app/create-workspace-resource).

### Feature usage report

Use the ready-to-run KQL report pack in [docs/appinsights-feature-usage-queries.kql](docs/appinsights-feature-usage-queries.kql) to track:

- Weekly active installs and sessions
- Per-feature adoption (% installs that used each feature)
- Feature funnel conversion (snip -> annotate -> pin/ocr)
- Power-user and stickiness indicators
- Version split and regression spotting after releases


## Support

If you find this tool useful, consider buying me a beer 🍺

[![PayPal](https://img.shields.io/badge/PayPal-donate-blue?logo=paypal)](https://paypal.me/DimitarRadenkov)
[![Revolut](https://img.shields.io/badge/Revolut-donate-black?logo=revolut)](https://revolut.me/dimitarradenkov)
