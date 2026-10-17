# Pointframe desktop app

The Pointframe tray app is the screenshot and recording tool for people: capture, annotate, blur, record and extract text. This page lists its features, menu, settings, shortcuts, requirements and troubleshooting. For agents and scripts, see the [CLI](cli/README.md) and the [MCP server](mcp/README.md).

## ✨ Key highlights

- **Live Video Annotations:** Draw, highlight, and redact *while* recording. No need for post-production video editing.
- **Privacy First (Live Blur):** Drag over sensitive content (passwords, emails, API keys) to apply a live Gaussian blur that stays hidden in the final export.
- **Built-in OCR:** Lasso any text on your screen (even in images or videos) to instantly copy it to your clipboard.
- **Pin to Screen:** Pin captured screenshots as floating, always-on-top windows for quick reference while coding or writing.

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
- **Upload & copy link** — Send an annotated PNG to one configured HTTPS destination and copy its returned HTTPS link; see the [Upload to Zipline setup recipe](zipline-upload.md)
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
