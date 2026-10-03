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
| A file or folder | `Pointframe/Views/OverlayWindow.Recording.cs` | Its File map rows, the features that use it, the sections to read in any knowledge base file, and the five most relevant `lessons.md` entries (`-All` for every one) |
| An area | `recording` | The area file, its features, and its sections with line numbers |
| A feature ID | `F-22` | Its trigger, entry point, telemetry events, tests, and sections |
| A topic | `hotkey` | Every feature and heading that names it |
| Nothing | | The cross-cutting file and every area file with its feature IDs |

Then:

1. Read each section it lists, at the line it gives. Read a lesson in `lessons.md` by its heading only when the change touches what the lesson describes.
2. The knowledge base is two layers: `docs/knowledge-base/knowledge-base.md` for cross-cutting knowledge, `docs/knowledge-base/features/<area>.md` for one area. Do not load area files the task does not touch.
3. Verify claims about behavior against the code before relying on them. If the knowledge base is wrong or missing something, fix it with `/kb-write` before finishing.

The project hook runs the same lookup the first time a session reads or edits a file in an area, so you may already have this guidance in context.
