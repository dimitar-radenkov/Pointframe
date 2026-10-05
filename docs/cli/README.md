# Pointframe CLI

`Pointframe.Cli.exe` is a self-contained Windows command-line tool for
discovering monitors and windows, capturing a monitor, a monitor sub-region,
or a single window as PNG, running Windows OCR against those captures, and
recording a whole monitor to MP4. It uses
`Pointframe.Engine` directly and does not start the Pointframe tray
application or any WPF window.

To make an agent finish only on a passing check in your own .NET or Node project,
see [Make your agent finish only on a passing check](verify.md).

## Requirements

- Windows x64
- An interactive, unlocked Windows desktop session for `capture`, `ocr`,
  `record`, and desktop verification scenarios. `verify` gates run headless,
  including in CI; see [the verify guide](verify.md)
- No .NET runtime or .NET SDK when using the published ZIP
- `ffmpeg.exe` on `PATH`, set via `POINTFRAME_FFMPEG_PATH`, or bundled next to
  `Pointframe.Cli.exe` — required only for the `record` command

The desktop commands cannot capture a user's desktop from a Windows service or
session 0. Run them as the same interactive user who owns the desktop being
inspected.

## Install

Download `Pointframe.Cli-<version>-win-x64.zip` from the
[latest Pointframe release](https://github.com/dimitar-radenkov/Pointframe/releases/latest)
and extract it to a directory. The ZIP is self-contained and includes the
single-file executable and its native dependencies. From the extracted ZIP
folder, run `.\Pointframe.Cli.exe install` once to copy it to
`%LOCALAPPDATA%\Programs\Pointframe.Cli` and add that directory to your user
`Path`. Open a new terminal or restart your agent afterwards, then check with
`pointframe --version`. The [verify guide](verify.md#install) lists the checksum
step in full. Scoop is not a supported install channel. The examples below call
`.\Pointframe.Cli.exe`; after the install, call `pointframe`.

The winget package `DimitarRadenkov.Pointframe.Cli` is awaiting acceptance in
winget-pkgs. Once `winget search DimitarRadenkov.Pointframe.Cli` finds it,
`winget install DimitarRadenkov.Pointframe.Cli` replaces the manual steps above
(winget adds the `pointframe` command to `PATH` itself) and
`winget upgrade DimitarRadenkov.Pointframe.Cli` updates it. That package is the CLI
only; the desktop app is the separate `DimitarRadenkov.Pointframe` package.

For source builds, use:

```powershell
pwsh .\packaging\build-cli-package.ps1 -Version 1.0.0 -FfmpegPath 'C:\path\to\ffmpeg.exe'
```

`-FfmpegPath` is optional; omit it to build a package without a bundled
`ffmpeg.exe` (the `record` command then relies on `PATH` or
`POINTFRAME_FFMPEG_PATH` on the target machine).

The script writes the ZIP and SHA-256 file under
`packaging\output\Pointframe.Cli-<version>-win-x64`.

## Install and verify the MCP server for VS Code

The CLI can download the latest published Pointframe MCP bundle, verify its
SHA-256 checksum, install it under a versioned directory in
`%LOCALAPPDATA%\Programs\Pointframe.Mcp`, and add the `pointframe` server to VS
Code's user-level `mcp.json`:

```powershell
pointframe mcp install --client vscode
pointframe mcp status --client vscode
pointframe mcp doctor --client vscode
```

`mcp install` backs up an existing VS Code MCP configuration to
`mcp.json.pointframe.bak` before its first change and preserves unrelated
servers and top-level settings. Existing JSON comments and trailing commas are
accepted, although the rewritten file is normalized as JSON. Repeating the
command is safe: the versioned package is reused and the original backup is not
overwritten.

Use `--dry-run` to download, checksum, and inspect the package and configuration
without persisting the installation or changing VS Code:

```powershell
pointframe mcp install --client vscode --dry-run
```

`mcp status` checks the CLI-managed installation record and VS Code command
path. `mcp doctor` additionally starts the installed server over stdio, performs
the MCP initialize handshake, and requires the exact released direct-tool set.
All three commands write a single-line JSON response and use exit code `0` for
success, `1` for an installation/configuration/health failure, and `2` for
invalid arguments. Desktop-testing tools remain disabled; these commands never
create or enable a desktop-testing policy.

The first supported managed client is `vscode`. Claude Code, Cursor, update,
and uninstall adapters remain manual workflows for now.

### Serve interactive desktop tools for a project's app

`pointframe mcp serve [--project <dir>] [--mcp <file>]` is the command a
committed agent configuration runs (`pointframe verify setup` writes it for
you; see [Interactive desktop tools for your agent](verify.md#interactive-desktop-tools-for-your-agent)).
It finds the project (`--project`, otherwise the nearest folder above the
current directory that holds `.pointframe\verify.json`), loads the spec, and
starts `Pointframe.Mcp.exe --desktop-testing --desktop-policy <file>` with the
caller's own stdin, stdout, and stderr, so the agent talks to the server
directly. The policy lists exactly the spec's `app` and is written to
`%LOCALAPPDATA%\Pointframe\verify\projects\<16 hex>\`, outside the repository.

It starts only under the same approval as `verify run`: the spec's commands must
already be trusted, approved by policy (standard commands), or approved by the
approver agent. `mcp serve` never asks a person at a terminal. Standard output is
reserved for the server; every refusal is one line on standard error with a stable
code and exit code `1`: `spec_untrusted`, `approver_unavailable`, `spec_invalid`,
`no_app`, `app_outside_project` (the app or its working folder resolves through a
link to a place outside the project), `mcp_not_found`, or `mcp_start_failed`.
When the spec's `app` declares `isolation`, the app starts on a fresh data folder
that is deleted when the server exits; without it the app uses its normal data.

## Commands

Every long option below also accepts a short alias: `-m` for `--monitor`,
`-w` for `--window-id`, `-g` for `--region`, `-s` for `--seconds`, `-f` for
`--fps`, `-r` for `--redact`, and `-o` for `--output`. Every long option also accepts an inline
value, e.g. `--monitor=\\.\DISPLAY1` instead of `--monitor \\.\DISPLAY1`.

### Choosing where the artifact is written

Every command that produces a file — `capture`, `ocr`, `capture-window`,
`ocr-window`, and `record` — accepts `--output <file>` (`-o`) to name the exact
file to write, instead of letting Pointframe generate a timestamped name:

```powershell
.\Pointframe.Cli.exe capture --monitor '\\.\DISPLAY1' --output .\shot.png
.\Pointframe.Cli.exe record --monitor '\\.\DISPLAY1' --seconds 5 -o .\clips\take1.mp4
```

The value is a file path, not a directory: missing parent directories are
created, an existing file is overwritten, and passing the path of an existing
directory is rejected as a runtime error. The metadata sidecar is written next
to the file. When `--output` is omitted, artifacts keep their previous behavior
and land under `%LOCALAPPDATA%\Pointframe` with a generated name.

### Discover monitors

```powershell
.\Pointframe.Cli.exe displays
```

Use the exact `monitorName` returned by this command. A typical name is
`\\.\DISPLAY1`, but the available names depend on the current Windows session.
The response is JSON containing monitor identifiers, physical pixel bounds, and
DPI scale information.

### List windows

```powershell
.\Pointframe.Cli.exe windows
```

Lists visible top-level windows as JSON. Each entry includes `Hwnd` (the
window handle), `Title`, `ProcessName`, `ProcessId`, `BoundsPixels` in
absolute physical screen pixels, `MonitorName` (the containing monitor's
device name, if the window fits on one monitor), and `IsMinimized`. Pointframe's
own process windows are excluded.

Window handles are session-local and temporary. Always call `windows` to get
current handles before calling `capture-window` or `ocr-window`.

### Capture a window

```powershell
.\Pointframe.Cli.exe capture-window --window-id 12345678
```

Captures the visible screen rectangle of the specified window and saves it as a
PNG. The `--window-id` (`-w`) value is the `Hwnd` returned by the `windows`
command and must be a positive integer.

This is a screen-rectangle capture: if the target window is partially covered
by another window, the occluding content will appear in the capture. Minimized,
zero-size, off-screen, and multi-monitor-spanning windows are rejected with a
runtime error (exit code `1`).

### Capture a window and run OCR

```powershell
.\Pointframe.Cli.exe ocr-window --window-id 12345678
```

Same as `capture-window`, but also runs Windows OCR against the captured image.
The JSON response includes `RecognizedText` (`null` when no text is found or no
OCR language pack is installed).

### Capture a monitor

```powershell
.\Pointframe.Cli.exe capture --monitor '\\.\DISPLAY1'
```

The command writes a JSON response to standard output and saves a PNG plus
metadata sidecar beneath:

```text
%LOCALAPPDATA%\Pointframe\Screenshots
```

The metadata identifies the artifact path, byte length, SHA-256, timestamp,
monitor, DPI, and physical capture bounds.

Add `--region <x,y,width,height>` (`-g`) to capture only a sub-rectangle of
the monitor instead of the whole thing. The coordinates are physical pixels
relative to the monitor's own top-left corner (not the virtual desktop), and
width/height must be positive integers:

```powershell
.\Pointframe.Cli.exe capture --monitor '\\.\DISPLAY1' --region 100,100,800,600
```

A region that falls outside the monitor's bounds is rejected with a runtime
error (exit code `1`) rather than being clipped. The response metadata reports
both `MonitorBoundsPixels` (the full monitor) and `CaptureBoundsPixels` (what
was actually captured), so a region capture is distinguishable from a
whole-monitor one.

### Capture and run OCR

```powershell
.\Pointframe.Cli.exe ocr --monitor '\\.\DISPLAY1'
```

OCR uses the same monitor capture as `capture`, then calls Windows OCR for the
current user's installed language profiles. The PNG and metadata sidecar are
still produced. The JSON adds `RecognizedText`; it is `null` when no text is
recognized or no suitable OCR language pack is installed. `ocr` accepts the
same optional `--region <x,y,width,height>` (`-g`) flag as `capture`, so OCR
can be scoped to a sub-region of the monitor.

### Record a monitor

```powershell
.\Pointframe.Cli.exe record --monitor '\\.\DISPLAY1' --seconds 10
```

`record` starts a direct MP4 recording of the whole monitor, waits for the
requested duration (or an earlier Ctrl+C, which stops the recording gracefully
instead of killing the process), stops the recording, and writes a single
combined JSON response containing both the started `Session` and the
finished `Artifact`. The MP4 and its `.events.jsonl` sidecar are saved beneath:

```text
%LOCALAPPDATA%\Pointframe\Recordings
```

Optional flags:

| Flag | Meaning | Default |
|---|---|---|
| `--fps <1-60>` (`-f`) | Capture frame rate | `20` |
| `--redact <x,y,width,height>` (`-r`) | Pixelate a capture-local physical-pixel region; repeatable | none |

Example with a 30 fps capture and two redacted regions:

```powershell
.\Pointframe.Cli.exe record --monitor '\\.\DISPLAY1' --seconds 30 --fps 30 --redact 100,100,200,80 --redact 400,300,150,150
```

`record` is a single blocking command: there is no separate `stop-recording`
command because each CLI invocation is a standalone process with no session
state that could persist across two separate invocations. If a script needs
to start recording and stop it later from a different process, use the MCP
server's `start_recording`/`stop_recording` tools instead — see the
[MCP server README](../mcp-desktop-testing/README.md).

If the recording cannot be started (for example, an unknown monitor name or a
missing `ffmpeg.exe`), the command writes a JSON response with
`"Success": false` and an `Error` object to standard output and exits with
code `1`.

## Verify a desktop app from a spec

To adopt verify in another project, start with the [verify guide](verify.md). To require a pass on pull requests, see [Run in CI](verify.md#run-in-ci). Every field of the spec file is in the [spec reference](verify-spec.md).

`verify run` checks a project against a verification spec that the project keeps
in git. It runs the spec's gates (build, test, or any command), then launches the
built app and runs each desktop scenario in the real UI, and writes a verdict with
a signed proof bundle for each scenario. No agent drives the app: the spec says
what to do and what must hold.

```powershell
pointframe verify run
pointframe verify run --spec .pointframe\verify.json --mcp C:\tools\Pointframe.Mcp.exe
pointframe verify run --scenario save-text
pointframe verify run --only gates
pointframe verify run --task keep-text
```

### Verify Pointframe's real settings window

This repository also keeps `.pointframe/verify-app.json` for the WPF app. Build the app and MCP server
in Release, then run the spec with an installed or copied `pointframe` CLI and that MCP executable:

```powershell
pointframe verify run --spec .pointframe\verify-app.json --mcp .\Pointframe.Mcp\bin\Release\net10.0-windows10.0.18362.0\Pointframe.Mcp.exe
```

The scenario toggles auto-save screenshots, saves, restarts Pointframe, and checks that the value
persisted. Verification gives the app a fresh settings directory and removes it after the scenario,
so it does not read or change the user's normal settings. A desktop run takes over the mouse and
keyboard until it finishes; close editor-started `Pointframe.Mcp.exe` processes first.

| Flag | Meaning | Default |
|---|---|---|
| `--spec <file>` | The verification spec | `.pointframe\verify.json` in the current folder |
| `--mcp <file>` | The `Pointframe.Mcp.exe` that drives the app | `POINTFRAME_MCP_EXECUTABLE`, then the server installed by `mcp install` |
| `--scenario <id>` | Run one scenario only | every scenario |
| `--only <gates\|scenarios>` | Run only the gates, or only the scenarios | both |
| `--task <id>` | Also run the frozen criteria of a task (see [Freeze a task's criteria](#freeze-a-tasks-criteria-before-work-starts)) | none |

### Initialize verification for a project

Run `pointframe verify init` from the project root. It detects root .NET solutions and test projects, or
the `build`, `lint`, and `test` scripts in a root `package.json`, then writes a loader-validated
`.pointframe/verify.json`. A single .NET `WinExe` project is used as the app candidate; pass `--app`
when there are multiple candidates or the executable lives elsewhere. Without `--explore`, add a
scenario after the app is built, or run init again with `--explore` to inspect it once through the
Pointframe MCP server and seed an `app-starts` check.

```powershell
pointframe verify init
pointframe verify init --app bin\Release\net10.0-windows\MyApp.exe --explore --agents-md
pointframe verify init --app bin\Release\net10.0-windows\MyApp.exe --explore --mcp path\to\Pointframe.Mcp.exe
pointframe verify init --hooks claude
pointframe verify init --hooks none
pointframe verify init --force
```

| Flag | Meaning | Default |
|---|---|---|
| `--app <path>` | App executable relative to the project root | detected when exactly one .NET `WinExe` project exists |
| `--hooks <claude\|codex\|both\|none>` | Merge Stop hooks into `.claude/settings.json` and/or `.codex/hooks.json` | `both` |
| `--agents-md` | Add instructions telling agents to run verification and preserve frozen criteria | off |
| `--explore` | Launch the built app once and create a starter scenario from its first automation id | off |
| `--mcp <file>` | Pointframe MCP server executable to use for `--explore` | `POINTFRAME_MCP_EXECUTABLE`, then the CLI-installed server |
| `--force` | Replace an existing `.pointframe/verify.json` | off |

The generated hook runs `pointframe verify hook stop --review`; keep `pointframe` on `PATH` so
Codex on Windows can launch the unquoted command. Existing hook settings are retained, and repeating
init does not duplicate the hook or the `AGENTS.md` section. The initializer adds
`artifacts/pointframe-verify/` to an existing `.gitignore` when it is not already covered. If no gates
or explored scenario can be created, it reports what is missing and writes nothing. An existing spec
is left untouched unless `--force` is supplied.

After initialization, run `pointframe verify run`; standard commands are approved by policy offline. Use
`pointframe verify trust` if the approver refuses nonstandard commands or no approver is available.

`--explore` launches the app, which is a command the spec declares, so it needs the same approval as a
run: init checks trust on the generated spec first. When approval is not obtained it skips exploring,
warns with the code (`spec_untrusted` or `approver_unavailable`), and points to `pointframe verify trust`.

### Give your agent interactive desktop tools

`pointframe verify setup [--client claude-code|codex|vscode|all] [--spec <file>] [--mcp <file>]`
turns a project that already has `.pointframe\verify.json` with an `app` into one an agent can drive
interactively, with no hand-written files:

```powershell
pointframe verify setup
pointframe verify setup --client all
```

It approves the spec through the same pipeline as `verify run` (standard commands are approved by policy
offline), installs the Pointframe MCP server with the checksum-verified `mcp install` installer only when
none resolves (it never touches VS Code's user configuration), and merges project-scoped agent
configuration that runs `pointframe mcp serve`: `.mcp.json` for Claude Code (the default),
`.codex\config.toml` for Codex, `.vscode\mcp.json` for VS Code, or all three. Other servers and settings
are kept, the first rewrite of a file leaves a `.pointframe.bak` copy, and a second run reports `unchanged`.
JSON comments and trailing commas are accepted but not kept when a file is rewritten; the Codex file is
edited as text, so its comments and other tables stay. It finishes with a smoke check: it starts
`pointframe mcp serve` for the project, requires `desktop_start_test_session` in `tools/list` and the spec's
app id in `desktop_list_apps`, and prints one JSON line with `status`, `filesWritten`, `filesUnchanged`,
`clients`, `profileId`, `serverPath`, `serverVersion`, `tools`, `warnings`, and a `nextStep` (restart the
agent session). Failures carry a `code`; the configuration files stay written. `pointframe.exe` must be on
`PATH` (`pointframe_not_on_path`).

| Flag | Meaning | Default |
|---|---|---|
| `--client <claude-code\|codex\|vscode\|all>` | Agent configuration to write | `claude-code` |
| `--spec <file>` | The verification spec | `.pointframe\verify.json` in the current folder |
| `--mcp <file>` | The `Pointframe.Mcp.exe` to use for the smoke check and installation check | `POINTFRAME_MCP_EXECUTABLE`, then the CLI-installed server |

A minimal spec:

```json
{
  "schemaVersion": 1,
  "app": { "id": "my-app", "executable": "MyApp/bin/Release/net10.0-windows/MyApp.exe" },
  "scenarios": [
    {
      "id": "save-text",
      "criteria": ["The text box shows the typed text."],
      "steps": [
        { "enterText": { "automationId": "textBox", "text": "hello" } },
        { "invoke": { "automationId": "saveButton" } },
        { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "hello", "criterion": "C1" } },
        { "check": { "kind": "textEquals", "automationId": "textBox", "expected": "not-hello", "expectFailure": true, "timeoutSeconds": 1 } }
      ]
    }
  ]
}
```

Paths are relative to the folder that holds `.pointframe\`. `app` also takes
`arguments` and `workingDirectory` (default: the executable's folder). A spec
needs at least one gate or one scenario; `app` is required only with scenarios.

### Gates

```json
"gates": [
  { "id": "build", "run": "dotnet build -c Release" },
  { "id": "tests", "run": "dotnet test -c Release --no-build", "timeoutMinutes": 20 }
]
```

Each gate is a command line run through `cmd.exe` in the project folder (or its
`workingDirectory`), with a timeout of `timeoutMinutes` (1-120, default 30). Every
gate runs, in order, even after one fails, so one run reports every problem. Its
output goes to a log, and a failed gate's `details` show compiler errors, failed
tests, and `ERROR` lines, or the end of the log. When any gate fails, the
scenarios are reported `skipped` and not run.

Gate commands and the app run with your permissions, so a spec runs only after
what it starts is approved: every gate command and the app's executable,
arguments, working folder, and isolation. Scenario steps and criteria are not
commands and need no approval. Approval covers the command text; it does not
approve or assess what the project's code does when that command runs it. When
the commands are new or changed (including a removed gate), `verify run` and
`verify task start` approve them in this order:

1. **Fixed rules, no agent.** The app must be a program inside the project folder,
   not a shell or interpreter (`cmd.exe`, `powershell.exe`, `python.exe`, ...);
   every working folder must be inside the project; and no command or app
   argument may contain a URL, a network share, a download tool (`curl`, `wget`,
   `iwr`, ...), a delete (`del`, `rm`, `Remove-Item`, ...), an encoded or inline
   payload (`-EncodedCommand`, `iex`, `base64`), a system change (`reg`,
   `schtasks`, `sc`, `net`, ...), or `$env:` and profile variables. A violation is
   refused at once.
2. **Policy for standard commands.** These command forms are approved offline,
   with `provenance.commandsApprovedBy` set to `policy`:

   | Program | Standard form |
   |---|---|
   | `dotnet` | `build`, `test`, `format`, or `restore`, an optional relative `.sln`, `.slnx`, `.csproj`, or `.fsproj` path, and the supported configuration, verbosity, build, restore, nologo, format, framework, or filter options |
   | `npm`, `pnpm`, `yarn` | `<manager> test`, `npm ci`, frozen-lockfile install for pnpm/yarn, or `<manager> run <script>` |
   | `pwsh`, `powershell` | optional `-NoProfile`/`-NonInteractive`, then `-File <relative-project-script.ps1>` and safe arguments |

   Commands must parse exactly into these forms; shell metacharacters, absolute
   or parent paths, unknown options, and other command shapes are not standard.
3. **The approver agent.** A separate run of the configured agent (see
   [Choose the agent](#choose-the-agent-claude-code-codex-or-your-own); up to USD
   0.5 on Claude Code) with no MCP tools and no project settings, started by the CLI, sees only the commands and
   the previously approved ones. It approves ordinary build, test, format, and
   check commands and the project's own app, treats the commands as untrusted
   data, and refuses when in doubt. Its approval is stored with its reason, so it
   is asked once per change of the commands. The verdict's
   `provenance.commandsApprovedBy` says who approved (`agent:claude-code` or
   `person`).
4. **A person**, when fixed rules refuse, the agent refuses, or the approver is
   unavailable:

```powershell
pointframe verify trust            # shows the commands and asks you to type yes
pointframe verify trust --revoke   # withdraws the approval
```

If fixed rules or the agent refuse, `verify run` and `verify task start` fail
with `errorCode` `spec_untrusted`. If commands are nonstandard and the approver
has no runner or cannot run, they fail with `approver_unavailable`, including
the cause and next steps. Both codes are person-only in the Stop hook. Approval
is stored per project under `%LOCALAPPDATA%\Pointframe\verify`, outside the repository.
`verify trust` asks at an interactive terminal only, with no flag that skips the
question. This is a consent check, not a sandbox: the approver agent can be
wrong, and the fixed rules are its backstop.

### Isolation

```json
"app": { "id": "my-app", "executable": "...", "isolation": { "environmentVariable": "MYAPP_DATA_DIR" } }
```

With `isolation`, every scenario gets a fresh, empty data folder, and the app
learns its path from one environment variable (`environmentVariable`) or from one
argument added after its other arguments (`"argument": "--data-dir"`). The folder
stays the same across a `restart` inside the scenario, so you can check what
survives a restart, and it is deleted when the scenario ends. A folder that
cannot be deleted fails the scenario. The spec never names the folder.

Steps, each an object with exactly one property:

| Step | Does | Takes |
|---|---|---|
| `enterText` | Sets an edit field's value through UI Automation | element, `text` |
| `invoke` | Activates a button or menu item | element |
| `pressKeys` | Focuses the app's window and presses a chord | `keys`: 1 to 4 virtual-key codes, modifiers first |
| `restart` | Relaunches the app in the same session. Close it first with an `invoke` of its own Close or Exit control | nothing |
| `check` | Waits until a condition holds | `kind`, element, `expected`, `criterion` or `expectFailure`, `timeoutSeconds` (1-30, default 10) |

An element is `automationId`, or both `role` and `name`. Check kinds are
`exists`, `absent`, `enabled`, `toggleEquals`, `selectionEquals`, and
`textEquals`. Criteria are numbered `C1`, `C2`, ... in order. The spec is
rejected before anything runs when a criterion has no check that names it, when
a scenario with criteria has no negative control (`"expectFailure": true`), or
when a property or step is unknown.

Each scenario runs in its own MCP server process and desktop test session. Before an action, the runner
waits up to 15 seconds for the element to appear, so a window that is still
opening is not a failure. The first failed step stops the scenario and marks the
rest `skipped`. The session's signed report is always fetched, its proof is
checked from disk, and the app is closed. A scenario passes only when every step
passed, the report's verdict is `passed`, the proof verifies, and cleanup
succeeded.

The verdict is written to `artifacts\pointframe-verify\verdict.json` under the
project folder and also printed to standard output. Each scenario's proof bundle
(`report.json`, `evidence\`, `index.html`) and each gate's log are under
`artifacts\pointframe-verify\runs\<time>\`. A failed check reports what it found:

```json
{ "index": 2, "kind": "check", "status": "fail", "code": "CheckFailed", "expected": "hello", "actual": "" }
```

The verdict's `provenance` ties it to its inputs: the git `head`, the working
`treeHash` (the same hash as `scripts\verify.ps1`, covering uncommitted files),
the CLI version, and the path and SHA-256 of the MCP server. `specSha256` is the
spec file's hash.

| `status` | Meaning | Exit code |
|---|---|---:|
| `pass` | Every gate and every scenario passed | `0` |
| `partial` | Everything that ran passed, but `--scenario` or `--only` left something out | `0` |
| `fail` | A gate or a scenario failed, or the run could not start (`errorCode` says why: `spec_untrusted`, `approver_unavailable`, `app_not_found`, `mcp_not_found`, `desktop_busy`) | `1` |
| `fail` with `errorCode` `spec_invalid`, `scenario_not_found`, `task_not_found`, or `task_invalid` | The spec or the arguments are wrong; no verdict file is written | `2` |

Read `status`, not only the exit code: `partial` exits `0` but is not a final
verdict. `verify status` answers the question a hook asks, "is the last verdict a
`pass` for the files as they are now?":

```powershell
pointframe verify status
```

It prints the last verdict's `status`, its tree hash and the current one, and
`fresh`, which is true only for a `pass` whose tree hash equals the current one.
It exits `0` when `fresh` is true and `1` otherwise.

### Freeze a task's criteria before work starts

```powershell
pointframe verify task start tasks\keep-text.md
pointframe verify run --task keep-text
```

`verify task start <task-file>` gives the task to an examiner agent before any
code is written. The examiner is a separate run of the configured agent (see
[Choose the agent](#choose-the-agent-claude-code-codex-or-your-own); up to USD 3 on
Claude Code) that sees only the task text and the running app: it runs in an
empty folder outside the repository, with only the Pointframe MCP server, and
without the project's settings or hooks. It explores
the app and proposes criteria, a scenario with a negative control, and the
automation ids that new elements must have (`requiredAutomationIds`).

The CLI does not trust the proposal. It parses it with the same rules as the
spec, then runs it on the unchanged app: every criterion must fail there or not
be reachable yet (an element the task adds), no criterion may already hold, and
every negative control that ran must pass. A proposal that breaks a rule is
rejected (`examiner_invalid` or `fail_before_rejected`) and nothing is frozen.

A confirmed proposal is frozen as a task snapshot under
`%LOCALAPPDATA%\Pointframe\verify`, outside the repository, with the task text,
the tree hash, and a hash of each gate and scenario at that moment. The task id
is the file name, or `--id <id>`. A task that already has a snapshot is not
replaced unless you pass `--replace` and approve it at an interactive terminal.

`verify task start` also makes the task the project's active task, which the
Stop hook below verifies without being told its id.

`verify run --task <id>` runs the frozen scenario as `task-<id>` next to the
spec's scenarios. The verdict's `task` block names the snapshot's hash and lists
`specChanges`, such as `scenario 'save-text' changed`, when the spec changed
since the task started, so a reviewer sees it.

### Make an agent finish only on a pass (Stop hook)

`verify hook stop` is a Claude Code Stop hook. Add it to the project's
`.claude/settings.json`; give it a long timeout, because Claude Code stops a Stop
hook after 30 seconds unless you set one:

```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          { "type": "command", "command": "pointframe verify hook stop --review", "timeout": 1800, "statusMessage": "Verifying the work..." }
        ]
      }
    ]
  }
}
```

When the agent tries to finish, the hook decides:

| Situation | What the hook does |
|---|---|
| The project has no `.pointframe\verify.json` | Nothing; the agent stops |
| The last verdict is a `pass` for these exact files, spec, and active task | Lets the agent stop without running anything again |
| Anything changed | Runs `verify run` (with the active task), then decides on the new verdict |
| `pass` | Lets the agent stop and tells you, with the reviewer's flags when `--review` is set |
| A gate, a scenario, or the task fails | Blocks the stop and tells the agent what failed (gate errors, `Expected ..., found ...`) |
| Only a person can fix it (`spec_untrusted`, `approver_unavailable`, `spec_invalid`, `mcp_not_found`, `desktop_busy`, a broken task) | Lets the agent stop and tells you that the work was not verified and why, because blocking would only loop |
| The agent was blocked `--max-blocks` times in this session (default 5) | Lets it stop and tells you it still fails |

With `--review`, the first pass on a tree the reviewer has not seen (whether the hook or the agent ran it) is followed by a reviewer agent: a separate run of the configured agent
(up to USD 1 on Claude Code) with no MCP tools, which reads the task, the diff since the
task started, and the verdict, and flags weakened tests, edits to the spec or
hooks, and work outside the task. Its flags are written to
`artifacts\pointframe-verify\review.json` and shown to you; they never block.

**Codex** uses the same hook: put the same `Stop` entry in the project's
`.codex\hooks.json`. Codex sends the same `session_id` and `cwd` and accepts the
same `decision: block` answer, so `verify hook stop` works unchanged. Codex runs
project hooks only when the project folder is trusted and the hook is trusted
(both once, in Codex). On Windows, Codex silently skips a hook command written as
a quoted path followed by arguments; use `pointframe verify hook stop` with
`pointframe` on the PATH, or point the command at a small `.cmd` script that
runs the CLI. A .NET worker in Codex's `workspace-write` sandbox also needs
`-c sandbox_workspace_write.network_access=true`, or `dotnet build` cannot
restore packages. An agent
without Stop hooks (Cursor, Copilot, and others) cannot be held back: tell it in
its instructions (`AGENTS.md`) to run `pointframe verify run` and fix failures
before it says it is done, and require a `pass` in CI.

Use an installed `pointframe` (or a copy of the CLI) in the hook, not a build
output the project's own gates rebuild: a running `Pointframe.Cli.exe` locks its
files, and the build gate then fails.

### Choose the agent: Claude Code, Codex, or your own

The examiner, the reviewer, and the approver are roles that any agent can play.
The choice is yours, stored in your profile
(`%LOCALAPPDATA%\Pointframe\verify\agent.json`), not in the project, so an agent
working in the project cannot pick its own judge.

```powershell
pointframe verify agent                 # which agent is used, and is it installed?
pointframe verify agent --use codex     # always use Codex
pointframe verify agent --use claude    # always use Claude Code
pointframe verify agent --use auto      # back to detection: Claude Code, else Codex
```

| Agent | How it runs | Limits |
|---|---|---|
| Claude Code | `claude -p` with the role's instructions as the system prompt, built-in tools off, only the Pointframe MCP server for the examiner, a JSON schema for the answer, and a budget cap | none |
| Codex | `codex --ask-for-approval never exec -` with the instructions leading the prompt, `--output-schema` for the answer, `--sandbox read-only`, `--ephemeral`, and the Pointframe MCP server through `-c mcp_servers...` with its tools approved (`default_tools_approval_mode`) | Codex has no budget flag, and its read-only shell stays available |
| Your own command | Any program that reads a prompt and writes a JSON answer | Its own |

Your own command is set in `agent.json`. The CLI fills in the placeholders, sends
the prompt on stdin as well, and reads the answer from `{output_file}`, or from
standard output when that file stays empty:

```json
{
  "agent": "command",
  "command": "C:\\tools\\my-agent.exe",
  "arguments": ["--prompt-file", "{prompt_file}", "--schema", "{schema_file}", "--out", "{output_file}", "--mcp", "{mcp_config_file}"]
}
```

| Placeholder | Holds |
|---|---|
| `{prompt_file}` | The role's instructions, then the input |
| `{instructions_file}`, `{input_file}` | The two parts on their own |
| `{schema_file}` | The JSON schema the answer must follow |
| `{output_file}` | Where to write the answer |
| `{mcp_config_file}` | An MCP config (`mcpServers` JSON) with the Pointframe desktop tools; only the examiner needs it |
| `{work_dir}` | An empty folder outside the repository |

`verify run` takes over the mouse and keyboard while it runs, and only one run
can use the desktop at a time. Stop the VS Code Pointframe MCP connector first
when the app under test is a build it locks.

## Exit codes and errors

| Exit code | Meaning |
|---:|---|
| `0` | Command completed successfully |
| `1` | Runtime or capture/OCR/recording failure |
| `2` | Invalid or incomplete command-line arguments |

Every command other than `--help`/`--version` writes a single-line JSON response
to standard output, on both the success and the failure path, so a script can
parse standard output the same way regardless of outcome. A runtime failure
writes a `"Success": false` response whose `Error` object carries a stable,
machine-readable `Code` alongside the human-readable `Message`:

| `Error.Code` | Raised when |
|---|---|
| `target_not_found` | The named monitor or window handle does not exist |
| `target_not_capturable` | The window is minimized, zero-size, off-screen, or spans monitors |
| `invalid_region` | A `--region`/`--redact` rectangle is non-positive or outside the monitor |
| `invalid_output_path` | The `--output` value names a directory rather than a file |
| `canceled` | The operation was canceled before it completed |
| `capture_failed` | Any other unexpected runtime failure |

`record` failures the engine reports as a structured error use the recording
response's own codes (such as `monitor_not_found`) in the same `Error` shape.

The human-readable `Pointframe CLI failed: ...` line is still written to standard
error as well, so interactive use is unchanged; only invalid command-line
arguments (exit code `2`) write usage text to standard error *instead of* JSON.

The parser accepts only these forms:

```text
Pointframe.Cli.exe displays
Pointframe.Cli.exe windows
Pointframe.Cli.exe capture --monitor <exact Windows device name> [--region <x,y,width,height>]
Pointframe.Cli.exe ocr --monitor <exact Windows device name> [--region <x,y,width,height>]
Pointframe.Cli.exe capture-window --window-id <window handle>
Pointframe.Cli.exe ocr-window --window-id <window handle>
Pointframe.Cli.exe record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]...
Pointframe.Cli.exe --help
Pointframe.Cli.exe --version
```

Friendly monitor labels, display indexes, or omitted `--monitor`/`--window-id`
values are not accepted.

## Help and version

```powershell
.\Pointframe.Cli.exe --help    # or -h
.\Pointframe.Cli.exe --version # or -v
```

Both accept the flag form (`--help`/`--version`), the short form (`-h`/`-v`),
or a bare `help`/`version` command. Unlike every other command, these write
plain text (not JSON) to standard output and always exit with code `0`.

`--help`/`-h` and `--version`/`-v` take priority over any other arguments on
the command line, so they can be appended to an otherwise invalid or
incomplete command to see usage instead of an error, e.g.
`Pointframe.Cli.exe record --monitor '\\.\DISPLAY1' --help`.

## Artifact verification

For every successful capture or OCR operation:

1. Read the JSON response from standard output.
2. Locate the PNG and `.metadata.json` sidecar in the reported artifact area.
3. Compare the file length and SHA-256 in the sidecar with the actual PNG.
4. Preserve both files together when attaching evidence to a report.

For every successful `record` operation:

1. Read the JSON response from standard output.
2. Locate the MP4 at `artifact.path` and the `.events.jsonl` sidecar at
   `artifact.eventSidecarPath`.
3. Compare the file length and SHA-256 in `Artifact` with the actual MP4.
4. Preserve both files together when attaching evidence to a report.

The CLI writes through the shared direct capture and recording services, so
the metadata is produced alongside the artifact rather than inferred by the
caller.

## Development and testing

Build the project:

```powershell
dotnet build Pointframe.Cli\Pointframe.Cli.csproj
```

Run the CLI from source:

```powershell
dotnet run --project Pointframe.Cli\Pointframe.Cli.csproj -- displays
```

Run focused tests:

```powershell
dotnet test Pointframe.Tests\Pointframe.Tests.csproj `
  --filter "FullyQualifiedName~CliApplication|FullyQualifiedName~CliCommand"
```

