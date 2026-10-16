---
name: verify-wpf-sample
description: Prove that a change to the WPF Contacts sample works by driving its real window with Pointframe and returning a signed report. Use after changing samples/verify/wpf, or when asked to verify that adding a contact works.
---

# Verify the WPF Contacts sample

The Contacts sample is a small WPF window: a name box, an Add button, a contact list and a status line.
`pointframe verify run` builds it, launches it and drives it in the real UI. The steps live only in
`.pointframe/verify.json` (the scenario `add-contact`); this skill names them and does not copy them.

Work from the sample's folder, in an interactive, unlocked Windows desktop session. Features are listed in
`features/README.md`.

## Launch

```powershell
pointframe verify run
```

The `build` gate (`dotnet build WpfSample.csproj`) produces `bin/Debug/net10.0-windows/WpfSample.exe`, then
the run launches it and drives every scenario. Read `status` in the printed JSON: it must be `pass`. A
build or UI failure is `fail` (exit 1). If the run stops with `mcp_not_found`, run `pointframe verify
setup` once and run again. If it stops with `app_running`, close the Contacts window.

## Doctor

- `pointframe verify status` shows whether the last verdict is a pass for the files as they are now, and
  whether `pointframe` is on `PATH`.
- `pointframe verify run --scenario add-contact` runs only that scenario. A filtered run is `partial`, so
  it is a quick check and never a completion proof.

## Drive

| Scenario | Proves |
|---|---|
| `add-contact` | An empty name is rejected with "Name is required" and adds nothing. A name is added to the list and the status says "Added <name>". See `features/add-contact.md`. |

## Evidence

- `artifacts/pointframe-verify/verdict.json` is the verdict.
- Each scenario's signed proof bundle is under `artifacts/pointframe-verify/runs/<time>/`: `report.json`,
  the `evidence/` screenshots, and `index.html`, a readable timeline.
- These are files on disk; they are still there after the app has closed. After a run, confirm that
  `verdict.json` and the bundle's `index.html` exist before you report.

## Cleanup

Nothing to do by hand. Each scenario's session ends when it finishes and only the process the run started
is stopped.

## Helpers

- `pointframe verify setup` gives the agent interactive `desktop_` tools for this app (restart the agent
  afterwards).
- `role` is the UI Automation control type name in one word: `Edit` (the name box), `Button`, `ListItem`
  (a row of the list), `Text`. `list item` never matches.
- Build the deliberately broken variant with `dotnet build WpfSample.csproj -p:SampleBroken=true`. It
  updates the status but does not add the contact, so `add-contact` must report `fail`.

## What this does not prove

Only the empty-name rule and adding one contact are covered. Whitespace-only or duplicate names, long
names, the keyboard path and the selection after an add are not.
A pass shows the scenario's checks hold, not that they were the right checks; a person judges that.
