# Verification spec v1 (frozen)

This page is the reference for `.pointframe/verify.json`, the file that
`pointframe verify` reads. For setup and daily use, start with the
[verify guide](verify.md). For the other verify commands, see the
[CLI README](README.md#verify-a-desktop-app-from-a-spec).

## Status

`schemaVersion` 1 is frozen as of this release.

- Existing fields keep their meaning.
- No field is added, removed, or reinterpreted under version 1.
- A change that would do any of that becomes `schemaVersion` 2, with migration
  notes.
- The CLI rejects unknown fields and unknown versions before it runs any command.
  The run exits with code `2` and `errorCode` `spec_invalid`.

The files the CLI writes (the verdict and `review.json`) are not frozen. See
[Output files](#output-files-not-frozen).

## Location and project root

- The default spec is `.pointframe/verify.json`, in the current folder.
- `--spec <file>` names another file. It applies to `verify run`, `verify status`,
  `verify trust`, and `verify task start`.
- The project root is the folder that holds `.pointframe/`. If the spec file is not
  inside a folder named `.pointframe` (compared without regard to case), the project
  root is the folder that holds the spec file itself.
- The verdict and logs go to `artifacts/pointframe-verify/` under the project root.

The file is JSON. Comments (`//` and `/* */`) are skipped. Trailing commas are not
allowed. Duplicate property names in any object are rejected. The top level must be
an object. A missing file or invalid JSON is also `spec_invalid`.

## How to read the tables

- All names are case-sensitive and written in camelCase, as shown.
- An unknown property in any object is an error. The message names the property and
  lists the allowed ones. The CLI checks for unknown properties in an object before it
  reads that object's values, so an unknown top-level field is reported before a bad
  `schemaVersion`.
- A "non-empty string" must be a JSON string that is not empty and not only
  whitespace.
- A "whole number" must be a JSON number that fits in a 32-bit integer.
- An "identifier" is a non-empty string of letters `A-Z a-z`, digits, `-`, `_`, and
  `.` only.
- A relative path is resolved against the project root. An absolute path is used as
  it is. The loader accepts `..` and does not check that a path stays inside the
  project. The approval step does (see [Command approval](#command-approval)).

## Top level

| Field | Type | Required | Default | Rules |
|---|---|---|---|---|
| `schemaVersion` | whole number | yes | none | Must be `1`. |
| `app` | object | only if there are scenarios | none | See [`app`](#app). |
| `gates` | array of objects | no | none | See [Gates](#gates). |
| `scenarios` | array of objects | no | none | See [Scenarios](#scenarios). |

Rules for the whole spec:

- The spec must have at least one gate or one scenario. An empty `gates` array and an
  empty `scenarios` array count as none.
- If there is at least one scenario, `app` is required.
- `app` without scenarios is allowed. The app is still validated and still covered by
  the approval.
- Any other `schemaVersion` gives `spec.schemaVersion <n> is not supported; expected 1.`

## `app`

| Field | Type | Required | Default | Rules |
|---|---|---|---|---|
| `id` | identifier | yes | none | Not checked for uniqueness. |
| `executable` | non-empty string | yes | none | A path. Relative to the project root. |
| `arguments` | array of strings | no | empty | Passed to the app. Strings may be empty. |
| `workingDirectory` | non-empty string | no | the executable's folder | A path. Relative to the project root. |
| `isolation` | object | no | none | See [Isolation](#isolation). |

### Isolation

`isolation` gives every scenario a fresh data folder. It takes exactly one of these
two properties. Giving both, or neither, is an error.

| Field | Type | Rules |
|---|---|---|
| `environmentVariable` | non-empty string | Starts with a letter or `_`. Only letters, digits, and `_`. Must not be one of these system variables (any case): `PATH`, `PATHEXT`, `COMSPEC`, `SYSTEMROOT`, `WINDIR`, `TEMP`, `TMP`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `PROGRAMDATA`, `HOMEDRIVE`, `HOMEPATH`, `USERNAME`. Empty and whitespace-only values are invalid. |
| `argument` | non-empty string | One option. Starts with `-` and holds no whitespace, for example `--data-dir`. The CLI passes the folder after it. |

The spec never names the folder. See the [CLI README](README.md#isolation) for how
the folder is created and removed.

## Gates

`gates` is an array. Each gate is an object.

| Field | Type | Required | Default | Rules |
|---|---|---|---|---|
| `id` | identifier | yes | none | Unique among gates. Letters are compared without regard to case, so `Build` and `build` clash. |
| `run` | non-empty string | yes | none | The command line to run. |
| `workingDirectory` | non-empty string | no | the project root | A path. Relative to the project root. |
| `timeoutMinutes` | whole number | no | `30` | From `1` through `120`. |

## Scenarios

`scenarios` is an array. Each scenario is an object.

| Field | Type | Required | Default | Rules |
|---|---|---|---|---|
| `id` | identifier | yes | none | Unique among scenarios, compared without regard to case. |
| `criteria` | array of strings | no | empty | At most 50. After trimming, each is not empty and at most 500 characters. |
| `steps` | array of objects | yes | none | Not empty. See [Steps](#steps). |

Criteria are numbered `C1`, `C2`, and so on, in order. Rules that tie criteria to
checks:

- A check that names a `criterion` must name one the scenario declares. The match is
  exact, so `c1` does not match `C1`.
- If a scenario has criteria, every criterion needs at least one check that names it.
- If a scenario has criteria, it needs at least one negative control: a check with
  `"expectFailure": true`.
- A scenario with no criteria needs neither. It may still hold checks.

## Steps

Each step is an object with exactly one property. The property name is the step
kind, and its value is an object. Anything else is an error.

| Step | Body fields | Notes |
|---|---|---|
| `enterText` | an element, and `text` | `text` is required, and may be an empty string. |
| `invoke` | an element | |
| `pressKeys` | `keys` | `keys` is required. An array of 1 to 4 whole numbers, each from 1 through 65535, which are Windows virtual-key codes. Put modifiers first. |
| `restart` | none | The body must be `{}`. |
| `check` | see [Checks](#checks) | |

An element names the control. Use one of these two forms:

- `automationId`: a non-empty string. Do not add `role` or `name`.
- `role` and `name`: both non-empty strings. Do not add `automationId`.

Any other mix is an error.

## Checks

A `check` body holds an element (as above) and these fields.

| Field | Type | Required | Default | Rules |
|---|---|---|---|---|
| `kind` | string | yes | none | One of `exists`, `absent`, `enabled`, `toggleEquals`, `selectionEquals`, `textEquals`. Case does not matter; the CLI uses the listed spelling. |
| `automationId`, `role`, `name` | non-empty strings | see element rules | none | |
| `expected` | string, `true`, `false`, or number | for `toggleEquals`, `selectionEquals`, and `textEquals` | none | A number is kept as its written text. `true` and `false` become the text `true` and `false`. `exists`, `absent`, and `enabled` reject this property. |
| `criterion` | non-empty string | no | none | A declared criterion, such as `C1`. Not allowed together with `"expectFailure": true`. |
| `expectFailure` | `true` or `false` | no | `false` | `true` makes the check a negative control. Any other value is an error. |
| `timeoutSeconds` | whole number | no | `10` | From `1` through `30`. |

## Command approval

Gate commands and the app run with your permissions, so the CLI approves them before
the first run: by fixed rules, by policy for standard commands (decision D-011 in the
[knowledge base](../knowledge-base/features/cli-mcp.md)), by the approver agent, or by
you with `pointframe verify trust`. The approval digest covers the gates and the app
(`id`, `run`, working folder, and timeout of each gate; and the app's id, executable,
arguments, working folder, and isolation, with paths resolved to full paths). Steps,
criteria, and checks are not covered. Changing anything in the digest asks for
approval again. The details are in the guide:
[Approval of the commands](verify.md#approval-of-the-commands).

## Examples

A spec with gates only:

```json
{
  "schemaVersion": 1,
  "gates": [
    { "id": "build", "run": "dotnet build -c Release" },
    { "id": "tests", "run": "dotnet test -c Release --no-build", "workingDirectory": "src", "timeoutMinutes": 20 }
  ]
}
```

A spec with an app and one scenario. The first check covers criterion `C1`. The
second check is the negative control: it expects the wrong text, and it is meant to
fail.

```json
{
  "schemaVersion": 1,
  "app": {
    "id": "my-app",
    "executable": "MyApp/bin/Release/net10.0-windows/MyApp.exe",
    "arguments": ["--no-splash"],
    "isolation": { "environmentVariable": "MYAPP_DATA_DIR" }
  },
  "gates": [
    { "id": "build", "run": "dotnet build -c Release" }
  ],
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

## Output files (not frozen)

The files below are written by the CLI. They are not part of the frozen spec, and
they may gain fields within version 1. A reader must ignore fields it does not know.
Do not fail on an unknown field in these files.

### `verdict.json`

`pointframe verify run` writes `artifacts/pointframe-verify/verdict.json` under the
project root and prints the same JSON. Property names are camelCase. A property
whose value is empty (null) is left out.

| Field | Meaning |
|---|---|
| `schemaVersion` | The verdict's own version, currently `1`. It is separate from the spec's. |
| `status` | `pass`, `partial`, or `fail`. `partial`: everything that ran passed, but `--scenario` or `--only` left something out. |
| `complete` | `true` only when `status` is `pass`. |
| `startedUtc`, `seconds` | When the run started, and how long it took. |
| `specPath`, `specSha256` | The spec file used, and its SHA-256. |
| `provenance` | `head`, `treeHash`, `verifierVersion`, `mcpExecutablePath`, `mcpSha256`, `commandsApprovedBy`. Which of these appear depends on the run. |
| `only`, `scenarioFilter` | The `--only` and `--scenario` values, when given. |
| `task` | The frozen task, when `--task` was used. |
| `gates` | One entry per gate that ran: `id`, `run`, `status` (`pass` or `fail`), `exitCode` (left out on a timeout or when the gate could not start), `seconds`, `log`, `details`. |
| `scenarios` | One entry per scenario: `id`, `status` (`pass`, `fail`, or `skipped`), `reportVerdict`, `criteria`, `steps`, `bundleDirectory`, `proofValid`, `proofKeyId`, `problems`. |
| `errorCode`, `error`, `details` | Present when the run could not finish, with a code such as `spec_untrusted`, `approver_unavailable`, `app_not_found`, `mcp_not_found`, or `desktop_busy`. |
| `warnings` | Additive run notes. A scenario run with no gates warns that the app binary may not match the current files. |
| `noBuildGate` | `true` when the spec has at least one scenario and no gates. Such a passing verdict is never fresh; add a build gate before relying on it. |

### `review.json`

`pointframe verify hook stop --review` writes `artifacts/pointframe-verify/review.json`.
It always holds `treeHash` and `baseCommit`. A finished review adds a `review`
object: `summary`, `flags` (each with `file`, `line`, `severity`, and `message`), and
`reviewer`. A failed review instead adds `"failed": true` and `error`.

### Exit codes of `verify run`

| Exit code | When |
|---:|---|
| `0` | `status` is `pass` or `partial`. |
| `1` | `status` is `fail`: a gate or scenario failed, or the run could not start (`spec_untrusted`, `approver_unavailable`, `app_not_found`, `mcp_not_found`, `desktop_busy`). |
| `2` | The spec or arguments are wrong: `spec_invalid`, `scenario_not_found`, `task_not_found`, or `task_invalid`. No `verdict.json` is written. The verdict JSON, with `status` `fail` and the `errorCode`, is still printed. |

Read `status`, not only the exit code, because `partial` also exits `0`.
