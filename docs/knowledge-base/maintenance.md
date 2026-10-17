# Maintaining the knowledge base

How this knowledge base is structured, checked, budgeted, measured, and maintained: conventions, the task suite, workflow rule IDs, and the maintenance commands.

Part of the [Pointframe knowledge base](knowledge-base.md).

## Maintenance rules

### Structure and conventions

**Decisions.** A decision records why a choice was made over the obvious alternative, so nobody reverses it by accident. It lives with what it governs: a decision that only one area needs is in that area file, and one that spans areas is in [decisions.md](decisions.md). Numbers are global across files and never reused; a reversed decision stays, with "Superseded by D-NNN" on its first line. The list is generated.

**This file's structure.** A generated [Feature index](knowledge-base.md#feature-index), the [Architecture rules](knowledge-base.md#architecture-rules), then four fixed groups, each a `##` heading with one `###` section per topic (Decisions holds only a generated index). `references.md` and `tooling.md` follow the References layout, and the File map lives in `file-map.md`.

| Group | Section layout |
|---|---|
| Subsystems | Responsibility, Entry points, Flow, Key types, Invariants, Tests, Files, Lessons |
| Decisions | A generated index only. Each decision is a `## D-NNN Title` section in `decisions.md` or its area file: decided date, Context, Decision, Consequences, Alternatives rejected, Files. Numbers are global and never reused; a reversed decision stays with "Superseded by D-NNN" on its first line |
| Invariants | Rule, Why, Enforced by, Symptoms when violated, Files |
| How-tos | When, Steps, Verify, Files |
| References | Tables or short lists, each fact with its source, Files |

**A feature area file's structure.** `# <Area>`, a one-line scope, then `## Features`: one row per user-facing feature, `| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |`, with a stable `F-NN` ID that is never renumbered or reused. Then one `##` section per topic, using the Subsystems, Invariants, or How-tos layout above. A new area gets a new file; split a file that passes about 150 lines.

**Sections.** A section is a `##` or `###` heading and the text up to the next heading of any level. A `###` section also carries the text between its `##` heading and the first `###` (its parent preamble), so reading it costs both. A `##` heading that only groups `###` sections is a container: `kb read` and the File map point at the `###` sections, and a File map or feature row that links a container is an error. In an area file the `## Features` table is not a section.

**Conventions the script checks.** In every file: repo paths in backticks exist; `- Lesson: <exact heading from lessons.md>` bullets match `lessons.md`; links such as `[Recording pipeline](features/recording.md#recording-pipeline)` resolve to a heading in the target file; a section has at most one `**Files.**` line; no two headings in one file share an anchor.
Feature IDs are unique across files; every feature names an entry point and links a section; every telemetry event named exists in `TelemetryEvents`, and every event there belongs to some feature; every smoke test in `Pointframe.AutomationTests/Smoke/` belongs to some feature. Every File map pattern matches a tracked file, and every tracked file matches a row.
In `lessons.md` every lesson has exactly one Problem, What fixed it, and Takeaway subsection (Root cause is optional) and is reachable: a `- Lesson:` line in a section that a File map row, a feature row, or a task-suite task reaches. The table of contents, the Feature index, and the decision index are generated. CI runs `kb.ps1 check -NoFix`, `scripts/verify.ps1` runs `kb.ps1 selftest` first, and the project hook in `.claude/settings.json` runs `kb.ps1 hook` when an agent reads or edits a file; it says each section, feature, and lesson once per session.

**Writing rules.** Say why, not only what. Replace stale text in place; never append a correction below an old statement. Prefer a table or a short list over prose. Keep a section under 8 KB (checked, see Budgets). Absolute dates only. No status badges or task journals.

### Budgets, task suite, and rule IDs

**Budgets.** A section over 8192 bytes (a `###` section counts its parent preamble) and this file over 12288 bytes are errors unless `docs/knowledge-base/kb-baseline.json` lists them at their current size or more. `kb.ps1 check` lowers a listed entry when the section shrinks and drops it when the section fits, and never raises one; a new or grown violation fails. Write new knowledge into a section that stays under the budget, or split. Aim for 8-10 KB in this file.
Warnings, not errors: a prose line over 600 characters (tables and `**Files.**` lines are exempt), an area file over 40 KB, and changelog phrasing in an area file (`since YYYY-MM-DD`, `added YYYY-MM-DD`, `As of YYYY`; decision dates are exempt).

