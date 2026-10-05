# Pointframe

<p align="center">
  <a href="https://dimitar-radenkov.github.io/Pointframe/">
    <img src="website/app-icon.png" alt="Pointframe icon" width="48" height="48">
  </a>
</p>

<h1 align="center">Pointframe</h1>


<p align="center">
  <b>A free Windows screenshot and recording tool built for fast bug reports, walkthroughs, and support replies.</b><br>
  Capture a region, a window, or a whole monitor — annotate, blur, record to MP4/GIF, and extract text with OCR.<br>
  Drive it from the tray app, the standalone CLI, or an AI agent over MCP.
</p>

<p align="center">
  <b>🌐 <a href="https://dimitar-radenkov.github.io/Pointframe/">Visit the Official Website</a></b>
</p>

<p align="center">
  <a href="https://github.com/dimitar-radenkov/Pointframe/actions/workflows/ci.yml"><img src="https://github.com/dimitar-radenkov/Pointframe/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://codecov.io/gh/dimitar-radenkov/Pointframe"><img src="https://codecov.io/gh/dimitar-radenkov/Pointframe/branch/master/graph/badge.svg" alt="codecov"></a>
  <a href="https://github.com/dimitar-radenkov/Pointframe/releases/latest"><img src="https://img.shields.io/github/v/release/dimitar-radenkov/Pointframe?color=success" alt="Latest release"></a>
  <a href="https://github.com/microsoft/winget-pkgs/tree/master/manifests/d/DimitarRadenkov/Pointframe"><img src="https://img.shields.io/winget/v/DimitarRadenkov.Pointframe?label=winget&color=blue" alt="winget"></a>
  <a href="https://github.com/dimitar-radenkov/Pointframe/releases"><img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fdimitar-radenkov.github.io%2FPointframe%2Fbadges%2Fdownloads-total.json&color=purple" alt="Downloads"></a>
  <a href="https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Cli-win-x64.zip"><img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fdimitar-radenkov.github.io%2FPointframe%2Fbadges%2Fdownloads-cli.json&color=orange" alt="CLI downloads"></a>
  <a href="https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Mcp-win-x64.mcpb"><img src="https://img.shields.io/endpoint?url=https%3A%2F%2Fdimitar-radenkov.github.io%2FPointframe%2Fbadges%2Fdownloads-mcp.json&color=orange" alt="MCP downloads"></a>
</p>

<p align="center">
  <b>☕ If Pointframe saves you time, consider <a href="https://paypal.me/DimitarRadenkov">buying me a beer</a>:</b><br>
  <a href="https://paypal.me/DimitarRadenkov"><img src="https://img.shields.io/badge/PayPal-donate-blue?logo=paypal" alt="PayPal"></a>
  <a href="https://revolut.me/dimitarradenkov"><img src="https://img.shields.io/badge/Revolut-donate-black?logo=revolut" alt="Revolut"></a>
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
On first launch, Pointframe opens a short welcome with a button to start your first capture.

For the standalone command-line workflow, installation, artifact verification,
exit codes, and troubleshooting, see the dedicated
[Pointframe CLI README](docs/cli/README.md).

## Pointframe CLI

A self-contained Windows CLI for monitor and window discovery, PNG screenshots of a
monitor, a sub-region, or a single window, on-screen text extraction via OCR, and
whole-monitor MP4 recordings. The Pointframe desktop app, the .NET runtime, and the
.NET SDK are not required.

