---
name: knowledge-base
description: Add or update sections in the Pointframe knowledge base, the single file docs/knowledge-base/knowledge-base.md (subsystems, decisions, invariants, how-tos, references; paths, lesson references, and links are checked by script). Use when the user types /knowledge-base; asks to document or write down architecture, a subsystem, a decision, an invariant, a how-to, or a reference fact; or when a finished feature, refactor, or fix changed how a subsystem works and the KB should reflect it. Subcommands - add, update.
---

# /knowledge-base: Pointframe knowledge base

One file: `docs/knowledge-base/knowledge-base.md`. Read its "How to maintain this file" section once per session before editing; it holds the section templates and conventions. The table of contents is generated; do not edit it by hand.

Script: `pwsh .claude/skills/knowledge-base/knowledge-base.ps1` refreshes the table of contents and reports every backticked repo path that does not exist, every `- Lesson:` line that does not match a `## ` heading in `lessons.md`, and every `(#anchor)` link that points nowhere. Both subcommands end by running it and fixing everything it reports. `-Check` only reports.

## Where a fact goes

| The fact is... | Goes to |
|---|---|
| How a part of the app is composed, its entry points, its flows | `## Subsystems` |
| A choice with lasting impact and a reason | `## Decisions`, as `### D-NNN Title` with the next number |
| A rule the code must keep, with why and what breaks | `## Invariants` |
| A recipe for a recurring change | `## How-tos` |
| A stable table of paths, lifetimes, pipelines, tools | `## References` |
| A bug, its root cause, and the fix | `lessons.md` (Problem, Root cause, What fixed it, Takeaway), then a `- Lesson: <heading>` line in the owning section |
| A task, plan, status, or roadmap item | `plan/`, not the knowledge base |

Not knowledge: test counts, PR numbers, "verified on my machine", what was tried and abandoned, anything derivable from `git log`.

## `/knowledge-base add <group> <title>`

1. Search the file's `###` headings and text for the topic. If a section already covers it, use `/knowledge-base update` instead of adding a near-duplicate.
2. Read the code before writing. Every path you name must exist; every claim about behavior comes from the file, not from memory. Say why, not only what.
3. Append a `### <Title>` section at the end of the right `##` group, using that group's template from "How to maintain this file". Keep it under about 80 lines; prefer tables and short lists.
4. End the section with a `**Files.**` line listing the repo paths it describes, `See [...](#anchor)` links to related sections, and `- Lesson:` lines for related post-mortems (exact headings from `lessons.md`).
5. Run `pwsh .claude/skills/knowledge-base/knowledge-base.ps1`, fix everything it reports, and confirm the new section appears in the table of contents.

## `/knowledge-base update [section]`

With a section name, refresh that section. Without one, sweep: look at `git status --porcelain`, `git diff --stat`, and the conversation, and refresh every section whose `**Files.**` changed or whose subject changed.

1. Read the section and each file on its `**Files.**` line.
2. Rewrite stale sentences in place. Never append "Update:" or "Correction:" paragraphs.
3. Reversing a decision: add a new `### D-NNN` with the next number and put "Superseded by D-NNN" as the first line of the old one. Never renumber.
4. A rule, recipe, or subsystem with no section yet goes through `/knowledge-base add`. A bug post-mortem goes to `lessons.md`, then gets a `- Lesson:` line in the owning section.
5. If you compared every section against the code, move the "Last full review" date at the top to today; otherwise leave it.
6. Run `pwsh .claude/skills/knowledge-base/knowledge-base.ps1` and fix everything it reports. Report what was added, what was updated, and anything you saw but left alone.

## Quality bar before finishing

- The script prints no errors.
- The section explains what the code cannot: why, invariants, entry points, what breaks. It does not restate the code line by line.
- Dates are absolute. No status badges, no "currently" without a date.
- `CLAUDE.md` gets at most one line per topic; detail lives in the knowledge base.
- Nothing was committed. The user reviews and commits.
