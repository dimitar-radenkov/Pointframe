# Pointframe plugin for Claude

Gives Claude eyes on your Windows desktop. The plugin starts the [Pointframe](https://github.com/dimitar-radenkov/Pointframe) MCP server locally, so Claude can list monitors and windows, capture a monitor, a region or a window and look at it, read on-screen text with Windows OCR, search earlier captures, and record a monitor to MP4 with pixelated redaction regions. It also ships three skills: `verify-desktop-work`, a workflow for proving that a change to a Windows desktop app works by checking its real UI and returning a signed report; `create-desktop-verification-skill`, which writes a `verify-<app>` skill for a desktop app repository; and `capture-screen`, which tells Claude how to look at the screen.

Pointframe is free and open source (MIT). The capture, OCR, and recording tools only observe; they never click or type.

## Requirements

- Windows 10 or 11 (x64) in a signed-in interactive desktop session. The plugin does nothing on macOS or Linux.
- Windows PowerShell 5.1, which ships with Windows. No .NET runtime, Node.js, Python, or the Pointframe desktop app is needed.
- Internet access on the first start, to download the server once.

## Install

Once the plugin is listed, install it from the `/plugin` marketplace in Claude Code. To test a local copy of this repository:

```
claude --plugin-dir ./plugin/pointframe
```

Then ask Claude to list your displays. It should call `list_displays`.

## What the plugin runs and downloads

`.mcp.json` starts one local stdio MCP server by running `scripts/start-mcp.cmd`, with no arguments. That two-line launcher runs this command, passes the arguments it receives through, and returns its exit code:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<plugin folder>\scripts\start-mcp.ps1"
```

`-ExecutionPolicy Bypass` applies only to this one PowerShell process, so the script runs on machines with the default Restricted policy; it does not change your system or user policy.

`scripts/start-mcp.ps1` is a short, readable script. It does the following and nothing else:

1. Reads `server.lock.json`, which pins one Pointframe release: its version, the download URL, and the SHA-256 of the archive. Open that file to see the version and hash this plugin version is pinned to; it is the only place they are written.
2. If there is no verified copy yet, downloads that exact release asset over HTTPS (TLS 1.2 or later) from GitHub:
   `https://github.com/dimitar-radenkov/Pointframe/releases/download/v<version>/Pointframe.Mcp-<version>-win-x64.mcpb`.
   The `.mcpb` file is a ZIP archive of the self-contained server, `Pointframe.Mcp.exe`, and `ffmpeg.exe` for recording. It is about 120 MB.
3. Checks the SHA-256 of the download against the pin. On a mismatch, or if the download fails, it prints an error to stderr, starts nothing, and exits with an error. It never runs an unverified file.
4. Extracts the archive to `%LOCALAPPDATA%\Pointframe\plugin-mcp\<version>\server\`, records the verified hashes in `.verified.json` there, and deletes other cached versions. Later starts re-check the executable against `.verified.json` and do not touch the network.
5. Runs `Pointframe.Mcp.exe` with stdin and stdout connected directly to Claude. The script itself never writes to stdout, which carries the MCP protocol.

It does not use `npx`, `uvx`, or any other package launcher, does not run any other script, and does not read credentials or environment secrets. The pin moves only when a new plugin version is published; a pull request in the repository updates `server.lock.json` to the new release and its hash.

## Privacy and telemetry

The server runs on your machine. Captures, OCR results, and recordings are written to your disk under `%LOCALAPPDATA%\Pointframe` and are not uploaded by the plugin. A screenshot reaches the model only when a capture tool returns it to Claude; pass `includeImage: false` to return metadata only.

Official builds of the server send one anonymous usage event per tool call to Azure Application Insights with IP masking: the tool name, the outcome (success, error, cancelled, denied, verification_failed, or invalid_request), a duration bucket, the host, the MCP client type, the version, and a random install ID that is not tied to your name, account, or machine. No arguments, file paths, screenshots, text, error messages, user names, or machine names are sent. To opt out, set the environment variable `POINTFRAME_TELEMETRY_OPTOUT=1` or `DO_NOT_TRACK=1` before starting Claude. Full details are in the [privacy policy](https://dimitar-radenkov.github.io/Pointframe/privacy.html).

## Enable desktop testing (optional)

The `verify-desktop-work` skill has two parts. The first needs nothing from this plugin: install the Pointframe CLI, write `.pointframe/verify.json` for your app, and run `pointframe verify run`. The CLI starts its own server for the run, approves standard commands by policy, and needs no policy file and no restart.

For interactive desktop tools, run `pointframe verify setup` in the project (it needs the `.pointframe/verify.json` with an `app`). It approves the spec like `pointframe verify run`, installs the server if needed, and writes the project's `.mcp.json` so Claude gets desktop tools for exactly that app, with no policy file to write. Restart Claude Code afterwards. This plugin's own server keeps desktop testing off.

If you run the server yourself without a spec, the desktop-testing tools need a policy file that lists the apps Claude may launch (see the [desktop-testing guide](https://github.com/dimitar-radenkov/Pointframe/blob/master/docs/mcp-desktop-testing/README.md)) and the flags `--desktop-testing --desktop-policy <file>`.

## Create a verification skill for your app

`create-desktop-verification-skill` reads a Windows desktop app repository and writes a `verify-<app>` skill for it, with a spec scenario that is proved once. Ask Claude to "create a verification skill for this app". The skill and its recipe (`skills/create-desktop-verification-skill/references/recipe.md`) are generated from the same guide as `verify-desktop-work` by the repository's `scripts/sync-agent-guides.ps1`; do not edit them by hand.

Without the Claude plugin, install the skill into any agent that supports the `skills` format:

```
npx skills add https://github.com/dimitar-radenkov/Pointframe/tree/master/plugin/pointframe --skill create-desktop-verification-skill
```

TODO(coordinator): run this command in a clean folder and mark it "verified on <date>" here. It has not been run yet.

## Uninstall and clear the cache

Remove the plugin with `/plugin`, then delete the cached server:

```
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Pointframe\plugin-mcp"
```

Captures you saved stay in `%LOCALAPPDATA%\Pointframe` until you delete them.

## Support

Report problems at [github.com/dimitar-radenkov/Pointframe/issues](https://github.com/dimitar-radenkov/Pointframe/issues). The plugin source is in the `plugin/pointframe` folder of that repository.
