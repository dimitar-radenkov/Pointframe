# Pointframe MCP Server

Pointframe also ships a standalone MCP server for agents that need to inspect the Windows desktop and produce verifiable screenshot or recording artifacts. The MCP server uses `Pointframe.Engine` directly; it does not start the Pointframe tray application, create a WPF overlay, or require the full Pointframe installer.

The standalone host requires an interactive Windows desktop session. It is a local stdio server intended to be launched by VS Code, Copilot, or another MCP client.

Official builds of the MCP server and the CLI send anonymous usage counts (tool or command name, outcome, duration bucket, host, MCP client type, version, a random install ID not tied to a name, account, or machine) and nothing else. Opt out with `POINTFRAME_TELEMETRY_OPTOUT=1` or `DO_NOT_TRACK=1`. See the [Privacy Policy](../../PRIVACY.md#privacy-policy).

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

The [`plugin/pointframe`](../../plugin/pointframe) folder is a Claude plugin for Windows. It
starts this MCP server for you and adds three skills: `verify-desktop-work` (prove that a
change to a Windows desktop app works, with a signed report), `create-desktop-verification-skill`
(write a `verify-<app>` skill for a desktop app repository) and `capture-screen`. On
the first start it downloads the pinned release of the server from GitHub, checks its
SHA-256 against `server.lock.json`, and caches it under
`%LOCALAPPDATA%\Pointframe\plugin-mcp`. Its `.mcp.json` runs `scripts/start-mcp.cmd`, which
runs `powershell.exe -NoProfile -ExecutionPolicy Bypass -File start-mcp.ps1` for that one
process only; the
[plugin README](../../plugin/pointframe/README.md) lists exactly what it runs and how to
remove it. Once the plugin is listed in the Claude plugin directory, install it from the
`/plugin` marketplace. To try a local copy, run this from the repository root:

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
[desktop-testing MCP README](../mcp-desktop-testing/README.md), which includes
the operator workflow and evidence limits.

### Build the MCP package locally

Run these commands from the repository root.

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

From the repository root, build the server and run the repository's protocol smoke test:

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

