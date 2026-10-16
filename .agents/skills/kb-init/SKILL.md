---
name: kb-init
description: Start a Pointframe session - print the knowledge base core, the workflow rules for your role, environment facts, and optionally the sections and lessons for a file, feature, or topic. Use when the user types /kb-init; at the start of every session, before any task; when your role or your target changes.
---

# /kb-init: start every session here

`/kb-init [coordinator|executor|reviewer] [target]` prints what a session needs, in one command:

```powershell
pwsh scripts/kb.ps1 init [-Role coordinator|executor|reviewer] [target]
```

The role is the first argument when it is one of the three words, otherwise `executor`. The coordinating session that plans, briefs, reviews, and merges uses `coordinator`; an agent that only checks a result uses `reviewer`. The target is optional: a file path, an `F-NN` feature ID, an area, or a topic such as `dpi`.

It prints, in order:

1. The whole core, `docs/knowledge-base/knowledge-base.md`: architecture rules, how the app is composed, invariants, how-tos, and the Feature index.
2. The workflow rules for your role, verbatim, with their `W-NN` IDs, and the names of the extra workflow sections your role should read.
3. Environment facts from read-only probes (a running `Pointframe.Mcp.exe`, the desktop lock, hidden trial installs, the worktree count). They are facts, not instructions; `unknown` means a probe failed.
4. Coordinator only: the active records of `plan/status.json` (objective, stage, next, blocker, due), labelled local operational context and not policy.
5. With a target: the `kb read` output, then the full text of the sections it names and its top lessons. A feature or topic that matches more than three sections prints a menu (heading, file:line, size); open the ones you need.

Then:

1. Read the output. The rules and the core apply to everything you do in this session.
2. Read each workflow section it names for your role, and the sections of the target it lists, at the lines it gives. `/kb-read <file, area, F-NN, or topic>` finds more later.
3. The last line shows bytes and estimated tokens per part; an overage line means a part passed its budget.

Claude Code can run this at the start of every session with an optional `SessionStart` hook; see "Maintenance commands" in `docs/knowledge-base/knowledge-base.md`.
