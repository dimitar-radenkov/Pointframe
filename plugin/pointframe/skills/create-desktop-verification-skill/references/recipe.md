# Verify your desktop work with Pointframe

Use this after you change a Windows desktop app: run the app, check the change in its real UI, and
return a signed proof instead of a claim. Pointframe takes the screenshots, evaluates the checks, and
signs the report. You only ask; you never write the result.

There are two ways to get that proof. The first needs no MCP client restart and is the fastest way to a
first passing proof in a project that has none. The second gives you interactive tools to explore and
check the running app step by step. Start with the first; use the second when you need it.

## Short path for coding agents

<!-- short-path:begin -->
Run everything from the project root, in an interactive, unlocked Windows desktop session.

1. Install the CLI (skip this if `pointframe --version` works). Run this one line as it is, in one tool call; it works from Bash and from PowerShell:

```
pwsh -NoProfile -Command '$u="https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Cli-win-x64.zip"; $d=Join-Path $env:TEMP "pointframe-cli"; Remove-Item $d -Recurse -Force -ErrorAction Ignore; New-Item -ItemType Directory $d | Out-Null; Invoke-WebRequest $u -OutFile "$d\cli.zip"; Invoke-WebRequest "$u.sha256" -OutFile "$d\cli.zip.sha256"; if ((Get-FileHash "$d\cli.zip").Hash -ne ((Get-Content "$d\cli.zip.sha256" -Raw).Trim() -split "\s+")[0]) { throw "SHA-256 mismatch" }; Expand-Archive "$d\cli.zip" "$d\x"; & "$d\x\Pointframe.Cli.exe" install; "$env:LOCALAPPDATA\Programs\Pointframe.Cli\pointframe.exe"'
```

   `install` puts `pointframe` on the user `Path` for new shells only. In the shell you already have open, call the exe path the line prints (`%LOCALAPPDATA%\Programs\Pointframe.Cli\pointframe.exe`) in place of `pointframe`.
2. Write the spec, with your built exe's path relative to the project root (one line, for a single-project WPF app):

```
pointframe verify init --app bin/Release/net10.0-windows/MyApp.exe --hooks none
```

3. Add one scenario to the `scenarios` array of `.pointframe/verify.json`: an action, the resulting state, and a negative control (a check with `expectFailure` and a deliberately wrong expectation, which must fail):

```jsonc
{ "id": "save-text", "criteria": ["Saving shows the typed text in the status line."], "steps": [
  { "enterText": { "automationId": "textBox", "text": "hello" } },
  { "invoke": { "automationId": "saveButton" } },
  { "check": { "kind": "textEquals", "automationId": "statusLabel", "expected": "Saved hello", "criterion": "C1" } },
  { "check": { "kind": "textEquals", "automationId": "statusLabel", "expected": "Not saved", "expectFailure": true, "timeoutSeconds": 1 } } ] }
```

4. Run `pointframe verify run`. It runs the gates, launches the app, drives the scenario in the real UI, and installs the MCP server itself on its first run. Read `status` in the printed JSON: `pass` is exit 0, `fail` is exit 1, a wrong spec is exit 2. Run `verify run` last: any later edit, even to docs or skill files, makes the pass stale (`verify status` then shows `fresh: false`), so write everything first and run again if you change anything.
5. Evidence: `artifacts/pointframe-verify/verdict.json` is the verdict, and each scenario's signed bundle (`report.json`, `evidence/` screenshots, `index.html`) is under `artifacts/pointframe-verify/runs/<time>/`. Say the work is done only when `status` is `pass`.

