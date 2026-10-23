# Verify decisions

Why `pointframe verify` works as it does: decisions D-007 to D-014 on how verify drives the MCP server, who approves commands, which agents play its roles, version compatibility, the frozen spec, and interactive desktop testing.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs. The verify feature (F-39) and how it works are in [Verify](verify.md).

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|

## D-007 `verify run` drives the MCP server instead of sharing an Engine service

**Decided.** 2026-10-03.

**Context.** The plan was to move the act/check/record logic out of `DesktopTestingMcpTools` into `Pointframe.Engine` so the CLI could call it directly. But the UI Automation backend (FlaUI, the worker process, input) lives in `Pointframe.Mcp`, which the CLI does not reference.

**Decision.** The CLI is an MCP client: it starts `Pointframe.Mcp.exe` with a generated policy and calls the same `desktop_*` tools an agent calls.

**Consequences.** One implementation of sessions, checks, evidence, and signing; a CLI run and an agent session produce the same report, and every `verify run` exercises the agent path. The cost is a child process per run and a JSON boundary: a tool's argument or response change must be mirrored in `DesktopScenarioRunner`.

**Alternatives rejected.** Moving the backend and orchestration into the Engine (a large refactor of FlaUI and worker code, with two hosts to keep in step); referencing `Pointframe.Mcp` from the CLI (pulls the MCP host and FlaUI into the CLI package).

**Files.** `Pointframe.Cli/Verification/DesktopScenarioRunner.cs`, `Pointframe.Cli/Verification/McpStdioToolClient.cs`, `Pointframe.Mcp/Tools/DesktopTestingMcpTools.cs`.

## D-008 Verify approvals need a person at a terminal and live outside the repository

Superseded by D-009 for the approval of gate and app commands; it still holds for replacing a task snapshot and for where approvals are stored.

**Decided.** 2026-10-03.

**Context.** Gate commands run with the user's permissions, and a task's frozen criteria are what the worker agent is graded against. The worker runs as the same Windows user with a shell, so any plain approve command or `--yes` flag is one it can use itself.

**Decision.** `verify trust` and `verify task start --replace` approve only after a person types `yes` at an interactive terminal (`ConsoleConfirmation` refuses redirected input), with no flag to skip it. Approvals and task snapshots live under `%LOCALAPPDATA%\Pointframe\verify`, keyed by project folder, not in the repository. New criteria come from the examiner agent, never from the worker.

**Consequences.** An agent cannot approve its own gates or app, remove gates, or replace its own criteria through the CLI, and editing the repository does not change them. This is a consent boundary, not a sandbox: a worker with unrestricted file access could still edit the store, which only a sandbox that limits writes to the workspace prevents. Non-interactive CI needs its own trust mechanism (planned with CI support).

**Alternatives rejected.** An `ask` permission rule on the approve command (only Claude Code honours it); storing approvals in the repository (the worker can edit them); a `--yes` flag (the worker can pass it).

**Files.** `Pointframe.Cli/Verification/VerificationStore.cs`, `Pointframe.Cli/Verification/VerificationApplication.cs`.

## D-009 An approver agent behind fixed rules approves verify commands

Partly superseded by D-011 for standard commands.

**Decided.** 2026-10-04.

**Context.** Under D-008 every new or changed gate or app command waited for a person at a terminal. The owner wants agents to work autonomously and rejected a human step in the normal flow. Claude Code's auto-mode classifier refused to replace the person with an agent until the owner allowed it explicitly in the permission settings.

**Decision.** New or changed commands first face `CommandPolicy` fixed refusals (the app is a program inside the project and not a shell; working folders are inside the project; no URLs, network shares, download tools, deletes, encoded or inline payloads, system changes, or environment paths). Commands outside D-011's standard forms then go to `AgentApprover`: a separate run of the configured agent (D-010) started by the CLI, with no MCP tools and no project settings, that sees only the commands and the previously approved ones, treats them as untrusted data, and refuses when in doubt. Its yes is stored like a person's, with `ApprovedBy` and its reason; a person (`verify trust`) is needed after a refusal or an unavailable approver.

**Consequences.** Normal work never waits for a person. The approver can be talked into a wrong yes by text crafted for it; the fixed rules are the backstop for dangerous cases, and the verdict's `provenance.commandsApprovedBy` identifies an agent approval. An unavailable agent is distinguished from its refusal so the person knows the command was not judged.

**Alternatives rejected.** A person for every change (D-008: blocks autonomy); treating commands committed in HEAD as approved (blocked by the permission layer, and unsafe in cloned repositories and in workflows where agents commit); an approver without fixed rules (one wrong yes could run anything).

**Files.** `Pointframe.Cli/Verification/CommandApprover.cs`, `Pointframe.Cli/Verification/VerificationApplication.cs`, `Pointframe.Cli/Verification/VerificationStore.cs`.

## D-011 Standard commands are approved by policy

**Decided.** 2026-10-04.

