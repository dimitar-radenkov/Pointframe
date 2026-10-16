---
name: create-desktop-verification-skill
description: Create a verify-<app> skill for a Windows desktop app (WPF, WinForms, Win32, Electron) that has no scripted proof yet, so an agent can later prove a change works by driving the real UI with Pointframe and returning a signed report. Use when asked to set up verification for a desktop app repo, to add a verify skill, or to run pstack's create-verification-skill on a Windows desktop project.
---

# Create a verification skill for a Windows desktop app

You are writing a project skill, `verify-<app>`, that lets any later agent prove a change to this app in
its real UI. Pointframe is the harness: it launches the app, drives it, evaluates checks, and signs the
evidence. The full commands, spec fields, role names and trust rules are in
`references/recipe.md` (next to this file). Read it once before you start; do not read the repository's
CLI docs piecemeal.

Work in the app's repository, in an interactive, unlocked Windows desktop session. Do not ask the user to
write policy files or selectors; the spec below is all that is needed.

## 1. Interview the repo

Find out, from the code and project files, not by asking:

- **Surface.** Which UI framework (WPF, WinForms, Win32, Electron), which project builds the exe, and the
  relative path of the built exe.
- **Run.** The build command (it becomes the build gate) and the test command, if any.
- **Drive.** The one journey that matters most (a save, an add, a search), the controls it touches, and
  their `AutomationProperties.AutomationId` (WPF) or `Name` (WinForms). If a control has none, add an id
  in the app's code. Do not guess roles: the recipe lists the UI Automation control type names.
- **Observe.** The resulting state a user would see (a status line, a new list row, a changed label) and
  the text it shows.
- **Isolate.** Where the app keeps its data. If it persists settings or files, find the environment
  variable or argument that moves them and put it in `app.isolation`, so a run starts clean.

## 2. Write the spec scenario for one meaningful journey

Install the CLI with the recipe's one-line command, then write `.pointframe/verify.json` as its first
section says. Run this on one line, with the built exe's path relative to the project root (for the WPF
sample, `bin/Debug/net10.0-windows/WpfSample.exe`), then add the scenario:

```
pointframe verify init --app <built exe> --hooks none
```

One scenario:

- Steps that do the action (`enterText`, `invoke`) with selectors.
- Criteria, each with a check on the resulting state: the status text or the new list item, not only that
  an element exists.
- A negative case when the app has one (for example an empty name is rejected).
- One negative control: a check with `"expectFailure": true` and a deliberately wrong expectation.

Keep the build gate and the test gate standard commands so no person has to approve anything.

## 3. Write the skill

The `verify-<app>` skill and `.pointframe/verify.json` live in the same repository. The spec is the single
source of the executable steps: the skill names scenarios by id and says how to run them, and must never copy
the steps (the action sequence and the expected values) out of the spec. A feature file may name the
controls and ids it relies on, as orientation only. Create `<skills>/verify-<app>/SKILL.md`, where `<skills>` is the project skill folder the repo already has:
`.claude/skills`, `.agents/skills` or `.cursor/skills`. If none exists, use `.claude/skills` for Claude Code
and `.agents/skills` for Codex and other agents. The skill has YAML front matter with `name: verify-<app>`
and a one-sentence `description` that says when to use it ("after changing <app>, to prove it works"), then
these sections:

- **Launch.** Run `pointframe verify run` from the project root. It builds the app (the gates), launches it
  and drives it.
- **Doctor.** `pointframe verify status`, and `pointframe verify run --scenario <id>` for a quick smoke
  check. A filtered run is `partial`; it is a check, not a completion proof.
- **Drive.** Name the scenario ids and what each proves. Do not copy the steps; they live only in
  `.pointframe/verify.json`.
- **Evidence.** `artifacts/pointframe-verify/verdict.json`, and per scenario the signed `report.json`,
  `evidence/` and `index.html` under `artifacts/pointframe-verify/runs/<time>/`.
- **Cleanup.** The run ends its sessions and stops only the processes it started. Say to close the app if
  `verify run` reports `app_running`.
- **Helpers.** `pointframe verify setup` for interactive `desktop_` tools, and the role vocabulary.

Also create `<skills>/verify-<app>/features/README.md`, an index with one line per feature file, and one
feature file, `features/<feature>.md`, for the journey you wrote. It has exactly four H2 sections:

1. `## Sub-features` what the feature includes.
2. `## How to get to it` how a user reaches it in the UI.
3. `## Driving it with Pointframe` the scenario id, the selectors it uses, and the criteria it proves.
4. `## Gotchas` traps found while writing it (a control without an id, a delay, a dialog).

## 4. Prove it once, end to end

Run `pointframe verify run`. Read `status` in the printed JSON. It must be `pass`. Then confirm that the
evidence exists after the run: `artifacts/pointframe-verify/verdict.json`, and the scenario's `report.json`
and `index.html` under `runs/`. Fix the spec or the app's ids and run again until it passes. Do not edit the
app to make a check pass.

If the run fails with `spec_untrusted` or `approver_unavailable`, a gate is not a standard command. Replace
it with a standard one. If it still cannot be approved, tell the user to run `pointframe verify trust`;
do not work around it.

## 5. Say what stays unproved

End your report with: the scenario you wrote and its verdict, the path of the evidence, and what the skill
does not cover (the other features, other states, anything the criteria do not check). A pass shows the
checks you wrote hold, not that they were the right checks; the user judges that.