The spec fields are in [verify-spec.md](https://github.com/dimitar-radenkov/Pointframe/blob/master/docs/cli/verify-spec.md); the example above covers the common case.
<!-- short-path:end -->

## Fastest first proof: a spec and `pointframe verify run`

Everything runs from the project root, in an interactive, unlocked Windows desktop session.
`pointframe verify run` drives the app itself through a Pointframe-owned MCP server, so your own MCP
client needs no configuration and no restart.

1. **Install the CLI with one command.** Run this single line as it is, in one tool call. It works the same
   from a Bash tool on Windows (the single quotes keep Bash from expanding anything) and from PowerShell. It
   downloads the latest release, stops on a SHA-256 mismatch, extracts it, runs `install`, and prints the
   installed exe path as its last line:

   ```
   pwsh -NoProfile -Command '$u="https://github.com/dimitar-radenkov/Pointframe/releases/latest/download/Pointframe.Cli-win-x64.zip"; $d=Join-Path $env:TEMP "pointframe-cli"; Remove-Item $d -Recurse -Force -ErrorAction Ignore; New-Item -ItemType Directory $d | Out-Null; Invoke-WebRequest $u -OutFile "$d\cli.zip"; Invoke-WebRequest "$u.sha256" -OutFile "$d\cli.zip.sha256"; if ((Get-FileHash "$d\cli.zip").Hash -ne ((Get-Content "$d\cli.zip.sha256" -Raw).Trim() -split "\s+")[0]) { throw "SHA-256 mismatch" }; Expand-Archive "$d\cli.zip" "$d\x"; & "$d\x\Pointframe.Cli.exe" install; "$env:LOCALAPPDATA\Programs\Pointframe.Cli\pointframe.exe"'
   ```

   `install` adds `pointframe` to the user `Path` for new shells only. In the shell you are already using,
   call the printed path (`%LOCALAPPDATA%\Programs\Pointframe.Cli\pointframe.exe`) instead of `pointframe`
   until you open a new one. Skip this step if `pointframe --version` already works.
2. **Write `.pointframe/verify.json`.** Run this one line from the project root, with the built exe's path
   relative to the project root in place of `<built exe>`:

   ```
   pointframe verify init --app <built exe> --hooks none
   ```

   For example, for the WPF sample (also one line):

   ```
   pointframe verify init --app bin/Release/net10.0-windows/WpfSample.exe --hooks none
   ```

   It writes a spec with the detected build and test gates and the `app`.
   `--hooks none` adds no hook and edits no agent settings. Then add one scenario for the change. A complete
   spec:

   ```json
   {
     "schemaVersion": 1,
     "app": { "id": "my-app", "executable": "bin/Release/net10.0-windows/MyApp.exe" },
     "gates": [{ "id": "build", "run": "dotnet build MyApp.csproj -c Release" }],
     "scenarios": [{
       "id": "save-text",
       "criteria": ["Saving shows the typed text in the status line."],
       "steps": [
         { "enterText": { "automationId": "textBox", "text": "hello" } },
         { "invoke": { "automationId": "saveButton" } },
         { "check": { "kind": "textEquals", "automationId": "statusLabel", "expected": "Saved hello", "criterion": "C1" } },
         { "check": { "kind": "textEquals", "automationId": "statusLabel", "expected": "Not saved", "expectFailure": true, "timeoutSeconds": 1 } }
       ]
     }]
   }
   ```

   Every criterion needs a check that names it with `criterion`. A scenario with criteria needs one
   negative control: a check with `"expectFailure": true` and a deliberately wrong expectation. The spec
   fields are in `references/verify-spec.md` in the base directory of the `create-desktop-verification-skill` skill, or
   `docs/cli/verify-spec.md` in the Pointframe repository. If you are reading this as the bundled skill or the MCP
   resource, read the file from disk and do not fetch Pointframe docs from the web. If you are reading the public
   README or docs, the short path above covers the common case, and the schema is linked there.
3. **Name controls by `automationId` when the app has one, otherwise by `role` and `name`.** If a control
   has no automation id, give it one in the app (`AutomationProperties.AutomationId` in WPF, `Name` in
   WinForms) rather than guessing. See Roles below.
4. **Build and run.** Run `pointframe verify run`. It runs the gates (so the build gate produces the
   app), launches the app, runs each scenario in the real UI, and writes the verdict. Read `status` in
   the printed JSON, not only the exit code: `pass` is exit 0, `fail` is exit 1, a wrong spec is exit 2.
   On a machine with no Pointframe MCP server, the first run installs the checksum-verified server itself
   and prints a one-line note on stderr; no separate setup is needed. If that install fails (for example
   offline), the run stops with `mcp_not_found` and the installer's error: fix the network and run again,
   run `pointframe verify setup`, or pass `--mcp <path>`. If it stops with `app_running`, close the app
   and run again.
5. **Read the evidence.** `artifacts/pointframe-verify/verdict.json` is the verdict. Each scenario's
   signed proof bundle (`report.json`, the `evidence/` screenshots, and `index.html`, a readable
   timeline) is under `artifacts/pointframe-verify/runs/<time>/`. A failed step names the step and what
   the app showed. Fix the code or the spec and run again. Do not use `--only gates` or `--scenario` when
   you check completion: a filtered run is `partial` and never fresh.
6. **Report.** Give the user the `status`, each scenario's result, and the path of the bundle's
   `index.html`. Say the work is done only when `status` is `pass`.

### What approval needs, and who gives it

- The gates and the app run with the user's permissions, so the CLI approves them before the first run.
  Standard commands (`dotnet build`, `dotnet test`, `npm test`, `npm run <script>`, the pnpm and yarn
  forms, `pwsh -File` of a script inside the project) and an app inside the project folder with plain
  arguments are approved by policy, offline, with nobody involved. Keep the gates standard.
- A nonstandard gate command goes to the approver agent. If none is available, the run fails with
  `spec_untrusted` or `approver_unavailable`, and only a person can approve it, by running
  `pointframe verify trust` in a terminal. Tell the user that once. Do not work around it, and do not
  edit approval or policy files yourself.
- Changing a gate or the app (not a scenario step) asks for approval again.
- The proof shows the report and screenshots are unedited. It does not show the criteria were the right
  ones; the user judges that.

### Roles: the UI Automation control type name

In a spec step or check, and in `desktop_check_ui`, `desktop_invoke` and `desktop_enter_text`, `role` is
the UI Automation control type name, written as one word. Matching ignores case but not spaces:
`ListItem` works and `list item` never matches. For WPF:

| Control | `role` |
|---|---|
| `Button` | `Button` |
| `TextBox`, `PasswordBox` | `Edit` |
| `TextBlock`, `Label` | `Text` |
| `ListBox` or `ListView` row | `ListItem` |
| `ListBox` or `ListView` itself | `List` |
| `CheckBox` | `CheckBox` |
| `RadioButton` | `RadioButton` |
| `ComboBox` | `ComboBox` |
| `MenuItem` | `MenuItem` |
| `TabItem` | `TabItem` |
| `TreeViewItem` | `TreeItem` |
| `DataGrid` | `DataGrid` |
| `Hyperlink` | `Hyperlink` |

A control's `name` is its UI Automation name: a button's caption, a list item's text, a text block's
text. It is compared exactly, including case. Prefer `automationId`; use `role` with `name` when there is
none.

When you are not sure of the role, name or id of a control, read it instead of guessing:

- In an interactive session (next section), `desktop_observe_app` lists each element's `role`, `name`
  and `automationId`.
- After a failed `verify run`, search the scenario's `report.json` for the control's name to see the
  `role` Pointframe recorded. When a locator matched more than one element, the failed step lists the
  candidates with their role, name and automationId.

## Interactive tools

Use these to explore an unfamiliar UI, to read the role or id of a control, or to check a change step by
step. They need desktop testing to be on.

### Before you start

1. Read `pointframe://server-info`. If `DesktopTestingEnabled` is `true`, continue with the workflow
   below. If it is `false`, the server was started without a policy. Do not write a policy. Run
   `pointframe verify setup` in the project; it needs the `.pointframe/verify.json` from the steps above,
   with an `app`. It writes the project's agent configuration (`.mcp.json` for Claude Code; use
   `--client codex`, `vscode` or `all` for the others) so the server starts with a policy generated from
   that spec, which lists exactly the spec's app and nothing else. Restart the agent session to load the
   new configuration. Until then, prove the work with `pointframe verify run`.