**Context.** M1 was blocked because a clean .NET project's `verify run` depended on an approver agent reaching its model API; the sandbox returned `ConnectionRefused` before the normal build gate could run. `verify init` emits a small set of conventional build and check commands.

**Decision.** `CommandPolicy.IsStandard` approves only command text that parses exactly as the supported `dotnet build|test|format|restore`, npm/pnpm/yarn test/install/run, or PowerShell project-script forms, with relative in-project paths, allowed options, and no shell metacharacters. When every gate is standard and the app passes its existing fixed rules with metacharacter-free arguments, `EnsureTrustedAsync` stores approval with `ApprovedBy = "policy"`. Any nonstandard command remains with the approver agent, then a person. This approves the command text only, not the behavior of the project's code it runs.

**Consequences.** Standard verification is offline and costs no agent call. A grammar change is a policy change and needs focused classification tests. `approver_unavailable` distinguishes missing or failing infrastructure from a real refusal (`spec_untrusted`); the Stop hook lets an agent stop and tells a person how to restore approval. Policy is intentionally narrow and does not sandbox gate processes.

**Alternatives rejected.** A structured command schema (requires changing the spec contract and generated forms); an offline human baseline (still blocks clean projects on a person); an approval service (adds an online dependency); a signed manifest (adds a signing and distribution lifecycle).

**Files.** `Pointframe.Cli/Verification/CommandApprover.cs`, `Pointframe.Cli/Verification/VerificationApplication.cs`, `Pointframe.Cli/Verification/VerificationHook.cs`, `Pointframe.Agent.Tests/Cli/CommandApprovalTests.cs`, `Pointframe.Agent.Tests/Cli/VerificationHookTests.cs`, `Pointframe.Agent.Tests/Cli/VerificationInitTests.cs`, `docs/cli/README.md`.

## D-012 Version compatibility

**Decided.** 2026-10-04.

**Decision.** CLI release numbers, the spec's `schemaVersion`, and the hook command are independent. An unknown `schemaVersion` is rejected as `spec_invalid`; the CLI never silently migrates an unknown schema. The hook command `pointframe verify hook stop [--review]` is a stable contract across CLI upgrades. A CLI/hook version mismatch produces a warning only. For the CLI-installed fallback, a server older than the CLI is updated from the CLI's own stable release or refused with `mcp_version_mismatch`; a newer server is used with a warning. Every CLI install of the server (`mcp install`, the `verify run` fallback, the older-server update) downloads the CLI's own release (`releases/download/v<version>/`, build metadata stripped); a release missing the asset fails with `mcp_release_asset_missing` and never falls back to latest, and every other build uses `latest`: the versioned release is chosen only when the CLI carries the `PointframeReleaseBuild` assembly metadata (set by `packaging/build-cli-package.ps1`) and a stable `x.y.z` version other than `0.0.0`, because a plain source build reports `1.0.0+<hash>` (amended 2026-10-17: a CLI pinned to 6.7.123 installed MCP 6.7.124 from `latest`, and every pinned trial scored invalid). Explicit server paths and `POINTFRAME_MCP_EXECUTABLE` are honored as chosen, with a stderr warning when that server's file version is older than the CLI (a stale variable from an old install silently ran a 6.7.71 server under CLI 6.7.85 in 2026-10 trials).

**Alternatives rejected.** Lockstep numbering; silent acceptance of unknown fields; automatic migrations; a version negotiation protocol.

**Files.** `Pointframe.Cli/Verification/VerificationSpecLoader.cs`, `Pointframe.Cli/Commands/CliCommandParser.cs`, `Pointframe.Cli/Verification/VerificationInit.cs`, `Pointframe.Cli/Verification/VerificationApplication.cs`, `Pointframe.Agent.Tests/Cli/McpServerVersionTests.cs`, `Pointframe.Agent.Tests/Cli/VerificationRunTests.cs`, `Pointframe.Agent.Tests/Cli/VerificationInitTests.cs`, `Pointframe.Agent.Tests/Cli/VerificationTests.cs`.

