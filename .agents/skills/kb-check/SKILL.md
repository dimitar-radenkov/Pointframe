---
name: kb-check
description: Check the Pointframe knowledge base against the code and fix what it reports - paths, lessons, links across files, feature IDs, telemetry and smoke-test coverage, File map coverage, and the generated table of contents, Feature index, and Codex skill copies. Use when the user types /kb-check; after /kb-write; after renaming, moving, or adding files, telemetry events, or smoke tests; or when CI's "Knowledge base check" step fails.
---

# /kb-check: check and fix

```powershell
pwsh scripts/kb.ps1 check          # refresh the generated parts, then check everything
pwsh scripts/kb.ps1 check -NoFix   # check only, write nothing (what CI runs)
```

`check` rewrites three generated things, so never edit them by hand: the table of contents and the Feature index in `docs/knowledge-base/knowledge-base.md`, and the Codex copies of the `kb-*` skills in `.agents/skills/`. Edit skills only under `.claude/skills/kb-*`.

Fix every `ERROR`, then run `check` again until it prints `0 errors`:

| Error says | Fix |
|---|---|
| path does not exist | The file moved or was renamed: update the path, or the section if behavior moved with it |
| path is gitignored | `*.md` is ignored by default: add a `!` negation to `.gitignore`, or drop the path |
| lesson heading not found | Copy the exact `## ` heading from `lessons.md` |
| broken link | Point it at an existing heading; another file's section is `other.md#anchor` or `../knowledge-base.md#anchor` |
| tracked file matches no File map row | Add or widen a File map row in the main file |
| File map pattern matches no tracked file | The folder or file is gone: narrow or remove the pattern. A brand-new file must be tracked (`git add -N`) before it counts |
| File map row is redundant | Delete it, or give it a link the wider row does not have |
| telemetry event belongs to no feature | Add the event to the `F-NN` row that emits it |
| smoke test belongs to no feature | Add the test to the `F-NN` row it covers |
| feature ID is also used | IDs are never reused: give the new feature the next free `F-NN` |
| more than one **Files.** line | A block landed under the wrong heading: move it |
| is out of date | Run `check` without `-NoFix` |

A `note` is not an error. "lesson not linked from any section" means `kb.ps1 read` will never show that lesson: link it from the owning section with `/kb-write`, unless its feature is still on an unmerged branch.
