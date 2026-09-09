# Pointframe CLI

`Pointframe.Cli.exe` is a self-contained Windows command-line tool for
discovering monitors, capturing a whole monitor as PNG, running Windows OCR
against a monitor capture, and recording a whole monitor to MP4. It uses
`Pointframe.Engine` directly and does not start the Pointframe tray
application or any WPF window.

## Requirements

- Windows x64
- An interactive, unlocked Windows desktop session
- No .NET runtime or .NET SDK when using the published ZIP
- `ffmpeg.exe` on `PATH`, set via `POINTFRAME_FFMPEG_PATH`, or bundled next to
  `Pointframe.Cli.exe` — required only for the `record` command

The CLI cannot capture a user's desktop from a Windows service or session 0.
Run it as the same interactive user who owns the desktop being inspected.

## Install

Download `Pointframe.Cli-<version>-win-x64.zip` from the
[latest Pointframe release](https://github.com/dimitar-radenkov/Pointframe/releases/latest)
and extract it to a directory. The ZIP is self-contained and includes the
single-file executable and its native dependencies.

For source builds, use:

```powershell
pwsh .\packaging\build-cli-package.ps1 -Version 1.0.0 -FfmpegPath 'C:\path\to\ffmpeg.exe'
```

`-FfmpegPath` is optional; omit it to build a package without a bundled
`ffmpeg.exe` (the `record` command then relies on `PATH` or
`POINTFRAME_FFMPEG_PATH` on the target machine).

The script writes the ZIP and SHA-256 file under
`packaging\output\Pointframe.Cli-<version>-win-x64`.

## Commands

Every long option below also accepts a short alias: `-m` for `--monitor`,
`-g` for `--region`, `-s` for `--seconds`, `-f` for `--fps`, and `-r` for
`--redact`. Every long option also accepts an inline value, e.g.
`--monitor=\\.\DISPLAY1` instead of `--monitor \\.\DISPLAY1`.

### Discover monitors

```powershell
.\Pointframe.Cli.exe displays
```

Use the exact `monitorName` returned by this command. A typical name is
`\\.\DISPLAY1`, but the available names depend on the current Windows session.
The response is JSON containing monitor identifiers, physical pixel bounds, and
DPI scale information.

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

## Exit codes and errors

| Exit code | Meaning |
|---:|---|
| `0` | Command completed successfully |
| `1` | Runtime or capture/OCR/recording failure |
| `2` | Invalid or incomplete command-line arguments |

Invalid commands print the error and usage to standard error. Runtime failures
print `Pointframe CLI failed: ...` to standard error; successful JSON is written
to standard output. `record` failures that the engine reports as a structured
error (rather than an exception) print a `"Success": false` JSON response to
standard output instead, so scripts can parse the failure the same way as a
success.

The parser accepts only these forms:

```text
Pointframe.Cli.exe displays
Pointframe.Cli.exe capture --monitor <exact Windows device name> [--region <x,y,width,height>]
Pointframe.Cli.exe ocr --monitor <exact Windows device name> [--region <x,y,width,height>]
Pointframe.Cli.exe record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]...
Pointframe.Cli.exe --help
Pointframe.Cli.exe --version
```

Friendly monitor labels, display indexes, or omitted `--monitor` values are not
accepted.

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