See [D-013](#d-013-verification-spec-v1-is-frozen) for the frozen input contract and output compatibility rule.

## D-013 Verification spec v1 is frozen

**Decided.** 2026-10-04.

**Context.** Projects keep verification specs in source control, and tools need predictable input and output compatibility across CLI releases.

**Decision.** `schemaVersion: 1` is frozen: existing input fields keep their meanings, and no field is added, removed, or reinterpreted under version 1. A change to the input contract requires `schemaVersion: 2` and migration notes. The loader rejects unknown fields and versions with `spec_invalid`. Verdict and review output may gain fields while remaining version 1; consumers must ignore fields they do not recognize.

**Consequences.** A misspelled or newer input field fails before any command runs, so the user must update the spec or CLI deliberately. Output consumers can read fields they understand without failing when a later CLI adds fields.

**Alternatives rejected.** Lenient handling of unknown input fields would let typos change verification silently. Automatic migration would change a project's checked-in input without an explicit review.

**Files.** `Pointframe.Cli/Verification/VerificationSpecLoader.cs`, `Pointframe.Cli/Verification/VerificationSpec.cs`, `Pointframe.Cli/Verification/VerificationResults.cs`, `Pointframe.Agent.Tests/Cli/VerificationSpecV1CompatibilityTests.cs`, `docs/cli/verify-spec.md`. See [Spec-driven verification runs](verify.md#spec-driven-verification-runs) and [D-012](#d-012-version-compatibility).

## D-014 Interactive desktop testing reuses verify trust

**Decided.** 2026-10-05.

**Context.** A coding agent in another project needed a hand-written policy file and a manual `--desktop-testing --desktop-policy` registration to get interactive desktop tools. A policy file the agent writes would let it choose what the server may launch.

**Decision.** The CLI is the front door and the spec is the source: `pointframe mcp serve` builds the desktop policy from the project's `.pointframe/verify.json` `app` after the same trust check as `verify run` (D-008, D-009, D-011 semantics unchanged: the commands digest, fixed refusals, standard-command policy, approver agent, a person only through `verify trust`), and `pointframe verify setup` writes the project-scoped client config that runs it. The policy lives outside the repository under the verify store, keyed by the project path. Containment is decided on the resolved path, so a junction cannot lead the app out of the project. Isolation from the spec is honoured with a temporary data folder deleted on exit. `verify init --explore` needs the same trust before it launches the app.

**Consequences.** One command replaces the policy file and the registration. Changing the app or any gate stops the server from starting until it is approved again. A config that depends on the CLI being on `PATH` fails with `pointframe_not_on_path` at setup rather than silently later. `mcp serve` never asks a person, so first approval of a nonstandard spec happens in `verify trust` or `verify setup` with an approver.

**Alternatives rejected.** A policy file kept in the repository (an agent could edit what it may launch); a separate trust store for interactive tools (two approvals for one command); user-scope client config (it would apply to every project).

**Files.** `Pointframe.Cli/Verification/DesktopToolsApplication.cs`, `Pointframe.Cli/Verification/PathContainment.cs`, `Pointframe.Cli/McpManagement/ProjectMcpClientConfig.cs`, `Pointframe.Cli/McpManagement/McpServerHost.cs`, `Pointframe.Agent.Tests/Cli/DesktopToolsTests.cs`. See [Spec-driven verification runs](verify.md#spec-driven-verification-runs), [D-008](#d-008-verify-approvals-need-a-person-at-a-terminal-and-live-outside-the-repository), [D-009](#d-009-an-approver-agent-behind-fixed-rules-approves-verify-commands), [D-011](#d-011-standard-commands-are-approved-by-policy).

## D-010 Verify's agent roles run on any agent

**Decided.** 2026-10-04.

**Context.** The examiner, reviewer, and approver were hard-wired to `claude -p`, so a team using Codex or another agent could not use them. The owner wants verify usable with any agent.

**Decision.** The roles (instructions, answer schema, how the answer is checked) are separate from how an agent is started. `IAgentRunner` has three implementations: `ClaudeCodeAgentRunner` (`claude -p`, system prompt, built-in tools off, budget cap, `structured_output`), `CodexAgentRunner` (`codex --ask-for-approval never exec -`, instructions leading the prompt, `--output-schema`, `-o`, read-only sandbox, `--ephemeral`, MCP through `-c mcp_servers...` with `default_tools_approval_mode=approve` for that one server), and `CommandAgentRunner` (any program, with `{prompt_file}`, `{schema_file}`, `{output_file}`, `{mcp_config_file}` placeholders). `AgentSelection` reads the choice from the person's profile (`agent.json` under `%LOCALAPPDATA%\Pointframe\verify`), not the repository, and otherwise detects Claude Code, then Codex. npm `.cmd` shims are resolved to the real program so JSON arguments are not re-parsed by cmd.exe. The Stop hook already fits Codex, which uses the same hook input and answer.

Every answer schema is in strict form (each object closed with `additionalProperties: false`, every property required), which Codex requires and Claude Code accepts; examiner steps therefore come back as JSON text, parsed by `AgentExaminer.ParseProposal`.

**Consequences.** Any agent can play the roles; with no agent at all, the approver refuses (a person approves) and task start explains how to configure one. Codex runs have no budget cap and keep a read-only shell. Agents without Stop hooks are held only by their instructions and CI.

**Alternatives rejected.** Calling model APIs directly (the examiner needs an agent loop with MCP tools, which agent CLIs already provide); keeping the choice in the spec (the worker could pick its own judge).

**Files.** `Pointframe.Cli/Verification/AgentRunners.cs`, `Pointframe.Cli/Verification/Examiner.cs`, `Pointframe.Cli/Verification/Reviewer.cs`, `Pointframe.Cli/Verification/CommandApprover.cs`.