Download `Pointframe.Cli-<version>-win-x64.zip` from the
[latest release](https://github.com/dimitar-radenkov/Pointframe/releases/latest),
extract it, and from that folder run:

```powershell
.\Pointframe.Cli.exe install
```

This copies the CLI to `%LOCALAPPDATA%\Programs\Pointframe.Cli` and adds it to your
user `Path`. Open a new terminal, then run `pointframe displays`. The
[CLI README](docs/cli/README.md#install) and the
[verify guide](docs/cli/verify.md#install) have the checksum step.

The winget package `DimitarRadenkov.Pointframe.Cli` is awaiting acceptance in
winget-pkgs. Once `winget search DimitarRadenkov.Pointframe.Cli` finds it,
`winget install DimitarRadenkov.Pointframe.Cli` replaces the manual steps above.

To make an agent finish only on a passing build and test run in your own project,
see the [verify guide](docs/cli/verify.md). `verify` gates run headless, including
in CI.

Capture, OCR, recording, and desktop verification scenarios require an interactive
Windows desktop session. They cannot capture a user's desktop from a Windows service
(session 0).

```powershell
.\Pointframe.Cli.exe displays
.\Pointframe.Cli.exe windows
.\Pointframe.Cli.exe capture --monitor '\\.\DISPLAY1' --output .\shot.png
.\Pointframe.Cli.exe capture --monitor '\\.\DISPLAY1' --region 100,100,800,600
.\Pointframe.Cli.exe capture-window --window-id 12345678
.\Pointframe.Cli.exe ocr --monitor '\\.\DISPLAY1'
.\Pointframe.Cli.exe ocr-window --window-id 12345678
.\Pointframe.Cli.exe record --monitor '\\.\DISPLAY1' --seconds 10 --output .\take1.mp4
```

Use the exact `monitorName` emitted by `displays`, or a window handle emitted by
`windows`. Every command other than `--help`/`--version` writes a single-line JSON
response to standard output on both the success and the failure path, so a script can
parse it the same way either way: success exits `0`, a runtime failure exits `1` and
carries an `Error.Code` (`target_not_found`, `target_not_capturable`, `invalid_region`,
`invalid_output_path`, `canceled`, or `capture_failed`), and invalid arguments exit `2`
with usage text on standard error.

After installing the CLI, it can install and verify the standalone MCP server
for VS Code without manual archive extraction or JSON editing:

```powershell
pointframe mcp install --client vscode
pointframe mcp doctor --client vscode
```

The installer verifies the published MCPB SHA-256, preserves unrelated VS Code
MCP entries, creates a configuration backup, and performs a real MCP handshake.
Use `pointframe mcp install --client vscode --dry-run` to validate the download
and proposed configuration without persisting changes. See the
[CLI guide](docs/cli/README.md#install-and-verify-the-mcp-server-for-vs-code)
for status, diagnostics, output contracts, and current client support.

Pass `--output <file>` (`-o`) to any command that produces a file to choose the exact
path to write; parent directories are created for you. Without it, screenshots and their
metadata sidecars are saved under `%LOCALAPPDATA%\Pointframe\Screenshots` and recordings
under `%LOCALAPPDATA%\Pointframe\Recordings`, each with a generated timestamped name.

`ocr` captures the monitor the same way `capture` does, then runs Windows OCR against
the captured image and adds a `RecognizedText` field to the JSON output (`null` when no
text is found or no OCR language pack is installed). `capture-window` and `ocr-window` do
the same for a single window by handle, using visible screen-rectangle semantics: an
occluding window may appear in the capture, and minimized, zero-size, off-screen, and
multi-monitor-spanning windows are rejected. `record` starts a direct MP4 recording, waits
for the requested `--seconds` (or an earlier Ctrl+C for a graceful early stop that still
finalizes and reports the artifact), then writes the combined session/artifact JSON;
recordings require `ffmpeg.exe` on `PATH`, via `POINTFRAME_FFMPEG_PATH`, or bundled next
to the executable.

## Pointframe MCP Server

Pointframe also ships a standalone MCP server for agents that need to inspect the Windows desktop and produce verifiable screenshot or recording artifacts. The MCP server uses `Pointframe.Engine` directly; it does not start the Pointframe tray application, create a WPF overlay, or require the full Pointframe installer.

The standalone host requires an interactive Windows desktop session. It is a local stdio server intended to be launched by VS Code, Copilot, or another MCP client.

Official builds of the MCP server and the CLI send anonymous usage counts (tool or command name, outcome, duration bucket, host, MCP client type, version) and nothing else. Opt out with `POINTFRAME_TELEMETRY_OPTOUT=1` or `DO_NOT_TRACK=1`. See the [Privacy Policy](#privacy-policy).

### MCP capabilities

The server exposes:

- 🔎 `search_captures` — search the local catalog of saved screenshots by filename or indexed OCR text. Results may be incomplete while newly discovered images are indexed.
- 🗂️ `get_capture` — retrieve a catalog artifact by its opaque ID, including metadata and an optional downscaled inline preview. It never accepts arbitrary local paths.
- 🖥️ `list_displays` — return monitor identifiers, physical pixel bounds, and DPI scales.
- 🪟 `list_windows` — return visible top-level windows with handles, titles, process names, bounds, and containing monitor names. Window handles are session-local and temporary.
- 📸 `capture_monitor` — capture a named monitor, or an optional monitor-local sub-region of it, and return a PNG artifact plus metadata. The captured image is also returned inline as an image block (downscaled to at most 1600 px on its longest edge) so the calling model can see it directly; pass `includeImage: false` to get metadata only.
- 📸 `capture_window` — capture the visible screen rectangle of a window by its handle from `list_windows`. Occluding windows may appear; minimized, off-screen, and multi-monitor-spanning windows are rejected. Returns the image inline like `capture_monitor` unless `includeImage: false`.
- 🔤 `read_text_from_monitor` — capture a named monitor (optionally a sub-region) and run OCR against it, returning the PNG artifact plus recognized text (`null` when no text is found or no OCR language pack is installed). The captured image is also returned inline unless `includeImage: false`.
- 🔤 `read_text_from_window` — capture a window by handle and run OCR against it. Same screen-rectangle capture semantics as `capture_window`, and the same inline-image behavior.
- 🎥 `start_recording` — start a whole-monitor MP4 recording. `redactionRegionsCaptureLocalPixels` is optional; omit it to record without redaction.
- ⏹️ `stop_recording` — stop the active recording and return the finalized MP4 artifact, metadata, and event sidecar references.
- ⏱️ `get_recording_status` — report whether a recording is currently active and, if so, its session details and elapsed duration; returns no session when nothing is recording.

The server also exposes MCP resources:

- `pointframe://commands` — the exact list of registered tool identifiers (varies depending on whether desktop testing is enabled).
- `pointframe://server-info` — server version, whether desktop testing tools are enabled, and whether `ffmpeg` (required for recording) was found, along with where it was found (`EnvironmentVariable`, `Bundled`, or `Path`). Useful for a health check before calling `start_recording`.

The normal workflow is:

1. Call `list_displays` and select a returned `monitorName`.
2. Call `capture_monitor` with that exact monitor name, call `read_text_from_monitor` to also extract on-screen text, or call `start_recording`. Pass an optional `region` (`{x, y, width, height}` in monitor-local physical pixels) to `capture_monitor`/`read_text_from_monitor` to limit the capture to a sub-rectangle instead of the whole monitor; a region outside the monitor's bounds is rejected rather than clipped.
3. For recording, pass redaction rectangles in capture-local physical pixels. Omit the argument entirely when no redaction is required.
4. Call `get_recording_status` at any time to check whether a recording is active before calling `stop_recording`.
5. Call `stop_recording` to finalize the MP4 and retrieve its metadata.

Example tool arguments:

```json
{
  "monitorName": "\\\\.\\DISPLAY1"
}
```

```json
{
  "monitorName": "\\\\.\\DISPLAY1",
  "redactionRegionsCaptureLocalPixels": [
    { "x": 120, "y": 80, "width": 240, "height": 48 }
  ],
  "framesPerSecond": 20
}
```

Responses contain structured JSON with `Success`, operation identifiers, artifact paths,
byte lengths, SHA-256 hashes, monitor geometry, DPI information, and sidecar paths.
Artifact paths are local filesystem paths on the machine running the MCP server. The
four capture and OCR tools additionally return the captured image itself as an inline
image content block — downscaled to at most 1600 px on its longest edge, while the
full-resolution PNG is always saved to disk — so a client that cannot reach the server's
filesystem can still see the screenshot. Pass `includeImage: false` to suppress it.

#### Smaller responses (opt-in)

Every default above is unchanged. These optional parameters and one server option cut what an agent spends per call:

- `maxImageEdge` (64 through 1600, default 1600) on `capture_monitor`, `capture_window`, `read_text_from_monitor`, `read_text_from_window`, and `desktop_observe_app` caps the inline image's longest edge. Image tokens follow pixels, so a whole-monitor capture at 800 px costs about a quarter of the default. On `desktop_observe_app` the returned image width and height are what action coordinates use, so clicks stay correct.
- `imageFormat: "jpeg"` on the four capture and OCR tools returns the inline image as JPEG (quality 80). It shrinks whole-monitor captures sharply but can be larger than PNG for flat UI windows, so measure before using it on windows. The saved file is always the full-resolution PNG.
- `detail: "compact"` on `desktop_observe_app` keeps every ref (`observationRef`, `imageRef`, `elementRef`, `windowRef`), the process and image identity, and each element's role, name, automation id, and bounds, in a payload about half the size: bounds become `[x, y, width, height]`, null fields are omitted, an element without `windowRef` uses the top-level `windowRef`, `disabled: true` marks disabled elements, and names or text over 200 characters are truncated with the omitted count.
- `detail: "compact"` on `desktop_get_test_report` returns verdicts, criteria, every action outcome, and every check verdict, but leaves out per-item evidence, recorded check conditions, and the whole `proof` (it is not verifiable from the response). The full signed `report.json`, evidence folder, and `index.html` are always written to `sessionDirectory`.
- Server option `--compact-text` (or environment variable `POINTFRAME_MCP_COMPACT_TEXT=1`) replaces the duplicated JSON text block of status-style tools (the desktop action tools, `desktop_check_ui`, `desktop_get_test_report`, `desktop_get_action_result`, and the recording tools) with a one-line summary. `structuredContent` is untouched, and tools whose result is the data itself (lists, search, observations, replay differences) keep their full text. An action summary always states the operation, dispatch, verification, and observation status plus any error; when the dispatch is partial or unknown it says not to resend the action and names `desktop_get_action_result` with the action id.

### MCP use cases

The MCP server is useful when an agent needs a local, verifiable visual artifact
rather than a text-only description of the Windows desktop:

- **Bug report capture:** call `list_displays`, select the affected monitor, then
  call `capture_monitor` to produce a PNG and metadata sidecar that can be attached
  to a report.
- **Reading on-screen text:** call `read_text_from_monitor` to extract error dialogs,
  logs, or terminal output as plain text alongside the screenshot, without a separate
  OCR step.
- **Privacy-safe support recording:** call `start_recording` with capture-local
  rectangles covering credentials, tokens, customer data, or other sensitive areas,
  then call `stop_recording` when the reproduction is complete. Redaction is applied
  before frames are passed to ffmpeg.
- **UI regression evidence:** capture the relevant monitor before and after an
  interaction and use the returned artifact paths and SHA-256 values to identify
  exactly which files were produced.
- **Multi-monitor troubleshooting:** discover displays first and target the exact
  `monitorName` returned by `list_displays` instead of relying on screen order or
  desktop coordinates.
- **Reproducible automation artifacts:** use the structured response metadata to
  record the operation ID, monitor, DPI, physical bounds, file size, timestamp, and
  checksum alongside test or support results.

The server is intentionally local: it is not a remote desktop service and does not
start the Pointframe WPF application. Recording does not include microphone audio.
Event sidecars contain lifecycle and declared-redaction events, but do not contain
bitmap data, OCR text, clipboard contents, or prompts.

Artifacts are written beneath `%LOCALAPPDATA%\Pointframe`:

```text
Screenshots\*.png
Screenshots\*.png.metadata.json
Recordings\*.mp4
Recordings\*.mp4.metadata.json
Recordings\*.mp4.events.jsonl
```

Metadata includes the artifact path, byte length, SHA-256, timestamp, monitor, DPI, and physical capture bounds. Recording event sidecars contain lifecycle and declared-redaction events without bitmap data, OCR text, clipboard contents, or prompts.

### Connect your MCP client

The server ships as a `.mcpb` bundle on every
[release](https://github.com/dimitar-radenkov/Pointframe/releases/latest): the
self-contained `win-x64` server, `ffmpeg.exe` for recording, and an MCPB
`manifest.json`. It does not need the Pointframe desktop app or the .NET runtime.

**Claude Desktop.** Download
[`Pointframe.Mcp-win-x64.mcpb`](https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Mcp-win-x64.mcpb)
and open it; Claude Desktop installs it as an extension. For local validation,
check the MCPB checksum and `manifest.json`; registration is completed in the
Claude Desktop Extensions UI.

**Claude Code, Codex, Cursor, and VS Code** run the server from a folder on
disk. Download and verify the latest bundle once (a `.mcpb` is a ZIP archive):

```powershell
$dir = "$env:LOCALAPPDATA\Programs\Pointframe.Mcp"
$mcpb = "$env:TEMP\Pointframe.Mcp-win-x64.mcpb"
$release = 'https://github.com/dimitar-radenkov/Pointframe/releases/latest/download'
Invoke-WebRequest "$release/Pointframe.Mcp-win-x64.mcpb" -OutFile $mcpb
Invoke-WebRequest "$release/Pointframe.Mcp-win-x64.mcpb.sha256" -OutFile "$mcpb.sha256"
$expected = ((Get-Content "$mcpb.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash $mcpb -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'MCPB SHA-256 verification failed.' }
New-Item -ItemType Directory -Force $dir | Out-Null
tar -xf $mcpb -C $dir
```

Stop the server in your client before updating, because Windows locks a running
`Pointframe.Mcp.exe`. Then register it:

- **Claude Code**

  ```powershell
  claude mcp add --scope user pointframe -- "$env:LOCALAPPDATA\Programs\Pointframe.Mcp\Pointframe.Mcp.exe"
  ```

  `claude mcp add` refuses a name that already exists; to change the path, run
  `claude mcp remove --scope user pointframe` first.

- **Codex** (native user registration; running it again is safe)

  ```powershell
  codex mcp add pointframe -- "$env:LOCALAPPDATA\Programs\Pointframe.Mcp\Pointframe.Mcp.exe"
  ```

- **VS Code**: run **MCP: Add Server** from the Command Palette, choose
  **Command (stdio)**, and enter the path to `Pointframe.Mcp.exe`. Or add it to
  `.vscode/mcp.json` or your user MCP configuration, using your own user name in
  the path:

  ```json
  {
    "servers": {
      "pointframe": {
        "type": "stdio",
        "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Pointframe.Mcp\\Pointframe.Mcp.exe"
      }
    }
  }
  ```

- **Cursor** uses the `mcpServers` format. Cursor also supports Stop hooks for
  `pointframe verify hook stop`:

  ```json
  {
    "mcpServers": {
      "pointframe": {
        "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Pointframe.Mcp\\Pointframe.Mcp.exe"
      }
    }
  }
  ```

- **Other clients** that use the `mcpServers` format, such as
  `%USERPROFILE%\.cursor\mcp.json`:

  ```json
  {
    "mcpServers": {
      "pointframe": {
        "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Pointframe.Mcp\\Pointframe.Mcp.exe"
      }
    }
  }
  ```

To check the connection, ask the agent to list your displays; it should call
`list_displays`. The `pointframe://server-info` resource reports the server version
and whether `ffmpeg` was found for recording.

#### Use with Claude (plugin)

The [`plugin/pointframe`](plugin/pointframe) folder is a Claude plugin for Windows. It
starts this MCP server for you and adds two skills: `verify-desktop-work` (prove that a
change to a Windows desktop app works, with a signed report) and `capture-screen`. On
the first start it downloads the pinned release of the server from GitHub, checks its
SHA-256 against `server.lock.json`, and caches it under
`%LOCALAPPDATA%\Pointframe\plugin-mcp`; the
[plugin README](plugin/pointframe/README.md) lists exactly what it runs and how to
remove it. Once the plugin is listed in the Claude plugin directory, install it from the
`/plugin` marketplace. To try a local copy:

```powershell
claude --plugin-dir ./plugin/pointframe
```

Each release is also published to the official
[MCP Registry](https://registry.modelcontextprotocol.io/v0/servers?search=pointframe)
as `io.github.dimitar-radenkov/pointframe-mcp`, so registry-aware clients can find
and install it. The matching `*.server.json` attached to the release pins the MCPB
URL and includes the bundle SHA-256; verify the adjacent `.sha256` file before
installation when your client does not verify the bundle itself.

For the opt-in black-box desktop-testing driver, including policy validation,
worker behavior, gate procedures, and evidence limits, see the dedicated
[desktop-testing MCP README](docs/mcp-desktop-testing/README.md), which includes
the operator workflow and evidence limits.

### Build the MCP package locally

```powershell
dotnet restore Pointframe.Mcp/Pointframe.Mcp.csproj
./packaging/build-mcp-package.ps1 `
  -Version "1.0.0" `
  -FfmpegPath "C:\path\to\ffmpeg.exe"
```

Use the current release version instead of `1.0.0` when producing a release package.
The script writes the MCPB bundle, a legacy ZIP with the same contents, a SHA-256
checksum, and release-ready `server.json` metadata under `packaging/output`.

For local development, point your client's configuration at the Debug executable
instead, and rebuild `Pointframe.Mcp` after code changes before restarting the MCP
server.

### Test the MCP server locally

Build the server and run the repository's protocol smoke test:

```powershell
dotnet build Pointframe.Mcp/Pointframe.Mcp.csproj
./packaging/test-mcp-stdio.ps1 `
  -ExecutablePath ".\Pointframe.Mcp\bin\Debug\net10.0-windows10.0.18362.0\Pointframe.Mcp.exe"
```

The smoke test verifies the MCP initialize handshake and confirms that the exact
expected tool set is advertised, both with desktop testing disabled and enabled. To test an actual capture, configure the executable in VS
Code, call `list_displays`, then call `capture_monitor` (or `read_text_from_monitor`)
with one of the returned monitor names. A successful capture should have a matching
`.metadata.json` sidecar whose SHA-256 and byte length agree with the image.

Recording currently captures a whole monitor without microphone audio. Redaction regions are capture-local physical pixels and are applied before ffmpeg receives the frame. The process must run in the logged-in interactive Windows session; Windows services running in session 0 cannot capture the user desktop.

### Troubleshooting

- **No displays are returned or capture fails:** run the MCP server in the same
  logged-in interactive Windows session as the desktop you want to capture.
  Windows services and session-0 processes cannot capture the user desktop.
- **`capture_monitor` rejects the monitor:** use the exact `monitorName` returned
  by `list_displays`; do not substitute a friendly display label.
- **Recording cannot start:** confirm that no other Pointframe recording is active,
  the requested monitor still exists, and `framesPerSecond` is between 1 and 60.
- **Recording finalization fails:** make sure `ffmpeg.exe` is next to
  `Pointframe.Mcp.exe` in the extracted package, or rebuild the package with
  `-FfmpegPath` pointing to a valid Windows ffmpeg executable.
- **The MCP client reports invalid protocol output:** stdout is reserved for
  MCP JSON-RPC messages. Run the published executable through the configured MCP
  client rather than wrapping it in a shell that writes additional output.
- **Artifacts cannot be opened:** artifact paths refer to the MCP server's
  machine and user profile. The client must have access to that filesystem.

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
- **Upload & copy link** — Send an annotated PNG to one configured HTTPS destination and copy its returned HTTPS link; see the [Upload to Zipline setup recipe](docs/zipline-upload.md)
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

## Privacy Policy

This policy covers the Pointframe desktop app, the Pointframe CLI, and the Pointframe MCP server. The same text is published at <https://dimitar-radenkov.github.io/Pointframe/privacy.html>.

**Data collection.** Pointframe is free, open source, and needs no account. Screenshots, recordings, OCR text, file names, and file paths are never collected by the project. They stay on your machine under `%LOCALAPPDATA%\Pointframe`, apart from what you choose to send somewhere yourself: an MCP capture tool returns the image to your MCP client (pass `includeImage: false` to return metadata only), and Upload & copy link sends one capture to the HTTPS destination you configured.

**Usage telemetry of the CLI and the MCP server.** Official release builds of the CLI and the MCP server send one anonymous event per MCP tool call or CLI command to Azure Application Insights. The event holds only these fixed values:

| Property | Values |
|---|---|
| `name` | the MCP tool or CLI command, from a fixed list; anything else is `other` |
| `outcome` | `success`, `error`, `cancelled`, or `denied` (never an error message) |
| `duration_bucket` | `lt_1s`, `1_5s`, `5_30s`, or `gt_30s` |
| `host` | `cli` or `mcp` |
| `client` (MCP only) | `claude-desktop`, `claude-code`, `codex`, `vscode`, `cursor`, or `other` |
| `version` | the Pointframe version |

No arguments, paths, screenshots, text, error messages, or identifiers are sent: there is no install ID, session ID, user name, or machine name, and the exporter identifies itself with the fixed names `Pointframe.Cli` or `Pointframe.Mcp`. Because nothing identifies an install, this telemetry counts operations; it cannot count unique users or retention. Source builds send nothing, because the connection string is empty in the repository and injected only by the release pipeline. The first run prints a one-time notice to standard error (the MCP server never writes to standard output outside the protocol, so it also adds the notice to its server instructions once).

**Opting out of CLI and MCP telemetry.** Set the environment variable `POINTFRAME_TELEMETRY_OPTOUT=1` (or the standard `DO_NOT_TRACK=1`), or create `%LOCALAPPDATA%\Pointframe\agent-telemetry.json` containing `{"optOut": true}`. A config file that cannot be read counts as an opt-out. When opted out, no telemetry request is made at all.

**Desktop app telemetry.** Official installer builds also send the usage telemetry described in [Privacy & Telemetry](#privacy--telemetry) below, including a random install ID. The desktop app has no in-app telemetry switch; the opt-out above applies to the CLI and the MCP server only.

**Third-party sharing.** Telemetry goes to Microsoft Azure Application Insights, which acts as a processor on the project's behalf. Application Insights receives the IP address of the connection, derives a coarse country and city from it, and stores the address masked (IP masking is on); the exporter attaches no host name, user name, or other machine details. Nothing is sold or shared with anyone else.

**Retention.** Telemetry is kept in the project's Application Insights resource under Application Insights' default retention (90 days unless the maintainer changes the resource setting) and is used only to see which tools and commands are used and whether they fail. Local files stay until you delete them.

**Contact.** Questions, deletion requests, and privacy concerns: open an issue at <https://github.com/dimitar-radenkov/Pointframe/issues>.

## Privacy & Telemetry

Official builds send usage telemetry to Azure Application Insights. The payload contains a random install ID, a per-run session ID, app version, event name, and only properties declared for that event in the catalog below. `os_build` and `screen_count` are sent with `app_started`; other declared properties are sent only with their corresponding event. The envelope also includes `telemetry_channel` and `telemetry_schema_version`. The exporter uses the fixed service identity `service.name=Pointframe` and `service.instance.id=desktop`; it does not attach the PC name, Windows user or domain, or automatic host, process, and OS resource details. Azure derives country and city from the connection IP address.

### What is collected

Every event below is defined in [`TelemetryEventCatalog.cs`](Pointframe/Services/Infrastructure/TelemetryEventCatalog.cs), which is the single source of truth. A unit test fails the build if this table and the catalog ever disagree.

**App lifecycle**

| Event | Properties |
|---|---|
| `app_started` | `os_build`, `screen_count` |
| `hotkey_status` | `status` (installed / failed) |
| `startup_completed` | `duration_ms` |
| `app_heartbeat` | `uptime_minutes` (sent every 4 hours while the tray app remains open) |
| `app_closed` | `session_minutes` |

**Capture**

| Event | Properties |
|---|---|
| `snip_started` | `type` (region / whole_screen / window_clean / scrolling), `source` (tray / hotkey / onboarding) |
| `onboarding_shown` | — |
| `onboarding_action` | `action` (capture / dismiss) |
| `snip_cancelled` | `type` (region / whole_screen / scrolling) |
| `scrolling_capture_completed` | `count` (frames captured), `stop_reason` (end_of_content / no_overlap / frame_limit / height_limit) |
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
| `recording_failed` | `phase`, `reason`, `inner_types`, and `ffmpeg_exit_code` when known |
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

Every event includes an app `version`, a per-run `session_id`, a `telemetry_channel` (`product` or `diagnostic`), a `telemetry_schema_version` (currently `2`), and an `install_id` when one is available. The install ID is a random GUID generated once on first launch and stored locally. It is used only to count unique installs; it is not tied to an account or identity. Azure may derive country and city from the IP address used to connect to Application Insights.

Properties are allow-listed per event in the catalog: anything a caller passes that the event does not declare is dropped before export and reported locally as a schema violation without logging its value. Every declared value is truncated to 200 characters. OpenTelemetry resource environment overrides (`OTEL_RESOURCE_ATTRIBUTES`, `OTEL_SERVICE_NAME`, and related `OTEL_*` settings) are ignored by the product telemetry pipeline. These measures keep paths, file names, and recognised text out of telemetry by construction rather than by convention.

The `last_action` value attached to `unhandled_exception` is the name of the most recent **product** event — background diagnostic events such as `app_heartbeat` never overwrite it.

Screenshots, recordings, OCR output, file names, file paths, exception messages, and stack traces are not sent as telemetry. Captures stay local until you use Upload & copy link; then only the capture you choose to upload is sent to the HTTPS destination you configured in Settings → Sharing. Local diagnostic logs are stored under `%LOCALAPPDATA%\Pointframe\logs\` and may include local paths to help troubleshoot issues; they are not uploaded automatically.

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
