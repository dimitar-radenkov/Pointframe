---
name: kb-read
description: Find what the Pointframe knowledge base says before you change or explain something - the sections, user-facing features (F-NN), and past-bug lessons for a file, an area, a feature ID, or a topic. Use when the user types /kb-read; before editing a file you have not worked on in this session; when asked how an area or feature works, where a feature lives, or what to watch out for.
---

# /kb-read: what to read first

Run one command, then read only what it names:

```powershell
pwsh scripts/kb.ps1 read <target> [<target> ...] [-All]
```

| Target | Example | You get |
|---|---|---|
| A file or folder | `Pointframe/Views/OverlayWindow.Recording.cs` | Its File map rows, the features that use it, the narrowest sections to read in any knowledge base file with their size, and the five most relevant `lessons.md` entries (`-All` for every one) |
| An area | `recording` | The area file, its features, and its sections with line numbers |
| A feature ID | `F-22` | Its trigger, entry point, telemetry events, tests, and sections |
| A topic | `hotkey` | Every feature and section heading that names it; when none does, the sections that mention it most |
| Nothing | | The cross-cutting file and every area file with its feature IDs |

Then:

1. Read each section it lists, at the line it gives. Read a lesson in `lessons.md` by its heading only when the change touches what the lesson describes.
2. A section is a `##` or `###` heading and its text; a `###` section also costs its parent `##` preamble, and the size after each section is what reading it costs. The core is `docs/knowledge-base/knowledge-base.md` (`/kb-init` prints it); `file-map.md`, `composition.md`, `how-tos.md`, `maintenance.md`, `references.md`, `tooling.md`, `decisions.md`, and `workflow.md` sit next to it, and `docs/knowledge-base/features/<area>.md` holds one area. Do not load files the task does not touch.
3. Verify claims about behavior against the code before relying on them. If the knowledge base is wrong or missing something, fix it with `/kb-write` before finishing.

The project hook runs the same lookup when a session reads or edits a file, and says each section, feature, and lesson once per session, so you may already have this guidance in context. `/kb-init <target>` prints the same sections in full.