2. `desktop_list_apps` lists the app profile you may start. The policy decides what you may launch: you
   cannot start an executable it does not list. If the spec's gates or app change, the server refuses to
   start until they are approved again (by policy for standard commands; otherwise a person runs
   `pointframe verify trust`).

The policy's `evidencePolicy` `All` stores a screenshot for every check and action; `Failures` only for
those that did not pass; `None` stores none.

### The workflow

1. **Write the criteria before you act.** Each criterion is one observable statement about the UI,
   such as "The customer list shows Jane Doe". Write them from the task, not from what you later see.
   Each one must be checkable with `desktop_check_ui` (step 4). If the user gave acceptance criteria,
   use theirs word for word.
2. **Start a session.** Call `desktop_start_test_session` with a new UUID `actionId`, the `profileId`,
   and `criteria`. It launches the app and returns `sessionRef`. The criteria are numbered `C1`, `C2`,
   ... in your order, hashed, and frozen: you cannot change them in this session.
3. **Use selectors first.** For a known field and button, start the session, enter text with
   `desktop_enter_text` using `semanticValue: true` and a selector, invoke the button with
   `desktop_invoke` using a selector, then check the criterion with `desktop_check_ui`. This is the
   four-call first check (session start, enter text, invoke, check); it needs no display discovery or
   observation. For example, after starting with `criteria: ["The status shows Saved"]` and getting
   `sessionId`, use these arguments in order:

   ```json
   {"sessionId":"<sessionId>","actionId":"00000000-0000-4000-8000-000000000002","text":"hello","semanticValue":true,"automationId":"textBox"}
   {"sessionId":"<sessionId>","actionId":"00000000-0000-4000-8000-000000000003","automationId":"saveButton"}
   {"sessionId":"<sessionId>","kind":"textEquals","automationId":"statusLabel","expected":"Saved","timeoutSeconds":5,"criterionId":"C1"}
   ```

   `desktop_enter_text` accepts `automationId`, or `role` with `name`, when `semanticValue` is
   `true`; it sets the value through UI Automation. `desktop_invoke` accepts the same selectors.
   Selectors must resolve to exactly one element. Use `desktop_observe_app` to explore an unknown UI,
   identify a selector or window, or get an image/element reference for an operation that needs one.
   `desktop_click`, `desktop_drag`, and `desktop_scroll` use image coordinates; coordinate-based
   `desktop_enter_text` also needs an observation. `desktop_focus_window` needs a `windowRef` from an
   observation. These refs expire after 30 seconds, so observe again before using them.

   Every action needs a new UUID `actionId`. You do not need a tool to make one: any well-formed UUID
   that you have not used before in this server works, for example `00000000-0000-4000-8000-000000000001`,
   then `...0002`. Reusing one with different arguments is rejected. If a selector returns
   `AmbiguousTarget`, observe the matches and narrow the selector or add `windowRef`; if it returns
   `InspectionIncomplete`, observe the UI and retry with a new `actionId`. Those failures send no
   input. Follow a response's `nextStep` when supplied.

   For a keyboard chord, call `desktop_press_keys` with `chord: "Ctrl+S"` and a current target
   `windowRef` or `observationRef`; focus the window first with `desktop_focus_window` when needed.
