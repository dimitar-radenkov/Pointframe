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

`desktop_focus_window`, `desktop_click`, `desktop_press_keys`, `desktop_drag`, `desktop_enter_text`, `desktop_scroll`, and
`desktop_invoke` do not know or care that a target is Pointframe. Any executable an
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

Every tool in this set carries a `desktop_` prefix. The actions they perform —
clicking, dragging, scrolling, typing — have generic names that would otherwise
collide with tools from other MCP servers connected to the same client, leaving
the caller with two indistinguishable `click` tools.

| Tool | Purpose |
|---|---|
| `desktop_list_apps` | List the policy's profiles (`id`, executable file name, allowed action count); never lists running processes or attach candidates |
| `desktop_start_test_session` | Launch one approved executable |
| `desktop_restart_app` | Restart only after the previous target exited normally |
| `desktop_observe_app` | Capture bounded images and optional UIA data |
| `desktop_focus_window` | Focus a verified target window |
| `desktop_click` | Send a bounded physical click from an observation point |
| `desktop_press_keys` | Send bounded physical keys to an observed foreground window (`windowRef` and/or `observationRef`, consumed) or an approved global hotkey |
| `desktop_drag` | Send a bounded drag |
| `desktop_enter_text` | Send physical Unicode text or verified ValuePattern text |
| `desktop_invoke` | Physically click the center of an observed on-screen element after foregrounding its window; it moves the mouse and requires an unobstructed target. If bounds are unusable or click preflight cannot target it, fall back to the UI Automation invoke/toggle pattern. A pattern invoke that opens a modal dialog leaves the app unreadable to UI Automation until the dialog closes. The response reports `method: "click"` or `method: "pattern"`. |
| `desktop_check_ui` | Evaluate bounded UI conditions |
| `desktop_scroll` | Send bounded mouse-wheel input |
| `desktop_get_action_result` | Read an action result |
| `desktop_get_test_report` | Finalize the signed session report and write its proof bundle |
| `desktop_replay_checks` | Re-run a signed report's checks in a fresh session and compare verdicts |
| `desktop_end_test_session` | Ask the target to close normally, then terminate its session-launched process tree if it does not exit |
| `desktop_export_scenario` | Export the session's portable actions and evaluated checks to a verify.json scenario |

### For agents: verify your own work

An agent that changed a desktop app can verify the change in the running app
and return a signed proof. The server ships the steps as the MCP resource
`pointframe://guides/verify-desktop-work` (source:
`Pointframe.Mcp/Guides/verify-desktop-work.md`), and its connect-time
instructions tell every client to read it before calling a `desktop_` tool.

### Session report verdict

The server, not the agent, writes the session report: every action it ran and
every check `desktop_check_ui` evaluated. `desktop_get_test_report` returns:

- `failed` when any check failed, or any dispatched action failed verification.
- `inconclusive` when no check ran, a check could not read the UI state, an
  action's dispatch was partial or unknown, or a declared criterion is not
  covered by a passing check.
- `passed` otherwise.

Actions rejected before dispatch (for example a stale observation) do not
affect the verdict, so they can be retried. A failed check always fails the
report, so explore with `desktop_observe_app`, not `desktop_check_ui`.

To judge a session against goals written before the work starts, pass
`criteria` to `desktop_start_test_session`. They are numbered `C1`, `C2`, ...,
hashed (`criteriaSha256`), and frozen for the session. Name the criterion a
check supports with `criterionId` on `desktop_check_ui`. The report lists a
verdict per criterion: `passed`, `failed`, `inconclusive`, or `uncovered`.

A session with criteria also needs a negative control: a `desktop_check_ui`
call with `expectFailure: true` and a deliberately wrong expectation, such as
`textEquals` with a value the element does not hold. It passes only when the
condition does not hold, which shows the check can tell states apart; without
one, covered criteria still leave the report `inconclusive`. If the wrong
expectation holds, the check fails with `NegativeControlMatched` and so does the
report. A negative control cannot name a `criterionId`, and a short
`timeoutSeconds` is enough because the tool waits for the condition to hold.

### Evidence screenshots

The policy's `evidencePolicy` decides which report entries get a screenshot the
server takes itself: `All` (every check and every action that reached the
app), `Failures` (only entries that are not `passed`, plus actions that failed
or whose dispatch is uncertain), or `None`. Images are full-resolution PNGs in
`<artifactRoot>/<sessionRef>/evidence/`, named `0001-check.png`,
`0002-action.png`, and so on. The report gives that folder as
`evidenceDirectory` and each entry's `evidence` as `path`, `sha256`, and
`boundsPixels`, so a changed image no longer matches its report.

Only the target's own windows are captured, each rendered from its own contents
(`PrintWindow`), not copied from the screen. A window covering the app, or the
desktop between the app's windows, never appears; the space between windows is
transparent. A restarted app that opens behind other windows is still captured
correctly. An action's image is taken right after dispatch, so it may show the app before it has
updated; a check's image is taken after the check has waited for its
condition. When no image could be taken, `evidence.error` says why
(`NoVisibleWindow` or `CaptureFailed`) instead of the entry having nothing.

### Signed reports

Every report from `desktop_get_test_report` carries a `proof`:

- `entries`: a hash chain over a header (session, executable hash, criteria
  hash, evidence folder), each action, each check, and a summary (verdict and
  per-criterion verdicts). Each hash covers the previous one, and evidence
  hashes sit inside the entries, so editing any part of the report or any image
  breaks the chain from that entry on.
