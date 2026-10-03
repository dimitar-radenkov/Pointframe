---
name: kb-write
description: Write to the Pointframe knowledge base - add new knowledge or update stale knowledge in docs/knowledge-base/knowledge-base.md (cross-cutting) or docs/knowledge-base/features/<area>.md (one area, with F-NN feature rows). Use when the user types /kb-write; asks to document, write down, or update architecture, a feature, a subsystem, a decision, an invariant, a how-to, or a reference fact; or when a finished feature, refactor, fix, rename, or move changed how an area works.
---

# /kb-write: add or update knowledge

`/kb-write <topic>` writes about one topic. `/kb-write` with no topic sweeps the current change: run `pwsh scripts/kb.ps1 changed` (against `HEAD`; add `-Base master` for a whole branch) and handle every section and feature it lists whose subject actually changed.

Read "How to maintain this file" at the top of `docs/knowledge-base/knowledge-base.md` once per session; it holds the templates and conventions.

## Where it goes

| The fact is... | Goes to |
|---|---|
| A user-facing feature: how it is triggered, its entry point, telemetry, and tests | A row in its area file's `## Features` table, with the next free `F-NN` across all files |
| How one area works: flow, entry points, key types, its lessons | A `##` section in `features/<area>.md` |
| A rule or recipe that only one area needs | A `##` section in that area file |
| Which sections to read before editing a folder or file pattern | A File map row in the main file |
| How the app is composed (host, DI, messaging) | `## Subsystems` in the main file |
| A choice with lasting impact and a reason | A `## D-NNN Title` section with the next number across all files: in the area file if only one area needs it, otherwise in `decisions.md` |
| A rule every area must keep, with why and what breaks | `## Invariants` in the main file |
| A recipe any area uses | `## How-tos` in the main file |
| A stable table of paths, lifetimes, pipelines, tools | `## References` in the main file |
| A bug, its root cause, and the fix | `lessons.md` (Problem, Root cause, What fixed it, Takeaway), then a `- Lesson: <heading>` line in the owning section |
| A task, plan, status, or roadmap item | `plan/` (local-only, not in git), not the knowledge base |

Not knowledge: test counts, PR numbers, "verified on my machine", what was tried and abandoned, anything derivable from `git log`.

## Steps

1. Look first: `pwsh scripts/kb.ps1 read <topic or file>`. If a section already covers it, update that section instead of adding a near-duplicate.
2. Read the code before writing. Every path you name must exist; every claim about behavior comes from the code, not from memory. Say why, not only what.
3. Write it in the file from "Where it goes":
   - Main file: a `### <Title>` section at the end of the right `##` group.
   - Area file: a `## <Title>` section, or a row in `## Features`. A new area is a new `features/<area>.md` with `# <Area>`, a one-line scope, and `## Features`.
   - Updating: rewrite stale sentences in place; never append "Update:" or "Correction:" paragraphs.
4. End a section with a `**Files.**` line, `See [...](...)` links (another file's section is `other.md#anchor` or `../knowledge-base.md#anchor`), and `- Lesson:` lines with exact `lessons.md` headings.
5. Keep the maps true in the same change:
   - A new, removed, or rewired feature, telemetry event, or smoke test: update its `F-NN` row. IDs are never renumbered or reused; a removed feature's ID stays retired.
   - A new, renamed, or moved folder or project: add or rewrite its File map row; link new sections from the rows whose files they describe.
   - A reversed decision: a new `## D-NNN`, and "Superseded by D-NNN" as the first line of the old one.
6. Keep a section under about 80 lines and an area file under about 150; prefer tables and short lists. Dates are absolute; no status badges.
7. If you compared every section against the code, move "Last full review" at the top of the main file to today.
8. Finish with `/kb-check`. Report what was added, what was updated, and anything you saw but left alone.

`AGENTS.md` gets at most one line per topic; detail lives in the knowledge base. Nothing is committed; the user reviews and commits.