4. **Check each criterion.** Call `desktop_check_ui` with `criterionId` set to `C1`, `C2`, .... Kinds:
   `exists`, `absent`, `enabled`, `toggleEquals`, `selectionEquals`, `textEquals`, `windowExists`,
   `windowAbsent`, `processExited`. For `enabled`, `expected` is `true` or `false`. For
   `toggleEquals`, use `true`/`checked`/`on` for checked, `false`/`unchecked`/`off` for unchecked,
   or `indeterminate`. For `selectionEquals`, use `selected` or `notSelected`; `textEquals` compares
   exact text. Prefer `automationId`; otherwise use `role` with `name`. The tool waits up to
   `timeoutSeconds` for the condition, so you do not need delays. A state check returns `actualValue`
   when exactly one element's state was read, making a mismatch clear; sensitive text is never returned.
5. **Run one negative control.** Call `desktop_check_ui` once with `expectFailure: true` and a
   deliberately wrong expectation, such as `textEquals` with a value the element does not hold, and
   `timeoutSeconds: 1`. It passes only when the wrong expectation is rejected, which shows the check
   can tell states apart. A session with criteria cannot pass without one. Do not give it a
   `criterionId`.
6. **Check what must survive a restart.** For saved data, close the app through its own UI (its Close
   or Exit command, or `desktop_press_keys`), call `desktop_restart_app`, then check again. The restart
   is refused while the app is still running.
7. **Get the report.** Call `desktop_get_test_report`. It returns `verdict`, the per-criterion
   verdicts, the `proof`, and `sessionDirectory`, a folder with the signed `report.json`, the
   `evidence/` screenshots, and `index.html`, a readable timeline.
8. **Export a repeatable scenario (optional).** Call `desktop_export_scenario` with `sessionId` and a
   spec-compatible `scenarioId`. It writes `scenario-<id>.json` beside the report and returns the
   portable steps plus any unsupported coordinate actions or session-scoped checks. Add it to the spec
   with `pointframe verify scenario add --from <file>`.
