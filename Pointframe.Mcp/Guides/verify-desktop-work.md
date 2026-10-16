# Verify your desktop work with Pointframe

Use this after you change a Windows desktop app: run the app, check the change in its real UI, and
return a signed proof instead of a claim. Pointframe takes the screenshots, evaluates the checks, and
signs the report. You only ask; you never write the result.

## Before you start

1. Read `pointframe://server-info`. `DesktopTestingEnabled` must be `true`. If it is `false`, the
   server was started without `--desktop-testing --desktop-policy <absolute path>`; ask the user to
   enable it, and stop.
2. The app must be a profile in that policy file. The policy decides what you may launch: you cannot
   start an executable it does not list. For an app you just built, the profile's `executablePath`
   must point at that build. A minimal policy:

```json
{
  "schemaVersion": 1,
  "artifactRoot": "C:\\absolute\\existing\\folder",
  "evidencePolicy": "All",
  "profiles": [
    {
      "id": "my-app",
      "executablePath": "C:\\absolute\\path\\MyApp.exe",
      "arguments": [],
      "workingDirectory": "C:\\absolute\\path",
      "allowAttach": false,
      "allowedActions": ["StartTestSession", "ObserveApp", "FocusWindow", "Click", "PressKeys", "Drag", "EnterText", "Invoke", "Scroll", "CheckUi", "GetActionResult", "GetTestReport", "ReplayChecks", "RestartApp", "EndTestSession"],
      "allowedGlobalHotkeys": {},
      "allowedShellSurfaces": [],
      "allowMonitorObservation": false
    }
  ]
}
```

`evidencePolicy` `All` stores a screenshot for every check and action; `Failures` only for those that
did not pass; `None` stores none.

## The workflow

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
   portable steps plus any unsupported coordinate actions or session-scoped checks.
9. **End the session.** Call `desktop_end_test_session`. It asks the app to close its main window,
   waits briefly, and terminates the session-launched process tree only if the app does not exit.
   Attached processes are never terminated. A second end call returns `SessionNotFound`.

## Reading the verdict

- `passed`: every criterion has a passing check, the negative control passed, and nothing failed.
- `failed`: a check failed, or an action that reached the app failed. Fix the code, rebuild, and verify
  again in a **new** session. A session keeps its failures.
- `inconclusive`: nothing was checked, a criterion has no passing check (`uncovered`), the UI could not
  be read, or the negative control is missing. This is not a pass.

Every evaluated check counts, so a failing check you ran "just to look" fails the report. Use
`desktop_observe_app` to explore, and `desktop_check_ui` only for checks you mean. Selector errors
`InspectionIncomplete` and `AmbiguousTarget` mean the target was not established and no input was
sent; use the response's `nextStep`, observe to inspect the UI, and retry with a new `actionId`.

## What to return to the user

Report the `verdict`, each criterion with its verdict, the proof `keyId`, and the path to
`index.html` in `sessionDirectory`. Say the work is done only when the verdict is `passed`. Otherwise,
say which criterion did not pass and why.

## Replaying a report

`desktop_replay_checks` re-runs the checks of a signed `report.json` in a fresh session of the same
executable and returns `matched` or `differs`. Start a new session for the same profile, then pass its
`sessionId` and the report path. Replay starts from a fresh launch, so it confirms only what survives a
restart; unsaved state replays as `failed`, which is the correct answer.

## Limits

- The proof shows the report and screenshots are unedited and came from this machine's key. It does not
  show the criteria were the right ones; the user judges that.
- Screenshots show only the app's own windows, rendered from their contents, so a window covering the
  app never appears in them. An action's screenshot is taken right after dispatch and may show the app
  before it has updated; a check's screenshot is taken after the check has waited for its condition.
- Checks scoped to a `windowRef` are skipped on replay, because window refs belong to one session.
