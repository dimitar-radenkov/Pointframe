# Pointframe desktop-testing MCP

This document describes the opt-in MCP driver used to test Windows desktop
applications through their normal user interface. It is separate from the
product documentation because it is a test and operator workflow, not a
Pointframe end-user feature.

## What it does

The driver can:

- launch an explicitly approved Windows executable normally;
- capture bounded screenshots and optional UI Automation observations;
- focus windows and send bounded clicks, keys, text, drags, and scrolls;
- invoke verified UI Automation elements;
- record action results, checks, reports, and evidence metadata;
- test Pointframe and unrelated approved applications such as Notepad++.

The target is black-boxed. The driver does not inject code, add target-specific
startup flags, redirect Pointframe storage, seed settings, or read Pointframe
private data.

## Scope: general desktop automation, not a Pointframe-only test hook

`focus_window`, `click`, `press_keys`, `drag`, `enter_text`, `scroll`, and
`invoke` do not know or care that a target is Pointframe. Any executable an
operator adds to the policy file becomes something the driver can launch,
observe, and physically drive through its real UI, clicking controls, filling
fields, dragging elements, and invoking UI Automation elements the same way a
person would with a mouse and keyboard.

This means the driver is not limited to testing Pointframe itself. Once a
policy lists an application, whether Pointframe, Notepad, Notepad++, or another
approved Windows executable, an operator-authorized agent can use it to:

- reproduce a bug report by clicking through the same steps a user described;
- drive a third-party application into a specific state or artifact needed for
  a task;
- exercise any approved application's workflow end-to-end while capturing
  screenshots or recordings as evidence.

The policy allowlist, worker supervision, and physical-input preflight checks
described below exist precisely because this is real desktop control rather
than a sandboxed simulation: every executable added to a policy file is an
executable the driver can genuinely operate. That tradeoff is intentional —
broad, useful automation capability, gated behind an explicit, narrow,
operator-approved allowlist rather than left open by default.

## Safety model

Desktop testing is disabled by default. It is enabled only when the MCP process
is started with both:

```text
--desktop-testing --desktop-policy <absolute-policy-path>
```

The policy must list each executable, its ordinary arguments, its working
directory, allowed actions, permitted shell surfaces, and evidence directory.
Unknown fields, relative paths, missing executables, reparse points, target
data-root overrides, and unapproved actions are rejected.

A dedicated Windows account or machine is preferred. A personal account is
allowed only when the operator explicitly accepts that the run may:

- change normal application settings;
- change desktop focus and state;
- read clipboard data through the target workflow;
- create screenshots, recordings, OCR output, and evidence files.

Do not use the driver while unrelated desktop input is being performed.

## Architecture

```text
MCP client
    |
    | stdio JSON-RPC
    v
Pointframe.Mcp parent
    |
    | current-user named pipe
    v
same-executable worker
    |
    | UIA3 / user32 SendInput
    v
approved desktop target
```

The parent process supervises the worker and verifies its protocol version,
process ID, Windows session, and user identity. The worker performs native
input and UI Automation actions on one controlled execution path.

Physical actions require a fresh observation or an explicitly validated target.
Click coordinates are converted from observation-local coordinates to physical
virtual-desktop pixels. Before `SendInput`, the worker checks the process
generation, foreground state, window visibility, target bounds, click/key
limits, and cancellation state. Failed preflight sends no native input.

An action that may have been delivered but timed out is reported as unknown and
is never replayed automatically. Action IDs are idempotent when their
arguments match and produce a conflict when reused with different arguments.

## Available opt-in tools

When the policy-enabled server is running, the additional desktop tools are:

| Tool | Purpose |
|---|---|
| `list_apps` | List policy-eligible application candidates |
| `start_test_session` | Launch one approved executable |
| `restart_app` | Restart only after the previous target exited normally |
| `observe_app` | Capture bounded images and optional UIA data |
| `focus_window` | Focus a verified target window |
| `click` | Send a bounded physical click from an observation point |
| `press_keys` | Send bounded physical keys or an approved global hotkey |
| `drag` | Send a bounded drag |
| `enter_text` | Send physical Unicode text or verified ValuePattern text |
| `invoke` | Invoke a verified UI Automation element |
| `check_ui` | Evaluate bounded UI conditions |
| `scroll` | Send bounded mouse-wheel input |
| `get_action_result` | Read an action result |
| `get_test_report` | Finalize the session report |
| `end_test_session` | Release the driver session without force-killing targets |

The normal, disabled server continues to expose only the delivered capture and
recording tools:

```text
list_displays
capture_monitor
read_text_from_monitor
start_recording
stop_recording
get_recording_status
```

## Local validation

Build and publish the MCP executable:

```powershell
dotnet publish Pointframe.Mcp\Pointframe.Mcp.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -o Pointframe.Mcp\bin\publish\win-x64
```

Run the safe headless discovery check:

```powershell
pwsh .\packaging\test-mcp-stdio.ps1 `
  -ExecutablePath .\Pointframe.Mcp\bin\publish\win-x64\Pointframe.Mcp.exe
```

The repository test harness can generate a temporary policy and verify enabled
tool registration without launching Pointframe:

```powershell
$env:POINTFRAME_EXECUTABLE = (Resolve-Path `
  'Pointframe\bin\publish\win-x64\Pointframe.exe').Path
$env:POINTFRAME_MCP_EXECUTABLE = (Resolve-Path `
  'Pointframe.Mcp\bin\publish\win-x64\Pointframe.Mcp.exe').Path
$env:POINTFRAME_MCP_TEST_OUTPUT_DIRECTORY = Join-Path $env:TEMP `
  'PointframeDesktopGateEvidence'

dotnet test Pointframe.AutomationTests\Pointframe.AutomationTests.csproj `
  --filter 'FullyQualifiedName~DesktopGatePolicyFactoryTests'
```

This check proves policy loading and conditional tool registration only. It is
not evidence that a desktop gate passed.

## Gate workflow

The planned interactive gates are:

1. **Gate A:** launch Pointframe normally, use the tray and Settings UI,
   verify persistence, exit through the normal UI, and verify restart behavior.
2. **Gate B:** capture, annotate, undo/redo, save or copy, and verify the
   resulting image with the image oracle.
3. **Gate C:** record, pause/resume, annotate while recording, pin output,
   exercise OCR/scrolling, and test an approved external application.

The gates require retained evidence and a normal cleanup path. Headless unit
tests, MCP tool discovery, or a scaffold that only checks environment variables
must not be reported as a passed gate. The current gate entries remain
incomplete until the real workflows and their external oracles are executed.

## Related documentation

- [Detailed operator and evidence notes](../mcp-desktop-testing.md)
- [Desktop-testing implementation plan](../../plan/feature-mcp-desktop-testing-2.md)
- [Knowledge-base MCP subsystem entry](../knowledge-base/knowledge-base.md#standalone-cli-and-mcp-automation)
- [Automation workflow](../../.github/workflows/desktop-automation.yml)