**Measuring.** `pwsh scripts/kb.ps1 stats [-Json]` prints bytes and estimated tokens (bytes / 4) per file and section, the bytes `init` prints per part, and, for each task in `docs/knowledge-base/kb-tasks.json` (a file path, an `F-NN`, or a topic), the required-section, lesson, and hook bytes and the recall of its expected sections and lessons. `check` fails when a task's recall drops below 1.0, so a routing change that hides a section or lesson is caught. `init` with a target aims at 25 KB in total and prints an overage line when a part passes its budget: core 12 KB, environment 1.5 KB, status 2 KB, target 12 KB, and rules 4 KB for an executor, reviewer, or helper but 16 KB for the coordinator, who owns review, merge, status, and model allocation and so carries most of `workflow.md`. A new rule that pushes a role past its budget should be shortened or moved into a section the role reads on demand.

**Workflow rule IDs.** `workflow.md` marks each canonical rule with `<!-- rule:W-NN roles=<all|coordinator|executor|reviewer|helper list> -->` on the line before its bullet, paragraph, or table, and names extra sections a role should read with `<!-- rule-section roles=... -->` before a `##` heading. IDs are stable and never reused. `kb init` prints the rules of the agent's role verbatim, with their IDs; `check` fails on a missing or duplicate ID, an unknown role, or a trigger that points at no heading. Each marker needs a nonempty list of valid roles, and every block below the first `##` heading must be a marked rule or sit under a trigger. The ID and roles of every rule are also registered in `docs/knowledge-base/kb-baseline.json` (`workflowRules`): `check -NoFix` fails when a registered ID is missing, its roles changed, or an ID is unregistered, because a trigger can hide a deleted marker. `check` without `-NoFix` only adds new IDs; removing a rule or changing its roles is a hand edit of the JSON, visible in review.

### Maintenance commands

**Maintenance.** Five skills, all backed by `scripts/kb.ps1`. The owner types two: `/kb-init` to start and `/kb-done` to finish. `kb-read`, `kb-write`, and `kb-check` are model-only (`user-invocable: false`, hidden from the `/` menu); agents call them on their own. Edit them only under `.claude/skills/kb-*`; `check` regenerates the Codex copies in `.agents/skills/`.

| Skill | Does | Script |
|---|---|---|
| `/kb-init [coordinator\|executor\|reviewer\|helper] [target]` | Starts a session: this file, the role's workflow rules, environment facts, and the target's sections and top lessons (a menu for a broad topic) | `pwsh scripts/kb.ps1 init [-Role <role>] [target]`; a Haiku helper subagent cannot type slash commands, so its brief tells it to run `pwsh scripts/kb.ps1 init -Role helper` (or pastes that output) |
| `kb-read <file, area, F-NN, or topic>` | Lists the sections, features, and lessons to read | `pwsh scripts/kb.ps1 read <target> [-All]` |
| `kb-write [topic]` | Adds or updates knowledge within the budgets; with no topic, sweeps the current diff | `pwsh scripts/kb.ps1 changed [-Base <ref>]` |
| `kb-check` | Refreshes the generated blocks and the baseline, checks every file, fixes what it reports | `pwsh scripts/kb.ps1 check` (CI: `check -NoFix`) |
| `/kb-done [coordinator\|executor\|reviewer]` | Closes the work: updates the knowledge base or records it as reviewed unchanged, runs `check`, `dotnet format` and `verify.ps1`, and for a coordinator writes the status checkpoint. Never commits, pushes, or merges | `pwsh scripts/kb.ps1 changed -Base <merge-base>`, `check`, `pwsh scripts/verify.ps1` |

`pwsh scripts/kb.ps1 selftest` runs the tool against synthetic fixtures in `scripts/kb-fixtures/` (section routing, hook dedup, baseline growth and shrinkage, rule IDs, `init` order and role filtering, the status allowlist, topic menus); `scripts/verify.ps1` runs it before `check -NoFix`.

When a change adds a folder, a project, or a file that belongs to a different section than its folder's row says, add or narrow a File map row in the same change; a row may not be broader than the section that owns its files. When it adds, removes, or rewires a user-facing feature, a telemetry event, or a smoke test, update the feature's row in its area file in the same change.

**Optional SessionStart hook.** To print `kb init` at the start of every Claude Code session, add this to your own `settings.local.json` in the `.claude` folder (it is gitignored); do not commit it to `settings.json`. The guard keeps a resumed or compacted session from printing it twice, because a `SessionStart` hook receives the session ID on standard input:

```json
{
  "hooks": {
    "SessionStart": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -Command '$j = [Console]::In.ReadToEnd() | ConvertFrom-Json; $g = Join-Path $env:TEMP \"pointframe-kb-init-$($j.session_id)\"; if (-not (Test-Path $g)) { New-Item $g -Force | Out-Null; & ./scripts/kb.ps1 init }'"
          }
        ]
      }
    ]
  }
}
```
