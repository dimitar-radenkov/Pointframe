# Agent workflow

How work is planned, assigned, reviewed, and merged in this repository by the coordinating agent (Claude), Claude subagents (Sonnet, Opus), and Codex executors and reviewers. `kb init` prints the rules for your role; read the sections it names. Decisions D1-D5 below are recorded with their rationale in [D-016](decisions.md#d-016-agents-follow-one-workflow-in-the-knowledge-base).

Part of the [Pointframe knowledge base](knowledge-base.md).

**Enforcement status.** Rules marked **in force** are checked by scripts that exist today. Rules marked **planned** are policy only until the named follow-up lands: PR 3 moves run, status, and dialogue records to a shared store under git's common directory. Until then the coordinator applies planned rules by hand.

<!-- rule-section roles=coordinator -->
## Purpose and authority

The owner sets direction and holds the decisions the charter reserves. The coordinator is the single accountable agent: it decides, delegates, accepts or rejects results, and reports. Everything else is an executor or a reviewer.

<!-- rule:W-01 roles=coordinator -->
| Charter class | Who decides | Examples |
|---|---|---|
| A | Coordinator alone, report afterwards | Review findings inside an approved plan; bugs; flaky tests (assertions preserved); factual docs and site corrections; compatible dependency updates; release tooling; closing resolved or duplicate issues with linked evidence; security fixes that keep existing access and data contracts |
| B | Coordinator alone, notify before publishing | Routine releases through the approved channels (GitHub release, winget, MCP Registry); major dependency upgrades with demonstrated compatibility and a recovery path; compatible telemetry schema changes |
| C | Owner only | New product direction or large new feature; new external accounts or spend beyond budget; posting in the owner's name; deleting durable user data; user-visible promise changes (pricing, privacy policy, license); expanding collected telemetry or retention |

<!-- rule:W-02 roles=all -->
- **Precedence.** The charter, then an approved plan, then this file, then `CLAUDE.md` and `AGENTS.md` (which point here). A rule kept only in an agent's private memory is not a rule; it belongs here.
- **Disagreement.** A Claude-Codex debate about an execution question is bounded to one round. The coordinator then decides or runs a bounded experiment, and logs the dissent. Disagreement alone never goes to the owner.
<!-- rule:W-03 roles=coordinator -->
- **Guardrails.** At most 2 implementation tasks in parallel (separate worktrees), 1 release in flight, desktop takeover of at most 30 minutes at a time and announced first. Codex work stops at 80% of the weekly limit.
<!-- rule:W-04 roles=coordinator,reviewer -->
- **Evidence per change.** Focused tests, a real-environment check when runtime behavior changes, an independent review, a final `verify.ps1`, and the checked head SHA or artifact. A privacy change states its residual risk.
- **Decision log.** Log a material decision when it is made: time, objective, accountable agent, decision and reason, scope, evidence, recovery path, outcome (`scripts/status.ps1 -Decision`).
<!-- rule:W-05 roles=coordinator -->
- **Merged regression.** Stop the rollout, log it, open an issue, revert with verification (or roll forward when a revert would harm data), and notify at once without a new permission loop.

<!-- rule-section roles=coordinator -->
## From idea to approved package

| Work | Brainstorm and approval |
|---|---|
| Charter A work (bugs, flaky tests, doc corrections, review fixes) and work inside an approved plan | Proceeds directly: no brainstorm, no approval (D3) |
| A new product package | Codex debate, then one approval message from the owner |
| Charter C | Owner only |

- **Debate.** "Brainstorm with Codex" is a real debate on a strong model: Codex proposes, the coordinator critiques with evidence from code and data, Codex answers, and they iterate (usually 2-3 rounds) until they converge. A single Codex answer forwarded as-is is not a brainstorm. Substantial design debates get two independent assessments.
- **One approval message.** The converged plan (scope, deliverables, non-goals); each open decision with options, pros and cons, and a recommendation; and what comes after. After approval the coordinator executes, reviews, opens the PR, and merges without asking again. Include the points of disagreement and how each was settled in the approval message.
- **Still ask** for a decision the approved plan leaves open, a destructive or third-party action that neither the charter (A or B) nor an approved plan already authorizes, or a surprise that changes the plan. Routine releases through the approved channels are class B: notify, do not wait.
- **Private dialogues, public rules.** Rules and decision rationale live in this knowledge base. Full Claude-Codex dialogues stay local (D2) and are never needed to understand a rule. **Planned (PR 3):** a shared local store for dialogue records.

<!-- rule-section roles=coordinator -->
## Roles and assignment

<!-- rule:W-06 roles=all -->
| Role | Who | Does |
|---|---|---|
| Owner | Person | Sets direction, approves plans, decides class C |
| Coordinator | Claude main session | Plans, writes briefs, assigns, reads results critically, accepts or returns them, integrates shared files (indexes, KB, numbering), commits, opens PRs, merges, reports |
| Executor | Sonnet subagent or Codex (`gpt-6-luna`, workspace-write) | Delivers one whole result from a brief. Never commits, pushes, or opens a PR |
| Reviewer | A different agent than the executor | Checks the result against the criteria, read-only |
| Helper | Haiku 5.5 subagent (trial) | Read-only summaries for the coordinator |

<!-- rule:W-07 roles=coordinator -->
- **The coordinator does not execute whole results.** Delegate early: a task of more than about 5-10 steps, or one with its own run-test-fix loop, goes to an executor from the start. One executor gets the whole result; no agent per command. Fixes go back to the same executor: for a Sonnet subagent, continue the same subagent. Returning a fix to the same Codex executor through the launcher is **planned**; until then a fix is a new launcher run whose brief includes the previous run's report and the review findings. An incorrect review finding is rejected with a stated reason.
<!-- rule:W-36 roles=coordinator -->
- **Allocation.** Aim for 40-60% of eligible packages to Codex as a rolling target, never a per-task quota. Capability and remaining capacity decide exceptions. Codex suits scripts and code without visual judgment; Sonnet suits visual, MCP, and cross-file work.
<!-- rule:W-37 roles=coordinator -->
- **Models.** Cheapest model that can do the work: `gpt-6-luna` at `medium` effort by default, `high` effort for concurrency, lifecycle, and DPI work. Raise effort before raising the model. Only generation-6 models are used; the top tier needs owner approval. Brainstorm, retros, and heavy review use the strong tier (see Review). Set model and effort per run; never change persistent Codex configuration unless asked. Use only model names confirmed by Codex configuration or its model listing.
<!-- rule:W-39 roles=coordinator -->
- **Haiku 5.5 trial.** Haiku 5.5 (`claude-haiku-5-5`, Agent model `haiku`) is allowed only for this scope until the 2026-11-02 retro; no automatic renewal. Through `package.ps1` a Haiku model needs a `Haiku-scope: helper|mechanical` brief line; helpers get no Edit or Write.
  - **Mechanical executor:** only when all four small-fix conditions (W-08) hold, or for a generated or link fix with an explicit expected transformation and allowed paths. KB, index, and numbering edits stay with the coordinator (W-09): Haiku proposes the patch, the coordinator applies it.
  - **Never Haiku:** reviewer (light or heavy), KB prose rewriting or compression, lifecycle, DPI, privacy, release, and verify-truthfulness work.
  - **Review:** a Haiku executor's change gets an independent light review (Codex `gpt-6-luna`) and an ordinary review record; `-SmallFix` stays coordinator-only (W-20 unchanged).
<!-- rule:W-42 roles=coordinator,helper -->
- **Haiku helper scope.** Read-only, with bounded time and output: summarise verify and CI logs and long review files; code search; watchdog triage. A helper starts with `pwsh scripts/kb.ps1 init -Role helper` (a subagent cannot type `/kb-init`); the brief says so.
<!-- rule:W-40 roles=coordinator,reviewer,helper -->
- **Summaries are navigation, not evidence.** The coordinator reads exit codes and structured verdicts bound to the current head and tree itself. A helper summary lists every failed, cancelled, missing, or incomplete check with the raw path and line, and every blocking review finding with its location. Missing or contradictory evidence is reported as unknown, never PASS.
<!-- rule:W-08 roles=coordinator -->
- **Small-fix exception.** The coordinator may edit directly only when all hold: about 10 changed non-generated lines in at most 2 files; mechanical, with no runtime behavior change or new logic; no privacy, security, lifecycle, packaging, persistence, or workflow-gate impact; no diagnose-test-repair loop. Log each use and count them in the report, so three small fixes cannot hide substantial coordinator execution.
<!-- rule:W-09 roles=all -->
- **One integrator.** Only the coordinator edits shared files (KB, indexes, numbering). Parallel tasks own disjoint files.
<!-- rule:W-10 roles=coordinator -->
- **Coordinator context.** Bulky logs, long outputs, and desktop snapshots stay out of the coordinator's context: write them to a file and keep the path, a short excerpt, and the exit code.

<!-- rule-section roles=coordinator,executor -->
## Brief and acceptance criteria

<!-- rule:W-11 roles=coordinator,executor -->
One brief serves either executor. Required headings, checked by `scripts/agent-run.ps1` (**in force**; check a Sonnet brief with `-ValidateOnly`): objective (or why), worktree, read first, deliverables, exclusions, stop conditions, checks.

<!-- rule:W-12 roles=coordinator,executor -->
| A brief also states | Why |
|---|---|
| Owned files and worktree | Parallel tasks must not collide |
| Existing coverage to read first: exact files, tests, KB sections | No open-ended discovery |
| Acceptance cases, including known lifecycle cases (focus, cancellation, legacy users, recording-only users, older clients) | The reviewer needs a criterion per case |
| Authorized repairs | The executor repairs without asking |
| Absolute worktree paths, explicit working directory per write and process | The shell starts in the main checkout |
| Stop conditions | Stop after the first real environment failure (sandbox, missing tool, authentication) and report. Missing build outputs are not one; build them. Never retry an identical failing command |
| Exact checks | Sandbox-safe: `dotnet format Pointframe/Pointframe.csproj --no-restore`, and every `dotnet test --filter` adds `&Category!=Integration` |

- Lesson: An executor's relative path or default working directory can write into the main checkout
- Lesson: A background command that reads stdin can hang an agent for hours

<!-- rule-section roles=coordinator,executor -->
## Execution budgets

<!-- rule:W-13 roles=coordinator -->
- **Launcher (in force).** Every Codex executor run goes through `scripts/agent-run.ps1`: it validates the brief, refuses `high` effort or the strong tier without `-EscalationReason`, stops at `-MaxMinutes` and `-MaxInputTokens`, and appends a cost record to the local `agent-runs.jsonl` under `plan/`.
<!-- rule:W-14 roles=coordinator -->
- **Reassessment triggers.** At 100 tool calls, 20M tokens, or 3 unsuccessful repair cycles the coordinator reassesses: continue, hand over with notes, or stop. A trigger is not an automatic replacement.
<!-- rule:W-15 roles=coordinator,executor -->
- **One validation owner.** The executor runs the focused checks its environment allows, and `verify.ps1` once on its final tree when it can. A check its sandbox cannot run it reports as `ENV-BLOCKED: <command>: <error>`; the runner, `scripts/package.ps1 check`, owns build, focused tests, format and CRLF, and a result is fresh only for the tree it ran on. The coordinator commits, pins, and runs `verify.ps1` on the committed tree; `merge-pr.ps1 -Auto` refuses an older receipt.
<!-- rule:W-16 roles=coordinator -->
- **Limits.** Report both providers' remaining limits with progress reports and after each run (Codex 5-hour and weekly use; Claude status and reset time). Never read OAuth tokens or other credentials to call a usage endpoint.

<!-- rule-section roles=coordinator,reviewer -->
## Review

<!-- rule:W-17 roles=coordinator,reviewer -->
Every behavior change gets an independent review from a different agent than its executor, preferably from the other subscription: Sonnet executes and Codex `gpt-6-luna` reviews, and the reverse (D4).

<!-- rule:W-18 roles=coordinator,reviewer -->
| Review | When | Reviewer |
|---|---|---|
| Light | Default for a behavior change | The other subscription's light model, read-only |
| Heavy | Collection, identifiers, consent, retention, or privacy promises; security boundaries, destructive operations, data migration; release, install, or update delivery; F-39 verify truthfulness, evidence freshness, or bypass resistance; authority, review, verification, or autonomy rules; hard concurrency, lifecycle, or DPI | `gpt-6.1-sol` at high effort read-only, or an Opus subagent. One heavy review replaces the light one |
| Small-fix exception | All four small-fix conditions hold | The coordinator; logged and counted in the report |

<!-- rule:W-19 roles=coordinator,reviewer -->
- **Independence.** The reviewer gets the brief, the criteria, and the paths, not the executor's or the coordinator's opinion. It runs the key commands and returns PASS or FAIL per criterion with evidence, plus what it could not check.
- **Consequences, not locations.** Heavy review follows what a change can break, not the directory. An F-39 help typo or an ordinary File map row does not qualify. Privacy wording is never mechanical.
- **Model rule amended.** The strong Codex tier was reserved for brainstorm and retros; D4 also allows it, read-only, for heavy review.
<!-- rule:W-20 roles=coordinator -->
- **Review record (in force).** `pwsh scripts/record-review.ps1` writes one record per head SHA under git's common directory (`pointframe/reviews/<sha>.json`): head and tree, brief hash or requirement, executor and reviewer with sessions, light or heavy, verdict, open blocking findings, and evidence path. Sonnet reviewers stay read-only; the coordinator runs the command and is named as recorder. It refuses a reviewer equal to the executor (same model counts as the same reviewer) unless `-SmallFix`, which only the coordinator as executor may use and which is limited to 2 files and 12 changed lines.
<!-- rule:W-21 roles=coordinator -->
- **Merge gate (in force).** `scripts/merge-pr.ps1` refuses to merge (waiting mode and `-Auto`) without `-Worktree`, a matching verify receipt, and a passing record for the PR head; a stale head or tree, a FAIL verdict, an open blocking finding, or a missing evidence file is refused. A record for an ancestor counts only when the later change is the plugin pin. Direct `gh pr merge` remains a bypass; a required GitHub check is the next step if bypasses occur.
<!-- rule:W-22 roles=coordinator -->
- **Codex consultations.** Brainstorm and review calls go through `pwsh scripts/codex-ask.ps1` with `-Model` and `-Effort` on every call, including a resume by `-SessionId`; it rejects failed, missing, or stale output.

## Verification and desktop checks

<!-- rule:W-23 roles=all -->
- **Done gate (in force).** `pwsh scripts/verify.ps1` passes on the final tree and writes the gitignored verdict file `verdict.json` under `artifacts/verify`; see [Local gates and merging](tooling.md#local-gates-and-merging). Receipts are bound to the head and the working-tree hash, so a stale one is detectable.
<!-- rule:W-24 roles=coordinator,executor -->
- **Real-environment check.** When runtime behavior changes, run the real app: `pwsh scripts/desktop-tests.ps1` for the regression selection, with `-Repeat N` for stress. The desktop is announced and kept to 30 minutes at a time; check the input desktop is not locked first.
<!-- rule:W-25 roles=coordinator -->
- **Release qualification.** Run `pwsh scripts/release-impact.ps1` before merging; when it prints `desktop-qualification: required`, run `scripts/check-released-desktop.ps1 -BuildCandidate`. After each release run `scripts/check-release-channels.ps1 -Version <x> -Watch` and report a failed channel at once.
<!-- rule:W-26 roles=all -->
- **Report what was not tested.** A report that omits an untested path claims it passed.

<!-- rule-section roles=coordinator -->
## PR and merge

<!-- rule:W-27 roles=all -->
The coordinator commits, opens PRs, and merges. Executors never commit or push. This overrides the generic "leave changes for user review" default for this repository.

<!-- rule:W-28 roles=coordinator -->
1. Work on a feature branch, never on `master`.
2. Commit, then `pwsh scripts/merge-pr.ps1 -PinOnly -Worktree <path>`, then `pwsh scripts/verify.ps1` on the committed tree. The executor's earlier run does not satisfy the merge: a receipt from before the commit is refused.
3. Record the independent review for the committed head: `pwsh scripts/record-review.ps1 -Worktree <path> ...`.
4. Push and `gh pr create`.
5. `pwsh scripts/merge-pr.ps1 -Pr <n> -Worktree <path> -Auto -WaitMerged` under a watcher. It needs the receipt and the review record to match the PR head and tree. "AUTO-MERGE ENABLED" is not merged.

For a package run through `scripts/package.ps1`, `accept` covers step 3 and `ship -Id <slug>` runs steps 2, 4 and 5 bound to the accepted head.

<!-- rule:W-29 roles=coordinator -->
- **Checks.** Every required check (`unit-tests`, `package-checks`, `Analyze (csharp)`, CodeQL) must be present. Every check on the PR head, required or otherwise, must have completed without failure or cancellation; skipped checks are allowed. Never delete the branch or worktree until GitHub says MERGED.
<!-- rule:W-30 roles=coordinator -->
- **Serial.** Merge PRs one at a time; KB edits conflict. Every merge cuts a release.
- **Plugin pin.** A stale pin is acceptable when only the two pin files changed and `check-agent-discovery.ps1` passes; `-Auto` commits the pin itself.
- **Repository settings** (rulesets, auto-merge) change only on the owner's explicit wording.

<!-- rule-section roles=coordinator -->
## Status and handoff

<!-- rule:W-31 roles=coordinator -->
- **Status (in force).** the local status file under `plan/` is the store; `scripts/status.ps1 -Set`, `-Done`, `-Decision`, `-Publish` write the pinned status issue and the handoff region. Each active task has a record: objective, accountable agent, stage, SHA or artifact, evidence, next step, blocker, budget and desktop use. Update it at start, material decisions, ready, release, regression, and done; add a daily digest comment on the issue; raise alerts only when intervention is needed. Publish at every stage change; never hand-edit generated regions.
<!-- rule:W-32 roles=coordinator -->
- **Session closure (D5).** A coordinating session is one work package. At package end, end of day, the first context compaction, or a measured 150K context, whichever comes first: write the checkpoint (status plus handoff; `/kb-done` does it), finish or safely hand off the running operation (a desktop run, a merge watcher), start nothing new, then close.
<!-- rule:W-33 roles=coordinator -->
- **Owner questions.** Ask with `AskUserQuestion`: 2-4 real options, recommendation first (numbered plain options where it is unavailable), and only for a decision the charter or an approved plan leaves open; destructive or third-party actions are asked about only when neither the charter (A or B) nor an approved plan already authorizes them. A general "yes" approves only what was stated clearly.
<!-- rule:W-34 roles=coordinator -->
- **Final report.** What was done; what was tested and what was not; what was fixed after review; which work went to Claude and which to Codex; the model and effort used for each executor and reviewer run; both providers' limits; what awaits a decision.
- **Planned (PR 3).** Run and status records join the review records in a shared store under git's common directory so every linked worktree sees them.

<!-- rule-section roles=coordinator -->
## Incidents and measurement

<!-- rule:W-38 roles=coordinator -->
- **Weekly retro.** Every Monday, a dated document under `plan/retros/` with the work done, what went well and badly with causes, Claude and Codex token use, and next actions. Retros use strong models, with Codex challenging the draft.
- **Metrics to watch.** Coordinator versus executor tokens, questions asked to the owner, activation (`scripts/usage-report.ps1`), and counted small-fix exceptions.
- **Haiku measurement.** Every Haiku run, accepted or rejected, is a line in the local `agent-runs.jsonl` under `plan/`: model, task or run id, task kind, minutes, outcome (accepted, redone, rejected), raw artifact path and size, bytes the coordinator then read, review and rework minutes, failures and timeouts, and summary omissions or false verdicts found. Gross estimated input tokens avoided = unread raw bytes / 4. The coordinator spot-checks accepted summaries against raw evidence. The 2026-11-02 retro decides the trial; savings are assessed net of helper and review costs.
<!-- rule:W-35 roles=all -->
- **Trap to avoid.** Rules that live only in memory were not followed (D-016). When a correction is durable, put it in the owning section here or in a feature file as scope, check, and required evidence; use `lessons.md` when there is a reusable failure mechanism.

**Files.** `scripts/agent-run.ps1`, `scripts/codex-ask.ps1`, `scripts/merge-pr.ps1`, `scripts/record-review.ps1`, `scripts/status.ps1`, `scripts/verify.ps1`, `CLAUDE.md`, `AGENTS.md`.
