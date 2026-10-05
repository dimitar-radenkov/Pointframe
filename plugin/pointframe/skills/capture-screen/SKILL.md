---
name: capture-screen
description: Look at the user's Windows screen with Pointframe. Use when the user asks you to look at, check, read or compare something on their screen, a monitor or an app window, or to read on-screen text such as an error dialog.
---

# Capture and look at the screen

The Pointframe MCP tools let you see the Windows desktop. They only observe; they never click or type.

1. Call `list_displays` to get the monitors, or `list_windows` to get visible windows with their handles, titles and process names.
2. To look at a whole monitor or part of one, call `capture_monitor` with a monitor name exactly as `list_displays` returned it. To look at one app, call `capture_window` with its handle from `list_windows`. The image comes back inline so you can see it.
3. To read text instead of describing pixels, call `read_text_from_window` or `read_text_from_monitor`. They use Windows OCR and are cheaper than an image when the text is all you need.
4. To find an earlier screenshot, call `search_captures` with a word from its filename or its on-screen text, then `get_capture` with the ID.

Tips:

- Capture the specific window instead of the whole monitor when you can; the image is smaller and shows only what matters.
- Pass `includeImage: false` when you only need the saved file path and metadata.
- A screenshot can contain private information. Capture only what the task needs, and tell the user what you captured.
- Captures are saved on the user's disk under `%LOCALAPPDATA%\Pointframe`; nothing is uploaded by the plugin.
- If a tool reports no displays, the server is not running in the signed-in interactive Windows session. Tell the user instead of retrying.