The CLI tests cover command parsing, output and error streams, exit codes, and
the direct-capture and direct-recording service contracts. A successful unit
test does not prove that the current machine has an unlocked interactive
desktop or a working `ffmpeg.exe`; use a real `displays`, `capture`, or
`record` invocation for that check.

## Privacy and telemetry

Official release builds of the CLI send one anonymous event per command: the command name (`other` for anything unrecognized), whether it succeeded, a coarse duration bucket, `cli`, and the version. Arguments, paths, output, error text, and identifiers are never sent, `--help` and `--version` send nothing, and source builds send nothing. The first run prints a one-time notice to standard error. Opt out with `POINTFRAME_TELEMETRY_OPTOUT=1` or `DO_NOT_TRACK=1`, or put `{"optOut": true}` in `%LOCALAPPDATA%\Pointframe\agent-telemetry.json`. See the [Privacy Policy](../../README.md#privacy-policy).

## Troubleshooting

### No displays or capture errors

Run the executable in the logged-in interactive session, not as a scheduled
task or Windows service. Confirm that the desktop is unlocked and that the
process is running in the same Windows session as the monitors.

### Monitor name rejected

Run `displays` again and copy the exact `monitorName`. Do not replace it with a
friendly name or an assumed display number.

### OCR returns `null`

The capture may contain no readable text, or Windows may not have an OCR
language pack matching the current user's language profile. The PNG remains
valid and can be inspected independently.

### `record` fails immediately

`record` needs `ffmpeg.exe`. Set `POINTFRAME_FFMPEG_PATH` to its full path,
place `ffmpeg.exe` next to `Pointframe.Cli.exe`, or add it to `PATH`. Rebuild
the package with `-FfmpegPath` to bundle it automatically.

### ZIP or checksum problems

Rebuild with `packaging\build-cli-package.ps1`, ensure the archive and `.sha256`
file come from the same build, and verify the SHA-256 before distribution.

## Related documentation

- [Pointframe product README](../../README.md)
- [MCP server README](../mcp-desktop-testing/README.md)
- [CLI implementation](../../Pointframe.Cli/)
- [CLI packaging script](../../packaging/build-cli-package.ps1)
