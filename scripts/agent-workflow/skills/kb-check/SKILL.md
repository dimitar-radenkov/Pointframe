---
name: kb-check
user-invocable: false
description: Check the {{slot:product}} knowledge base against the code and fix what it reports - paths, lessons, links across files, feature IDs, {{slot:check-coverage?}}File map coverage, and the generated table of contents, Feature index, and Codex skill copies. Use after writing to the knowledge base; after renaming, moving, or adding files{{slot:check-triggers?}}; or when CI's "Knowledge base check" step fails.
---

# /kb-check: check and fix

```powershell
{{slot:kb-command}} check          # refresh the generated parts, then check everything
{{slot:kb-command}} check -NoFix   # check only, write nothing (what CI runs)
```

`check` rewrites generated things, so never edit them by hand: the table of contents, Feature index, and decision index in `{{kb.core}}`, {{slot:check-generated-skills}}, and `{{kb.baseline}}` (it only lowers entries). {{slot:check-skill-edit}}

`{{slot:kb-command}} selftest` runs the tool against {{slot:check-selftest}}, and `{{slot:kb-command}} stats` prints bytes and estimated tokens per file, section, and task-suite task.

Fix every `ERROR`, then run `check` again until it prints `0 errors`:

| Error says | Fix |
|---|---|
| path does not exist | The file moved or was renamed: update the path, or the section if behavior moved with it |
| path is gitignored | `*.md` is ignored by default: add a `!` negation to `.gitignore`, or drop the path |
| lesson heading not found | Copy the exact `## ` heading from `{{kb.lessons}}` |
| broken link | Point it at an existing heading; another file's section is `other.md#anchor` or `../other.md#anchor` |
| tracked file matches no File map row | Add or widen a row in `{{kb.root}}/file-map.md`, linking the narrowest section that owns the file |
| File map pattern matches no tracked file | The folder or file is gone: narrow or remove the pattern. A brand-new file must be tracked (`git add -N`) before it counts |
| File map row is redundant | Delete it, or give it a link the wider row does not have |
| links the container heading | A `##` heading that only groups `###` sections cannot be read; link one of its `###` sections |
| section is N bytes, over 8192 / the core file is over 12288 | Split or compress it. Only violations already in `{{kb.baseline}}` are allowed, at their listed size or less; a section that grew past its entry fails until it shrinks again |
| baseline lists a section that is within the limit | Run `check` without `-NoFix`; it drops the entry |
| rule ID / unknown role / rule-section trigger / malformed rule marker | Fix the `<!-- rule:W-NN roles=... -->` or `<!-- rule-section roles=... -->` line in `workflow.md`: unique `W-NN`, roles from `all`, `coordinator`, `executor`, `reviewer`, and a trigger directly before a `##` heading |
| lesson has N subsections / unknown subsection | A lesson needs exactly one `### Problem`, `### What fixed it`, and `### Takeaway`; `### Root cause` is optional |
| lesson is not linked from any section / only from sections nothing reaches | Add a `- Lesson:` line to the owning section, and make sure a File map row, a feature row, or a task reaches that section |
| task suite: task has recall below 1 | A routing change hid an expected section or lesson from `kb read`: fix the File map rows or links, or update `{{kb.tasks}}` when the expectation is really obsolete |
{{slot:check-coverage-rows?}}
| feature ID is also used | IDs are never reused: give the new feature the next free `F-NN` |
| more than one **Files.** line | A block landed under the wrong heading: move it |
| is out of date | Run `check` without `-NoFix` |

A `note` is not an error, and a `warn` (a prose line over 600 characters, an area file over 40 KB, changelog phrasing in an area file) does not fail the check but should be fixed when you touch that text.

## The project hook

`{{slot:kb-command}} hook` reads a Claude Code hook payload on stdin and, the first time a session touches an area, returns the sections, features, and lessons for that file as `additionalContext`. It exits 0 silently on any failure; set `KB_HOOK_DEBUG=1` to see why it produced nothing. It is enabled in the project's `.claude/settings.json`:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Read|Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -NonInteractive -File \"$CLAUDE_PROJECT_DIR/{{slot:kb-script}}\" hook",
            "timeout": 15
          }
        ]
      }
    ]
  }
}
```