- `rootHash`: the last chain hash, signed with ECDSA P-256 (`signature`).
- `publicKey` and `keyId`: the signing key. The private key is a
  non-exportable Windows CNG key (`Pointframe.DesktopProof.v1`) in the current
  user's key store, created on first use.

`DesktopProofService.Verify` recomputes the chain, checks the signature, and,
given the evidence folder, re-hashes every image. It names the first entry that
differs. A valid proof shows the report and images are what this key signed. It
does not show the work is correct, and anyone can sign an edited report with
their own key, so compare `keyId` with the one you expect.

### Proof bundle

Each `desktop_get_test_report` call also writes the report's
`sessionDirectory` (`<artifactRoot>/<sessionRef>/`):

- `report.json`: the signed report, which verifies on its own.
- `evidence/`: the screenshots it hashes.
- `index.html`: a timeline of actions and checks with their screenshots,
  the criteria verdicts, and the key ID. It is a readable view and is not
  itself signed.

Actions are named by their operation (`click`, `enter_text`, `drag`, and so
on). The bundle is rewritten on every call, so it always matches the latest
report.

### Replaying checks

`desktop_replay_checks` re-runs the checks of a signed `report.json` in a
fresh session and compares the verdicts, check by check and per criterion.
Start a new session for the same profile, then pass its `sessionId` and the
report path. The tool refuses a report outside the policy's `artifactRoot`, a
report whose proof does not verify, and a session whose executable hash
differs from the report's. Checks scoped to a `windowRef` are skipped, because
window refs belong to one session.

The result is `matched` when every replayed verdict equals the original and
`differs` otherwise. Replay starts from a fresh launch, so it confirms only
what survives a restart, such as saved data that reopens. Typed text that was
never saved replays as `failed`, which is the correct answer. The replayed
checks are recorded in the new session's own report.

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

`capture_monitor`, `capture_window`, `read_text_from_monitor`, and
`read_text_from_window` return the captured image inline as an image content
block (downscaled to at most 1600 px on its longest edge) in addition to the
structured artifact metadata, so a client that cannot read the server's
filesystem can still see the screenshot. Pass `includeImage: false` for
metadata only; the full-resolution PNG is saved to disk either way.

### App frameworks

Checked with fresh agents on WinForms (PKHeX), WPF (dnGrep), and Electron apps.

- **WinForms:** menu items (`ToolStripMenuItem`) and many controls expose no AutomationId; locate them by role and name (`{ "role": "menu item", "name": "Options" }`).
- **WPF and WinForms text:** `textEquals` reads a Value pattern, or the Name of a text control (TextBlock, Label), which is what it displays.
- **Electron and other Chromium apps:** launch with `--force-renderer-accessibility` (put it before the app path in the profile's arguments) so the page's elements reach UI Automation; an HTML `id` becomes the AutomationId. `ELECTRON_RUN_AS_NODE`, which VS Code sets for agents it hosts, is removed for launched apps, because Electron would otherwise start as plain Node and open no window.

## Smaller responses (opt-in)

Defaults are unchanged. Three optional parameters and one server option reduce what an agent spends
per call; `scripts/measure-mcp-payloads.ps1` prints the measured default-versus-compact table.

- `desktop_observe_app` `detail: "compact"`: every ref and identity an action needs
  (`observationRef`, `imageRef`, `elementRef`, `windowRef`, process ref, image size and desktop bounds)
  and each element's role, name, automation id, and bounds, without null fields. Bounds are
  `[x, y, width, height]` in desktop physical pixels; an element without `windowRef` uses the top-level
  one; `disabled: true` appears only on disabled elements; names and text over 200 characters are
  truncated with the omitted count. Use the default detail when you need the full text.
- `desktop_observe_app` `maxImageEdge` (64 through 1600): caps each image's longest edge. The returned
  `width` and `height` are the coordinate space for `desktop_click`, `desktop_drag`,
  `desktop_enter_text`, and `desktop_scroll`, so actions stay accurate.
- `desktop_get_test_report` `detail: "compact"`: verdicts, criteria, action outcomes, and check verdicts
  without per-item evidence, check conditions, or `proof` (omitted, since it would not be verifiable inline). The proof bundle written to
  `sessionDirectory` (`report.json`, `evidence/`, `index.html`) is always the full signed report, so
  verify and replay from that file, not from the compact response.
- Server option `--compact-text` or `POINTFRAME_MCP_COMPACT_TEXT=1`: the text block of the desktop
  action tools, `desktop_check_ui`, `desktop_get_test_report`, `desktop_get_action_result`, and the
  recording tools becomes a short summary instead of a copy of `structuredContent`. Action summaries
  keep operation, dispatch, verification, observation status, and any error. When dispatch is
  `Partial` or `Unknown` the summary says not to resend and to call `desktop_get_action_result` with
  the action id; when it is `NotStarted` it says no input was sent. Observations, lists, searches, and
  replay results keep their full text because a client that reads only text blocks needs that data.

Because actions are never replayed and the observation they consume stays valid for 30 seconds,
a compact flow does not change when to observe again: call `desktop_observe_app` (compact) before each
input action, act with its refs, and read the action's own dispatch and verification fields before
deciding to repeat anything.

## Local validation

After every release, the coordinator runs `pwsh scripts/check-released-desktop.ps1 -Version <x.y.z>` on an unlocked interactive Windows desktop.
It verifies the published CLI and MCP packages by checksum, then runs the fixture-backed desktop smoke and verification workflow against those released executables.

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
- [Knowledge-base MCP subsystem entry](../knowledge-base/features/cli-mcp.md#standalone-cli-and-mcp-automation)
- [Automation workflow](../../.github/workflows/desktop-automation.yml)

