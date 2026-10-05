# Make your agent finish only on a passing check

`pointframe verify` runs your project's own build and test commands and writes a
verdict. A Stop hook then keeps your coding agent (Claude Code or Codex) working
until the last verdict is a fresh pass. This page shows how to set it up in your
own project. The full reference is in the [CLI README](README.md#verify-a-desktop-app-from-a-spec).
The fields of the spec file are in the [spec reference](verify-spec.md).

## What it does, and what it does not

- It runs the commands in `.pointframe/verify.json`, called gates. For a .NET
  solution these are `dotnet build` and `dotnet test`. For a Node project they
  are your `npm` scripts.
- A pass is tied to the exact files in your working tree. Change a file and the
  pass is no longer fresh.
- When the agent tries to stop without a fresh pass, the hook runs the checks. If
  they fail, it blocks the stop and tells the agent what failed.
- It does not prove your code is correct. It proves that your own checks pass.
- It runs on Windows x64. The toolchain your gates call (the .NET SDK, Node, and
  so on) must already be installed.
- Desktop scenarios, which drive a running Windows app, are optional and not needed
  here. See [Advanced](#advanced-desktop-scenarios-and-tasks).

## Install

Use the release ZIP:

1. Download `Pointframe.Cli-<version>-win-x64.zip` and its `.sha256` file from the
   [latest release](https://github.com/dimitar-radenkov/Pointframe/releases/latest).
   Use the file with the version in its name; `Pointframe.Cli-win-x64.zip` is the
   same build under a stable name for scripts.
2. Check the hash. The two values must match:

   ```powershell
   (Get-FileHash .\Pointframe.Cli-<version>-win-x64.zip -Algorithm SHA256).Hash
   Get-Content .\Pointframe.Cli-<version>-win-x64.zip.sha256
   ```

3. Extract the ZIP, and from that folder run:

   ```powershell
   .\Pointframe.Cli.exe install
   ```

   This copies the CLI to `%LOCALAPPDATA%\Programs\Pointframe.Cli` and adds that
   folder to your user `Path`.
4. Open a new terminal, and restart your agent so it sees the new `Path`. A
   terminal or agent that was already running keeps its old `Path`: there,
   `pointframe` is not found and `verify init` warns that `pointframe.exe` was not
   found on PATH. That warning is expected until the restart; the install worked.
5. Check the install:

   ```powershell
   pointframe --version
   where.exe pointframe
   ```

   `where.exe` should list `%LOCALAPPDATA%\Programs\Pointframe.Cli\pointframe.exe`
   first.

The winget package `DimitarRadenkov.Pointframe.Cli` is awaiting acceptance in the
winget repository; until `winget search DimitarRadenkov.Pointframe.Cli` finds it,
use the ZIP. Scoop is not supported.

## Set up a project

Run this from the project root:

```powershell
pointframe verify init
```

By default it configures both agents. You can choose:

```powershell
pointframe verify init --hooks claude --agents-md
pointframe verify init --hooks codex
pointframe verify init --hooks both
```

`--hooks` takes `claude`, `codex`, `both`, or `none`. `--agents-md` also adds a
short section to `AGENTS.md` that tells agents to run verification before they
say they are done.

What init writes:

- `.pointframe/verify.json`: the spec, with the detected gates.
- `.claude/settings.json` and/or `.codex/hooks.json`: a Stop hook that runs
  `pointframe verify hook stop --review`. Existing settings are kept.
- `AGENTS.md`: a marked section, only with `--agents-md`.
- `.gitignore`: `artifacts/pointframe-verify/` is added when the file exists and
  does not already cover it.

Init prints one line of JSON with `status`, the files it wrote, the files it left
alone, the gates, `warnings`, and `nextSteps`. Running it again is safe: the hook
and the `AGENTS.md` section are not duplicated, and an existing spec is left alone
unless you pass `--force`. If nothing changed, `status` is `unchanged`.

Commit `.pointframe/verify.json` and the hook files so the whole team gets them.

### Recipe: a .NET solution with tests

With `MyApp.slnx` or `MyApp.sln` in the project root and a test project that
references xUnit, NUnit, MSTest, or `Microsoft.NET.Test.Sdk`, init writes:

```json
"gates": [
  { "id": "build", "run": "dotnet build MyApp.slnx -c Release" },
  { "id": "tests", "run": "dotnet test MyApp.slnx -c Release --no-build" }
]
```

Without a test project, only the `build` gate is written.

### Recipe: Node with an npm test script

With a `package.json` in the root (and no solution file), init writes one gate for
each of the `build`, `lint`, and `test` scripts that exist:

```json
"gates": [
  { "id": "build", "run": "npm run build" },
  { "id": "lint", "run": "npm run lint" },
  { "id": "test", "run": "npm test" }
]
```

Init picks the package manager from the lockfile: `pnpm-lock.yaml` gives
`pnpm test` and `pnpm run <script>`; otherwise `yarn.lock` gives `yarn test` and
`yarn run <script>`; otherwise (`package-lock.json`, or no lockfile) it uses `npm`.
When more than one lockfile exists, init uses the `packageManager` field in
`package.json` if it names `pnpm`, `yarn`, or `npm`. Without that field it picks in
the same order and adds a warning that names the lockfiles it found. All of these
commands are standard, so they are approved by policy.

If init finds no gate, it writes nothing and says what is missing.

### Approval of the commands

Gate commands run with your permissions, so the CLI approves them before the first
run. Standard commands such as `dotnet build`, `dotnet test`, `npm test`, and
`npm run <script>` are approved automatically by policy, offline. A nonstandard
command goes to the approver agent. If that agent refuses or is not available, you
approve it yourself:

```powershell
pointframe verify trust
```

It shows the commands and asks you to type `yes`. The details are in
[Gates](README.md#gates).

## Run and read results

```powershell
pointframe verify run
pointframe verify status
```

`verify run` runs every gate, writes the verdict, and prints it as JSON. Its exit
code is `0` for `pass` or `partial`, `1` for `fail`, and `2` when the spec or the
arguments are wrong. Read `status` in the output, not only the exit code.

`verify status` answers one question: is the last verdict a pass for the files as
they are now? It prints:

- `status`: `pass`, `fail`, `partial`, or `none` when there is no verdict yet.
- `fresh`: `true` only for a `pass` made on the current files. The exit code is
  `0` when `fresh` is true and `1` otherwise.
- `freshnessReason`: when the pass is stale, `verdict_for_another_spec` takes
  priority over `tree_changed`, then `task_not_covered` (the verdict has no task)
  or `verdict_for_another_task` (the task id or frozen snapshot hash differs).
- `activeTask`: the active task id (omitted when there is none). With an active task, freshness
  requires the verdict to cover that id and the stored snapshot hash.
- `lastStop`: the parsed `last-stop.json` record (omitted when there is none). `unverifiedStop` is
  `true` when that record says `unverified` for the current tree hash.
- `hookCommand`: where `pointframe` resolves to on `PATH`. `hookCommand.ok` should
  be `true`.
- `review`: `status` is `none`, `reviewed`, or `failed`.

Files are written under the project folder:

- `artifacts\pointframe-verify\verdict.json`: the last verdict.
- `artifacts\pointframe-verify\runs\<time>\`: one log per gate.
- `artifacts\pointframe-verify\review.json`: the reviewer's result, if a review ran.
- `artifacts\pointframe-verify\last-stop.json`: the latest allowed Stop hook
  outcome. `outcome` is `verified` or `unverified`; unverified records include a
  reason (`no_verdict`, `needs_person`, `block_limit`, or `hook_error`).

A failed gate lists compiler errors, failed tests, or the end of its log in
`details`.

Do not use `--only gates` or `--scenario` when you check completion. A filtered
run is `partial`, and it is never `fresh`.

## See it work

1. In a test project, add a test that fails on purpose.
2. Ask your agent for a small change in the code.
3. When the agent tries to finish, the hook runs the gates and blocks the stop.
   The agent sees the failing gate and the test errors.
4. The agent fixes the cause. You can fix the test yourself if you meant it to
   pass.
5. The agent finishes again. The hook runs the gates, they pass, and the stop is
   allowed. `pointframe verify status` now shows `"status": "pass"` and
   `"fresh": true`.

## Stopped is not verified

The hook blocks a failing stop at most five times in a session (`--max-blocks`
changes this). After that the agent is allowed to stop, and the message starts
with `UNVERIFIED:`. `verify status` surfaces the recorded stop; `unverifiedStop`
is true only while that stop's tree hash matches the current tree.

The agent is also allowed to stop, with a message that the work was not verified,
when only a person can fix the problem. These are an untrusted spec, no available
approver, an invalid spec, a missing MCP server, a busy desktop, or a broken task.
Blocking would only loop.

So "the agent stopped" does not mean "the work is verified". Before you trust
"done", run:

```powershell
pointframe verify status
```

You want `status` to be `pass` and `fresh` to be `true`. For a project that
matters, also require a pass in CI.

## Run in CI

The hook is a local safety net. It can be absent (a fresh clone, a different
agent, a human editing by hand), skipped, or used up after five blocks. A CI job
is the check that nobody can skip, so make it a required check on pull requests.

The job below downloads a pinned release of the CLI, checks its hash, and verifies
the repository. It is the same recipe that
[`verify-samples.yml`](../../.github/workflows/verify-samples.yml) runs against the
two [sample projects](../../samples/verify) (.NET and Node).

```yaml
name: Verify

on:
  pull_request:

permissions:
  contents: read

env:
  POINTFRAME_CLI_VERSION: "6.7.34"

jobs:
  verify:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v7

      # Install the toolchain your gates call, for example:
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: "10.0.x"

      - name: Download and check the CLI
        shell: pwsh
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          $version = $env:POINTFRAME_CLI_VERSION
          $download = Join-Path $env:RUNNER_TEMP 'pointframe-cli-download'
          $cli = Join-Path $env:RUNNER_TEMP 'pointframe-cli'
          New-Item -ItemType Directory -Force $download | Out-Null
          gh release download "v$version" --repo dimitar-radenkov/Pointframe `
            --pattern "Pointframe.Cli-$version-win-x64.zip" `
            --pattern "Pointframe.Cli-$version-win-x64.zip.sha256" `
            --dir $download
          if ($LASTEXITCODE -ne 0) { throw 'Could not download the CLI release.' }
          $zip = Join-Path $download "Pointframe.Cli-$version-win-x64.zip"
          $expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
          $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
          if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual." }
          Expand-Archive $zip -DestinationPath $cli
          $exe = Get-ChildItem $cli -Recurse -Filter Pointframe.Cli.exe | Select-Object -First 1
          "POINTFRAME_CLI=$($exe.FullName)" | Out-File -Append -Encoding utf8 $env:GITHUB_ENV

      - name: Verify run
        shell: pwsh
        run: |
          & $env:POINTFRAME_CLI verify run
          $exitCode = $LASTEXITCODE
          $verdict = Get-Content 'artifacts/pointframe-verify/verdict.json' -Raw | ConvertFrom-Json
          if ($exitCode -ne 0) { throw "verify run exited with $exitCode." }
          if ($verdict.status -ne 'pass') { throw "Expected status pass, got '$($verdict.status)'." }
          if ($verdict.complete -ne $true) { throw 'The verdict is not complete.' }

      - name: Verify status
        shell: pwsh
        run: |
          $json = & $env:POINTFRAME_CLI verify status
          $exitCode = $LASTEXITCODE
          $status = ($json | Out-String) | ConvertFrom-Json
          if ($exitCode -ne 0) { throw "verify status exited with $exitCode." }
          if ($status.status -ne 'pass') { throw "Expected status pass, got '$($status.status)'." }
          if ($status.fresh -ne $true) { throw 'The pass is not fresh for the current files.' }

      - name: Upload verify output
        if: always()
        uses: actions/upload-artifact@v7
        with:
          name: pointframe-verify
          path: artifacts/pointframe-verify/
          if-no-files-found: ignore
```

Change `POINTFRAME_CLI_VERSION` to a release you have checked. Run the job from
the project root, where `.pointframe/verify.json` is; the checkout must be a git
repository, which `actions/checkout` gives you.

What to require, all three:

- `verify run` exits `0`. This also covers a failed gate (exit `1`) and a broken
  spec (exit `2`).
- The verdict has `status: "pass"` and `complete: true`. Exit `0` alone is not
  enough, because `partial` also exits `0`.
- `verify status` exits `0` with `fresh: true`: the pass belongs to the files as the
  job sees them, not to an older tree.

Things to know:

- Do not use `--only gates` or `--scenario` in the job. A filtered run is `partial`
  and is never `fresh`, so it is not a completion check.
- A fresh runner has no stored approvals, and nobody is there to type `yes`. The
  job approves commands by policy only: standard commands (`dotnet build`,
  `dotnet test`, `npm test`, `npm run <script>`, and the pnpm and yarn forms) pass.
  A nonstandard command cannot be approved there, so the job fails with
  `spec_untrusted` or `approver_unavailable`. Keep the gates standard, and put
  anything else inside a project script that a standard command calls.
- CI runs the spec from the pull request itself, so a pull request could weaken
  its own gates. Protect `.pointframe/` with CODEOWNERS or required review. The
  reviewer flags edits to the spec and to tests, but a person decides.
- Desktop scenarios drive a running Windows app and need an interactive desktop
  session. A hosted runner has none, so scenarios are not covered by this recipe
  yet. Keep them in your local checks.

## Agents

**Claude Code** reads the hook from `.claude/settings.json`. The hook has a
timeout of 1800 seconds, because Claude Code stops a hook after 30 seconds by
default.

**Codex** reads the hook from `.codex/hooks.json`. Codex runs project hooks only
when the project folder and the hook are both trusted, which you do once in Codex.
Keep `pointframe` on `PATH`: on Windows, Codex skips a hook command written as a
quoted path followed by arguments. A .NET project in Codex's `workspace-write`
sandbox also needs `-c sandbox_workspace_write.network_access=true`, or
`dotnet build` cannot restore packages.

**Cursor** can use a Stop hook; configure it in Cursor's project rules or hooks
settings to run `pointframe verify hook stop`. Copilot and agents without a
compatible Stop hook rely on `pointframe verify init --agents-md` instructions
and a required passing CI check.

The `--review` flag in the hook starts a reviewer agent after the first pass on a
new tree. It flags weakened tests and edits to the spec or hooks, and its flags
never block. Choose which agent plays the reviewer and the approver:

```powershell
pointframe verify agent
pointframe verify agent --use codex
pointframe verify agent --use claude
pointframe verify agent --use auto
```

## Troubleshooting

Init and `verify status` check which command the hook will run. Fix any of these,
then restart the agent:

- **`pointframe.exe` was not found on PATH.** The hook cannot run, so the agent
  can stop without a check. Run the install steps above. Right after `install`, in
  the same terminal or agent session, this warning only means that session has
  the old `Path`; restart it and run `pointframe verify status` again.
- **The command is not the Pointframe CLI** (`hookCommand.ok` is `false`). Another
  program named `pointframe` is earlier on `PATH`, for example an old shim. Remove
  it or move it later on `PATH`.
- **The version differs from the CLI you ran.** This is a warning only. The hook
  command stays the same across CLI versions.
- **The review failed.** `review.json` has `"failed": true` and the error. The
  checks still passed, but the work was not reviewed. Run `verify status` and read
  `review.error`.
- **`spec_untrusted` or `approver_unavailable`.** Run `pointframe verify trust`.
- **A scenario step fails with `NoVisibleWindow`.** After the launch and after a
  `restart`, the runner waits up to 15 seconds for a visible window with UI
  elements. The app showed none in that time; check that it starts on this
  machine and is not hidden behind a splash or login dialog.
- **A step fails with `AmbiguousLocator`.** More than one element matched an
  input step's locator, and nothing was clicked. The message lists up to five
  candidates with role, name, automationId, and bounds; narrow the locator.
- **A step fails with `ToolError`.** The desktop tool returned an error with only
  a text message; the message carries that text.
- **`ElementNotFound` for an `automationId`.** WinForms menu items and many
  controls expose no AutomationId. When an observed element has that name, the
  message suggests `{ "role": "menu item", "name": "Options" }` instead.
- **A build gate fails because `Pointframe.Cli.exe` is locked.** Use the installed
  `pointframe` in the hook, not a build output that your own gates rebuild.

## Remove

To stop verifying a project:

1. Delete the hook entry that runs `verify hook stop` from `.claude/settings.json`
   and `.codex/hooks.json`.
2. Delete the `.pointframe/` folder and, if you want, `artifacts/pointframe-verify/`.
3. Delete the marked section (`<!-- pointframe-verify:start -->` to
   `<!-- pointframe-verify:end -->`) from `AGENTS.md`.

To uninstall the CLI, delete `%LOCALAPPDATA%\Programs\Pointframe.Cli` and remove
that folder from your user `Path`.

## Interactive desktop tools for your agent

If your project is a Windows desktop app and the spec has an `app`, one command gives your agent
interactive desktop-testing tools for exactly that app, under the same approval as `verify run`:

```powershell
pointframe verify setup
```

It approves the spec's commands (standard ones by policy, offline), makes sure the Pointframe MCP server is
installed, writes the project's agent configuration (`.mcp.json` for Claude Code; use `--client codex`,
`vscode`, or `all` for the others), and checks that the server starts and lists your app. Restart your agent
session afterwards. The agent can then call `desktop_list_apps`, which lists your app's profile, and the other
`desktop_*` tools to start it, click, type, and check its UI. Nothing else can be launched, and if the spec's
commands change, the server refuses to start until they are approved again (`pointframe verify trust`).
Details: [Give your agent interactive desktop tools](README.md#give-your-agent-interactive-desktop-tools).

## Advanced: desktop scenarios and tasks

If your project is a Windows desktop app, the spec can also hold scenarios that
drive the running app, and `pointframe verify task start` can freeze a task's
criteria before work begins. These need the Pointframe MCP server and take over the
mouse and keyboard while they run. See
[Verify a desktop app from a spec](README.md#verify-a-desktop-app-from-a-spec).

When the CLI-installed MCP server is older than the CLI, verification updates it to the latest release; if that fails, install it with `pointframe mcp install --client vscode` or pass `--mcp <path>`. A newer installed server is used with a warning.
