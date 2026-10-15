# Decisions

Decisions that span areas: why a choice was made over the obvious alternative. A decision that only one area needs lives in that area file under `features/`. Numbers are global and never reused.

Part of the [Pointframe knowledge base](knowledge-base.md).

## Chronology

Every decision by date, whichever file holds it. The sections themselves are in this file or the area file named by the link.

| Date | ID | Decision |
|---|---|---|
| 2026-04-09 | [D-001](#d-001-mvvm-plus-di-is-the-composition-model) | MVVM plus DI is the composition model |
| 2026-04-09 | [D-003](features/recording.md#d-003-recording-uses-one-authoritative-session-geometry) | Recording uses one authoritative session geometry |
| 2026-09-05 | [D-002](#d-002-one-knowledge-base-file-checked-by-script) | One knowledge base file, checked by script (superseded by D-006) |
| 2026-09-06 | [D-004](#d-004-native-libraries-ship-loose-and-the-installer-packages-them) | Native libraries ship loose and the installer packages them |
| 2026-09-06 | [D-005](features/transcription.md#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app) | The speech model is delivered by both the installer and the app |
| 2026-10-03 | [D-006](#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area) | Cross-cutting knowledge base plus one file per feature area |
| 2026-10-03 | [D-007](features/cli-mcp.md#d-007-verify-run-drives-the-mcp-server-instead-of-sharing-an-engine-service) | `verify run` drives the MCP server |
| 2026-10-03 | [D-008](features/cli-mcp.md#d-008-verify-approvals-need-a-person-at-a-terminal-and-live-outside-the-repository) | Verify approvals need a person at a terminal (superseded by D-009 for gate and app commands) |
| 2026-10-04 | [D-009](features/cli-mcp.md#d-009-an-approver-agent-behind-fixed-rules-approves-verify-commands) | An approver agent behind fixed rules approves verify commands (partly superseded by D-011) |
| 2026-10-04 | [D-010](features/cli-mcp.md#d-010-verifys-agent-roles-run-on-any-agent) to [D-013](features/cli-mcp.md#d-013-verification-spec-v1-is-frozen) | Verify agent roles, standard commands, version compatibility, frozen spec v1 |
| 2026-10-05 | [D-014](features/cli-mcp.md#d-014-interactive-desktop-testing-reuses-verify-trust) | Interactive desktop testing reuses verify trust |
| 2026-10-15 | [D-015](features/telemetry.md#d-015-agent-telemetry-carries-a-random-install-id) | Agent telemetry carries a random install ID |
| 2026-10-15 | [D-016](#d-016-agents-follow-one-workflow-in-the-knowledge-base) | Agents follow one workflow in the knowledge base |

## D-001 MVVM plus DI is the composition model

Decided 2026-04-09.

**Context.** WPF invites putting behavior in window code-behind, which cannot be tested without starting the app. Pointframe's flows (overlay, recording, settings, updates) need unit tests that run without a desktop session.

**Decision.**

- ViewModels (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` from CommunityToolkit.Mvvm) own state and commands.
- Every public service has an `I<Name>` interface and is registered in `AddPointframeAppServices`.
- Window code-behind holds only view-specific work: layout, HWND interop, DPI reads, focus.
- Tests target ViewModels and services with Moq; the WPF app is never started in `Pointframe.Tests`.

**Consequences.** New user-facing behavior starts as a ViewModel command plus a service, then gets a thin view binding. Constructor injection everywhere; a window with many dependencies gets a factory in the registration file (see `CreateOverlayWindow`). Manual `OnPropertyChanged()` calls are a smell; use the source generators. Most `I<Name>` interfaces have one production implementation; that is intended, because the Moq double is the second implementation. Do not remove one as speculative abstraction. Do run the deletion test on the service behind it: a service that only forwards calls to another should be inlined, interface and all.

**Alternatives rejected.** Code-behind-first WPF: fast to write, impossible to test without UI automation. A static service locator: hides dependencies and defeats Moq-based tests.

**Files.** `Pointframe/AppServiceRegistration.cs`. See [App bootstrap](knowledge-base.md#app-bootstrap-di-and-messaging) and [Register a service](knowledge-base.md#register-a-service).

## D-002 One knowledge base file, checked by script

Superseded by [D-006](#d-006-cross-cutting-knowledge-base-plus-one-file-per-feature-area) on 2026-10-03.

Decided 2026-09-05. Replaces the 2026-04-09 decision that kept one project-wide file plus ad hoc focused docs, and a same-day trial of one file per topic with frontmatter and a generated index.

**Context.** The previous single file went stale within months: it still named `SnippingTool/` paths after the rename and linked to two docs that no longer existed, because nothing checked it against the code. The one-file-per-topic trial fixed staleness but added more files and metadata than the project needs to maintain.

**Decision.** One file, `docs/knowledge-base/knowledge-base.md`, with five fixed groups (subsystems, decisions, invariants, how-tos, references), a generated table of contents, a `**Files.**` line per section, and `- Lesson:` references into `lessons.md`. `pwsh .claude/skills/knowledge-base/knowledge-base.ps1` refreshes the table of contents and fails when a repo path, lesson heading, or internal link no longer resolves. Since 2026-10-03 a `## Feature map` lists every user-facing feature with a stable `F-NN` ID, its triggers, entry point, telemetry events, and tests (after StrictDoc-style traceability); the script fails on an unknown or unclaimed telemetry event and on a smoke test no feature claims. Since 2026-10-02 a `## File map` group maps every tracked file, by repo-root glob, to the sections to read before editing it; the script fails on a tracked file no row covers or a pattern that matches nothing, `-For <path>` resolves a file to its sections and lessons, `-Changed` lists the sections to review for a diff, and `-Hook` serves the same lookup to a Claude Code `PostToolUse` hook. Codex reads skills from `.agents/skills/`, so the script also regenerates `.agents/skills/knowledge-base/` from the `.claude` copy and `-Check` fails when they differ. The `/knowledge-base` skill (`add`, `update`) is how agents change the file. `CLAUDE.md` stays a pointer; `lessons.md` stays the post-mortem log.

**Consequences.** One place to read and one place to edit. Staleness is caught mechanically for paths, lessons, and links, but not for prose; running `/knowledge-base update` after a subsystem change is the human step. The file grows, so sections stay under about 80 lines and prefer tables.

**Alternatives rejected.** One file per topic with frontmatter and an index: more to maintain. A wiki or external tool: not versioned with the code and invisible to agents on a clone. Docs generated from comments: the project bans XML doc comments, and the valuable facts (why, invariants) are not in the code. Path-scoped instruction files per tool (GitHub Copilot `.instructions.md` with `applyTo`, Cursor `.mdc` rules with `globs`, nested `CLAUDE.md` files): the File map borrows their glob-to-guidance idea, but separate files would split the knowledge base again and tie it to one agent.

**Files.** `docs/knowledge-base/knowledge-base.md`, `scripts/kb.ps1`.

## D-004 Native libraries ship loose and the installer packages them

Decided 2026-09-06.

**Context.** Whisper.net resolves its native runtime by probing `runtimes\win-x64` on disk, which the self-extract directory of a single-file build is not. Setting `IncludeNativeLibrariesForSelfExtract` to `false` in `Pointframe/Properties/PublishProfiles/win-x64.pubxml` fixes that, but the flag is all-or-nothing: it pushes *every* native out of the bundle, not just Whisper's.

**Decision.** Keep the flag `false` and make the installer package the publish output: `{#PublishDir}\*.dll` for the six loose WPF and SQLite natives, plus `{#PublishDir}\runtimes\win-x64\*` for the four Whisper DLLs. The arm64 and x86 copies the SDK emits are not shipped; this is an x64 build and they would only add weight.

**Consequences.** A publish-property change that alters what lands next to the exe now has to change the installer file list in the same commit. `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs` asserts the natives exist after install, so the failure names the missing file rather than a vague launch error. `{app}\runtimes` is removed on uninstall.

**Alternatives rejected.** Leaving the flag `true` and letting Whisper load from the self-extract directory: its loader does not look there. Copying only Whisper's DLLs out of the bundle: the flag has no per-library granularity.

**Files.** `Pointframe/Properties/PublishProfiles/win-x64.pubxml`, `installer/Pointframe.iss`, `Pointframe.AutomationTests/Installer/InstallerSmokeTests.cs`. See [Everything emitted next to the exe must be in the installer file list](knowledge-base.md#everything-emitted-next-to-the-exe-must-be-in-the-installer-file-list) and [CI, CD, and versioning](knowledge-base.md#ci-cd-and-versioning).

**Lessons.**

- Lesson: Turning off single-file native bundling silently breaks the installer, not the dev build

## D-006 Cross-cutting knowledge base plus one file per feature area

Decided 2026-10-03. Supersedes [D-002](#d-002-one-knowledge-base-file-checked-by-script).

**Context.** With a Feature map, a File map, and ten subsystems, the single file passed 950 lines. Agents loaded all of it to answer a question about one area, and every area edit touched the same large file. The owner's original intent was one file for what is cross-cutting (build, composition, shared rules) and a separate file per feature that an agent reads or updates only when it needs it.

**Decision.** `docs/knowledge-base/knowledge-base.md` keeps cross-cutting knowledge: app bootstrap and DI, invariants every area must keep, shared recipes, references, the File map, and a Feature index generated from the area files. Each feature area gets `docs/knowledge-base/features/<area>.md`: a `## Features` table of `F-NN` rows (triggers, entry point, telemetry, tests, read first), then the area's subsystem, area-only invariants and recipes, and their lessons. Areas are coarse, about ten files rather than one per feature, so related features share their flow and lessons. One script, `scripts/kb.ps1`, checks every file: paths, lessons, cross-file links, unique feature IDs, telemetry and smoke-test coverage, and File map coverage. Agents use it through three skills: `/kb-read` resolves a file, area, feature, or topic to what to read across all files; `/kb-write` adds or updates knowledge; `/kb-check` checks and fixes.

**Consequences.** An agent reads the main file (about 500 lines) plus one area file (under about 100) instead of the whole. Cross-file links must name the file, `features/recording.md#recording-pipeline`; the script fails on a broken one. A section moving between files changes its links, which the script also catches. An area file that passes about 150 lines is split. Decisions moved out of the main file on 2026-10-03: one that spans areas is in `decisions.md`, one that a single area needs is in that area file, and the main file keeps a generated index, because a decision is read only when someone is about to change what it governs.

**Alternatives rejected.** One file per `F-NN` feature: 37 small files, and features of one area would repeat or cross-link the same flow and lessons. Keeping one file: the cost that triggered this decision. Frontmatter and a hand-kept index: the index is generated, and structure lives in the tables the script already parses.

**Files.** `docs/knowledge-base/knowledge-base.md`, `docs/knowledge-base/features/recording.md`, `.claude/skills/kb-read/SKILL.md`, `.claude/skills/kb-write/SKILL.md`, `.claude/skills/kb-check/SKILL.md`, `scripts/kb.ps1`.

## D-016 Agents follow one workflow in the knowledge base

Decided 2026-10-15, by the owner, after a three-round Claude-Codex debate. The process itself is in [Agent workflow](workflow.md).

**Context.** Process rules (who executes, who reviews, when to ask, how to merge) lived in the coordinator's private memory and in scattered scripts. The 2026-10-07 retro showed the coordinator session used about 84% of Claude tokens, far more than all executors together, and several PRs merged without independent review. Rules kept only in memory were not followed, and `CLAUDE.md` still told agents never to commit, contradicting the authorized flow.

**Decision.**

| ID | Decision |
|---|---|
| D1 | Location: `docs/knowledge-base/workflow.md`, part of the knowledge base |
| D2 | Public rules, private dialogues: rules and decision rationale are public; full Claude-Codex dialogues stay local and are never required to understand a rule |
| D3 | Brainstorm boundary: charter A work (bugs, flaky tests, doc corrections, review fixes) and work inside an approved plan proceed without brainstorm or approval; a new product package needs the Codex debate and one approval message |
| D4 | Review: every behavior change gets an independent review from a different agent than the executor, preferably the other subscription. A small-fix exception (about 10 changed non-generated lines in at most 2 files; mechanical; no privacy, security, lifecycle, packaging, persistence, or workflow-gate impact; no diagnose-test-repair loop) is logged and counted. Heavy review (strong Codex tier read-only, or an Opus subagent) follows consequences: privacy promises, security and destructive operations, release delivery, F-39 truthfulness, authority and autonomy rules, hard concurrency, lifecycle, or DPI. This amends "strong Codex tier only for brainstorm and retros" |
| D5 | Sessions: a coordinating session is one work package. At package end, end of day, first compaction, or a measured 150K context, write the checkpoint, finish or safely hand off the running operation, start nothing new, then close |

**Consequences.** Enforcement is staged. This change is documentation only. A follow-up adds a review gate to `scripts/merge-pr.ps1` (refusing auto-merge without a worktree and a review record tied to the head SHA), a review record command, and a Codex consultation helper; a later one moves run, status, and review records to a shared store under git's common directory. Direct `gh pr merge` stays a bypass until a required GitHub check exists. Approved policy and implemented enforcement are marked separately in the workflow file.

**Alternatives rejected.** A separate agent-workflow document outside the knowledge base: not retrieved by `kb.ps1 read`. A second decision log beside this file: two overlapping histories; this file keeps the global D-NNN IDs and gains the chronology above. Copying another repository's rule that knowledge changes only after owner approval: it would undermine the charter's autonomy. Review by the coordinator for every change: the failure this decision answers. Brainstorm for every package: conflicts with charter A. Immediate stop at the context threshold: it can strand a desktop run or merge watcher. Required GitHub enforcement immediately: wait for the local gate pilot to show whether bypasses persist.

**Files.** `docs/knowledge-base/workflow.md`, `CLAUDE.md`, `AGENTS.md`, `scripts/merge-pr.ps1`.