9. **End the session.** Call `desktop_end_test_session`. It asks the app to close its main window,
   waits briefly, and terminates the session-launched process tree only if the app does not exit.
   Attached processes are never terminated. A second end call returns `SessionNotFound`.

### Reading the verdict

- `passed`: every criterion has a passing check, the negative control passed, and nothing failed.
- `failed`: a check failed, or an action that reached the app failed. Fix the code, rebuild, and verify
  again in a **new** session. A session keeps its failures.
- `inconclusive`: nothing was checked, a criterion has no passing check (`uncovered`), the UI could not
  be read, or the negative control is missing. This is not a pass.

Every evaluated check counts, so a failing check you ran "just to look" fails the report. Use
`desktop_observe_app` to explore, and `desktop_check_ui` only for checks you mean. Selector errors
`InspectionIncomplete` and `AmbiguousTarget` mean the target was not established and no input was
sent; use the response's `nextStep`, observe to inspect the UI, and retry with a new `actionId`.

### What to return to the user

Report the `verdict`, each criterion with its verdict, the proof `keyId`, and the path to
`index.html` in `sessionDirectory`. Say the work is done only when the verdict is `passed`. Otherwise,
say which criterion did not pass and why.

### Replaying a report

`desktop_replay_checks` re-runs the checks of a signed `report.json` in a fresh session of the same
executable and returns `matched` or `differs`. Start a new session for the same profile, then pass its
`sessionId` and the report path. Replay starts from a fresh launch, so it confirms only what survives a
restart; unsaved state replays as `failed`, which is the correct answer.

## If you are generating a verify-<app> skill

Use this section when you are asked to generate a project skill that verifies a Windows desktop app
(pstack `/create-verification-skill`, a similar generator, or the `create-desktop-verification-skill`
skill of the Pointframe plugin). Pointframe is the harness behind that skill. It fills the usual
sections like this:

| Skill section | In Pointframe |
|---|---|
| Launch | The gates and the `app` of `.pointframe/verify.json`. `pointframe verify run` builds, launches and drives the app; the skill does not start the exe itself. |
| Doctor | `pointframe verify status` (is `pointframe` on `PATH`, is the last verdict a fresh pass) and `pointframe verify run --scenario app-starts`. The second is a startup smoke check, not proof of a feature; `pointframe verify init --app <exe> --explore` writes that scenario, or add one that only checks a main-window control with `exists`. |
| Drive | The scenario steps (`enterText`, `invoke`, `pressKeys`, `restart`, `check`) with selectors. |
| Evidence | The signed report and the proof bundle under `artifacts/pointframe-verify/runs/<time>/`, and `verdict.json`. They are files on disk and survive Cleanup. |
| Cleanup | Nothing to do by hand: each scenario's session ends when it finishes, and only the processes the run started are stopped. |
| Helpers | The CLI (`pointframe verify ...`) and, after `pointframe verify setup`, the `desktop_` tools. |

Rules for the generated skill:

- The executable steps live only in `.pointframe/verify.json`. The skill and its feature files name
  scenarios by id and say how to run them; they do not copy steps, so they cannot drift from the spec.
- Write one file per feature under `features/`, each with exactly four H2 sections, named exactly
  `Sub-features`, `How to get to it (user POV)`, `Driving it with Pointframe` and `Gotchas` (tools check the
  headings), listed in `features/README.md`.
- Write the spec and every skill file (SKILL.md, features/README.md, the feature files) first. Then prove the skill
  once, end to end, with `pointframe verify run` as the last step, and confirm that the evidence files exist after
  the run, before telling anyone it works. Make no edit after the run: any change makes the pass stale; if you
  must change something, run `verify run` again. Say what stays unproved.

## Limits

- The proof shows the report and screenshots are unedited and came from this machine's key. It does not
  show the criteria were the right ones; the user judges that.
- Screenshots show only the app's own windows, rendered from their contents, so a window covering the
  app never appears in them. An action's screenshot is taken right after dispatch and may show the app
  before it has updated; a check's screenshot is taken after the check has waited for its condition.
- Checks scoped to a `windowRef` are skipped on replay, because window refs belong to one session.
- Desktop scenarios take over the mouse and keyboard of an interactive, unlocked Windows session while
  they run. A hosted CI runner has none.
