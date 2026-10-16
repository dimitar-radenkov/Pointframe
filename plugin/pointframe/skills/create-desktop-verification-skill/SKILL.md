---
name: create-desktop-verification-skill
description: Create a verify-<app> skill for a Windows desktop app (WPF, WinForms, Win32, Electron) that has no scripted proof yet, so an agent can later prove a change works by driving the real UI with Pointframe and returning a signed report. Use when asked to set up verification for a desktop app repo, to add a verify skill, or to run pstack's create-verification-skill on a Windows desktop project.
---

# Create a verification skill for a Windows desktop app

This skill's files are next to this SKILL.md: read `references/recipe.md` and `references/verify-spec.md`
from this skill's base directory (Claude Code prints it as "Base directory for this skill"). Do not search
elsewhere and do not use web docs.

You are writing a project skill, `verify-<app>`, that lets any later agent prove a change to this app in
its real UI. Pointframe is the harness: it launches the app, drives it, evaluates checks, and signs the
evidence. The full commands, spec fields, role names and trust rules are in
`references/recipe.md` and the spec schema in `references/verify-spec.md`. Read them once before you
start.

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

Install the CLI with the recipe's one-line command, then write `.pointframe/verify.json`. Run this on one
line, with the built exe's path relative to the project root (for the WPF sample,
`bin/Release/net10.0-windows/WpfSample.exe`):

```
pointframe verify init --app <built exe> --hooks none
```

Then edit the spec it wrote: start from the complete example in the recipe's step 2 and change the ids,
texts and selectors. Every field is also in `references/verify-spec.md`. Read that file and the recipe from
this skill's base directory. One scenario:

- Steps that do the action (`enterText`, `invoke`) with selectors.
- Criteria, each with a check on the resulting state: the status text or the new list item, not only that
  an element exists.
- A negative case when the app has one (for example an empty name is rejected).
- One negative control: a check with `"expectFailure": true` and a deliberately wrong expectation.

Keep the build gate and the test gate standard commands so no person has to approve anything.

## 3. Write the skill

The `verify-<app>` skill and `.pointframe/verify.json` live in the same repository. The spec is the single
source of the executable steps: the skill names scenarios by id and says how to run them, and must never
copy the steps (the action sequence and the expected values) out of the spec. A feature file may name the
controls and ids it relies on, as orientation only.

Write it in ONE folder, `<skills>/verify-<app>/`: the project skill folder the repo already has
(`.claude/skills`, `.agents/skills` or `.cursor/skills`). If none exists, use the folder of the agent you
are running in: `.claude/skills` for Claude Code, `.agents/skills` for Codex and other agents. Do not write
the skill to a second folder.

Create all three files in a single shell command (one tool call), filling this skeleton. The feature file
has exactly four H2 sections, and the headings must be exactly `Sub-features`,
`How to get to it (user POV)`, `Driving it with Pointframe` and `Gotchas`; tools check them.

`<skills>/verify-<app>/SKILL.md`:

```markdown
---
name: verify-<app>
description: Prove that a change to <app> works by driving its real window with Pointframe and returning a signed report. Use after changing <app>.
---

# Verify <app>

The executable steps live only in `.pointframe/verify.json`. This skill names the scenarios and how to run them.

## Launch
Run `pointframe verify run` from the project root. It builds the app (the gates), launches it and drives every scenario. Read `status` in the printed JSON; it must be `pass`. On `mcp_not_found` run `pointframe verify setup` once; on `app_running` close the app.

## Doctor
`pointframe verify status`; `pointframe verify run --scenario <id>` for a quick `partial` check.

## Drive
| Scenario | Proves |
|---|---|
| `<id>` | <what it proves> (see `features/<feature>.md`) |

## Evidence
`artifacts/pointframe-verify/verdict.json`, and per scenario the signed bundle (`report.json`, `evidence/`, `index.html`) at the `bundleDirectory` the verdict names.

## Cleanup
Nothing by hand: the run ends its sessions and stops only the processes it started.

## Helpers
`pointframe verify setup` gives interactive `desktop_` tools. `role` is the UI Automation control type name in one word (`Edit`, `Button`, `ListItem`, `Text`).

## What this does not prove
<what stays unproved>
```

`<skills>/verify-<app>/features/README.md`: a title and one line per feature file, `- [<feature>](<feature>.md): <summary>`.

`<skills>/verify-<app>/features/<feature>.md`:

```markdown
# <Feature>

## Sub-features
- <what the feature includes>

## How to get to it (user POV)
<how a user reaches it in the UI>

## Driving it with Pointframe
Scenario `<id>` in `.pointframe/verify.json`. Controls: <name box `automationId` ...>. Criteria it proves: C1 ..., C2 ...

## Gotchas
- <traps found while writing it>
```

## 4. Prove it once

Run `pointframe verify run` once. The verdict JSON it prints is the proof: `status` must be `pass`, and each
scenario in it has a `bundleDirectory`. Confirm the evidence exists with one command that lists that
directory (`report.json` and `index.html` must be there). Do not run `verify status` or run again unless
something failed. If it failed, fix the spec or the app's ids and run again. Do not edit the app to make a
check pass.

If the run fails with `spec_untrusted` or `approver_unavailable`, a gate is not a standard command. Replace
it with a standard one. If it still cannot be approved, tell the user to run `pointframe verify trust`;
do not work around it.

## 5. Say what stays unproved

End your report with: the scenario you wrote and its verdict, the path of the evidence, and what the skill
does not cover (the other features, other states, anything the criteria do not check). A pass shows the
checks you wrote hold, not that they were the right checks; the user judges that.
