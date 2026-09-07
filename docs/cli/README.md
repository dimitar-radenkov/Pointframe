# Pointframe CLI

`Pointframe.Cli.exe` is a self-contained Windows command-line tool for
discovering monitors, capturing a whole monitor as PNG, and running Windows OCR
against a monitor capture. It uses `Pointframe.Engine` directly and does not
start the Pointframe tray application or any WPF window.

## Requirements

- Windows x64
- An interactive, unlocked Windows desktop session
- No .NET runtime or .NET SDK when using the published ZIP

The CLI cannot capture a user's desktop from a Windows service or session 0.
Run it as the same interactive user who owns the desktop being inspected.

## Install

Download `Pointframe.Cli-<version>-win-x64.zip` from the
[latest Pointframe release](https://github.com/dimitar-radenkov/Pointframe/releases/latest)
and extract it to a directory. The ZIP is self-contained and includes the
single-file executable and its native dependencies.

For source builds, use:

```powershell
pwsh .\packaging\build-cli-package.ps1 -Version 1.0.0
```

The script writes the ZIP and SHA-256 file under
`packaging\output\Pointframe.Cli-<version>-win-x64`.

## Commands

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

### Capture and run OCR

```powershell
.\Pointframe.Cli.exe ocr --monitor '\\.\DISPLAY1'
```

OCR uses the same monitor capture as `capture`, then calls Windows OCR for the
current user's installed language profiles. The PNG and metadata sidecar are
still produced. The JSON adds `recognizedText`; it is `null` when no text is
recognized or no suitable OCR language pack is installed.

## Exit codes and errors

| Exit code | Meaning |
|---:|---|
| `0` | Command completed successfully |
| `1` | Runtime or capture/OCR failure |
| `2` | Invalid or incomplete command-line arguments |

Invalid commands print the error and usage to standard error. Runtime failures
print `Pointframe CLI failed: ...` to standard error; successful JSON is written
to standard output.

The parser accepts only these forms:

```text
Pointframe.Cli.exe displays
Pointframe.Cli.exe capture --monitor <exact Windows device name>
Pointframe.Cli.exe ocr --monitor <exact Windows device name>
```

Friendly monitor labels, display indexes, or omitted `--monitor` values are not
accepted.

## Artifact verification

For every successful capture or OCR operation:

1. Read the JSON response from standard output.
2. Locate the PNG and `.metadata.json` sidecar in the reported artifact area.
3. Compare the file length and SHA-256 in the sidecar with the actual PNG.
4. Preserve both files together when attaching evidence to a report.

The CLI writes through the shared direct capture services, so the metadata is
produced alongside the artifact rather than inferred by the caller.

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
the direct-capture service contract. A successful unit test does not prove that
the current machine has an unlocked interactive desktop; use a real `displays`
or `capture` invocation for that check.

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

### ZIP or checksum problems

Rebuild with `packaging\build-cli-package.ps1`, ensure the archive and `.sha256`
file come from the same build, and verify the SHA-256 before distribution.

## Related documentation

- [Pointframe product README](../../README.md)
- [MCP server README](../mcp-desktop-testing/README.md)
- [CLI implementation](../../Pointframe.Cli/)
- [CLI packaging script](../../packaging/build-cli-package.ps1)

