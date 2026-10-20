# Agent workflow

How work is planned, assigned, reviewed, and merged in this repository by the coordinating agent (Claude), Claude subagents (Sonnet, Opus), and Codex executors and reviewers. `kb init` prints the rules for your role; read the sections it names.{{slot:intro-note?}}

Part of the [{{slot:product}} knowledge base](knowledge-base.md).

**Enforcement status.** Rules marked **in force** are checked by scripts that exist today. Rules marked **planned** are policy only until the named follow-up lands:{{slot:enforcement-planned}} Until then the coordinator applies planned rules by hand.

<!-- rule-section roles=coordinator -->
## Purpose and authority

The owner sets direction and holds the decisions the charter reserves. The coordinator is the single accountable agent: it decides, delegates, accepts or rejects results, and reports. Everything else is an executor or a reviewer.

<!-- rule:W-01 roles=coordinator -->
{{local:W-01}}

<!-- rule:W-02 roles=all -->
- **Precedence.** The charter, then an approved plan, then this file, then `CLAUDE.md` and `AGENTS.md` (which point here). A rule kept only in an agent's private memory is not a rule; it belongs here.
- **Disagreement.** A Claude-Codex debate about an execution question is bounded to one round. The coordinator then decides or runs a bounded experiment, and logs the dissent. Disagreement alone never goes to the owner.
<!-- rule:W-03 roles=coordinator -->
- **Guardrails.** {{slot:guardrails}}
<!-- rule:W-04 roles=coordinator,reviewer -->
- **Evidence per change.** Focused tests, a real-environment check when runtime behavior changes, an independent review, a final `verify.ps1`, and the checked head SHA or artifact. A privacy change states its residual risk.
- **Decision log.** Log a material decision when it is made: time, objective, accountable agent, decision and reason, scope, evidence, recovery path, outcome (`scripts/status.ps1 -Decision`).
<!-- rule:W-05 roles=coordinator -->
- **Merged regression.** Stop the rollout, log it, open an issue, revert with verification (or roll forward when a revert would harm data), and notify at once without a new permission loop.

<!-- rule-section roles=coordinator -->
## From idea to approved package

{{slot:idea-to-package}}

<!-- rule-section roles=coordinator -->
## Roles and assignment

<!-- rule:W-06 roles=all -->
| Role | Who | Does |
|---|---|---|
| Owner | Person | Sets direction, approves plans, decides class C |
| Coordinator | Claude main session | Plans, writes briefs, assigns, reads results critically, accepts or returns them, integrates shared files (indexes, KB, numbering), commits, opens PRs, merges, reports |
| Executor | {{slot:role-executor}} | Delivers one whole result from a brief. Never commits, pushes, or opens a PR |
| Reviewer | A different agent than the executor | Checks the result against the criteria, read-only |
| Helper | {{slot:role-helper}} | Read-only summaries for the coordinator |

<!-- rule:W-07 roles=coordinator -->
- **The coordinator does not execute whole results.** Delegate early: a task of more than about 5-10 steps, or one with its own run-test-fix loop, goes to an executor from the start. One executor gets the whole result; no agent per command. Fixes go back to the same executor: {{slot:fix-continuation}} An incorrect review finding is rejected with a stated reason.
<!-- rule:W-36 roles=coordinator -->
{{local:W-36}}
<!-- rule:W-37 roles=coordinator -->
- **Models.** {{slot:models-policy}}
<!-- rule:W-39 roles=coordinator -->
{{local:W-39}}
<!-- rule:W-42 roles=coordinator,helper -->
- **{{slot:helper-label}} scope.** {{slot:helper-scope}} A helper starts with `{{slot:kb-command}} init -Role helper` (a subagent cannot type `/kb-init`); the brief says so.
<!-- rule:W-40 roles=coordinator,reviewer,helper -->
- **Summaries are navigation, not evidence.** The coordinator reads exit codes and structured verdicts bound to the current head and tree itself. A helper summary lists every failed, cancelled, missing, or incomplete check with the raw path and line, and every blocking review finding with its location. Missing or contradictory evidence is reported as unknown, never PASS.
<!-- rule:W-08 roles=coordinator -->
- **Small-fix exception.** The coordinator may edit directly only when all hold: {{slot:small-fix-size}}; mechanical, with no runtime behavior change or new logic; {{slot:small-fix-impact}}; no diagnose-test-repair loop. Log each use and count them in the report, so three small fixes cannot hide substantial coordinator execution.
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
| Acceptance cases, including known lifecycle cases {{slot:lifecycle-cases}} | The reviewer needs a criterion per case |
| Authorized repairs | The executor repairs without asking |
| Absolute worktree paths, explicit working directory per write and process | The shell starts in the main checkout |
| Stop conditions | Stop after the first real environment failure (sandbox, missing tool, authentication) and report. Missing build outputs are not one; build them. Never retry an identical failing command |
| Exact checks | {{slot:exact-checks}} |

{{slot:brief-lessons?}}

<!-- rule-section roles=coordinator,executor -->
## Execution budgets

<!-- rule:W-13 roles=coordinator -->
- **Launcher (in force).** Every Codex executor run goes through `scripts/agent-run.ps1`: it validates the brief, refuses `high` effort or the strong tier without `-EscalationReason`, stops at `-MaxMinutes` and `-MaxInputTokens`, and appends a cost record to the local `agent-runs.jsonl` under `plan/`.
<!-- rule:W-14 roles=coordinator -->
- **Reassessment triggers.** {{slot:reassessment}} the coordinator reassesses: continue, hand over with notes, or stop. A trigger is not an automatic replacement.
<!-- rule:W-15 roles=coordinator,executor -->
- **One validation owner.** The executor runs the focused checks its environment allows, and `verify.ps1` once on its final tree when it can. A check its sandbox cannot run it reports as `ENV-BLOCKED: <command>: <error>`; the runner, `scripts/package.ps1 check`, owns build, focused tests, format and CRLF, and a result is fresh only for the tree it ran on. The coordinator commits, pins, and runs `verify.ps1` on the committed tree; `merge-pr.ps1 -Auto` refuses an older receipt.
<!-- rule:W-16 roles=coordinator -->
- **Limits.** Report both providers' remaining limits with progress reports and after each run (Codex 5-hour and weekly use; Claude status and reset time). Never read OAuth tokens or other credentials to call a usage endpoint.

<!-- rule-section roles=coordinator,reviewer -->
## Review

<!-- rule:W-17 roles=coordinator,reviewer -->
Every behavior change gets an independent review from a different agent than its executor, preferably from the other subscription: {{slot:review-assignment}}.

<!-- rule:W-18 roles=coordinator,reviewer -->
| Review | When | Reviewer |
|---|---|---|
| Light | Default for a behavior change | The other subscription's light model, read-only |
| Heavy | {{slot:heavy-triggers}} | {{slot:heavy-reviewer}} |
| Small-fix exception | All four small-fix conditions hold | The coordinator; logged and counted in the report |

<!-- rule:W-19 roles=coordinator,reviewer -->
- **Independence.** The reviewer gets the brief, the criteria, and the paths, not the executor's or the coordinator's opinion. The reviewer relies on the coordinator's verify receipt bound to the same head and tree and does not re-run the long suites; it runs targeted risk checks (refusals, tamper, reproductions of findings) and returns PASS or FAIL per criterion with evidence, plus what it could not check.
- **Consequences, not locations.** Heavy review follows what a change can break, not the directory. {{slot:consequences-examples}}
{{slot:model-rule-note?}}
<!-- rule:W-20 roles=coordinator -->
- **Review record (in force).** `pwsh scripts/record-review.ps1` writes one record per head SHA under git's common directory (`{{state.commonDirNamespace}}/reviews/<sha>.json`): head and tree, brief hash or requirement, executor and reviewer with sessions, light or heavy, verdict, open blocking findings, and evidence path. Sonnet reviewers stay read-only; the coordinator runs the command and is named as recorder. It refuses a reviewer equal to the executor (same model counts as the same reviewer) unless `-SmallFix`, which only the coordinator as executor may use and which is limited to 2 files and 12 changed lines.
<!-- rule:W-21 roles=coordinator -->
- **Merge gate (in force).** `scripts/merge-pr.ps1` refuses to merge (waiting mode and `-Auto`) without `-Worktree`, a matching verify receipt, and a passing record for the PR head; a stale head or tree, a FAIL verdict, an open blocking finding, or a missing evidence file is refused. {{slot:pin-exception}} Direct `gh pr merge` remains a bypass; a required GitHub check is the next step if bypasses occur.
<!-- rule:W-22 roles=coordinator -->
- **Codex consultations.** Brainstorm and review calls go through `pwsh scripts/codex-ask.ps1` with `-Model` and `-Effort` on every call, including a resume by `-SessionId`; it rejects failed, missing, or stale output.

## Verification and desktop checks

<!-- rule:W-23 roles=all -->
- **Done gate (in force).** `pwsh scripts/verify.ps1` passes on the final tree and writes the gitignored verdict file `verdict.json` under `artifacts/verify`; {{slot:done-gate-ref}}. Receipts are bound to the head and the working-tree hash, so a stale one is detectable.
<!-- rule:W-24 roles=coordinator,executor -->
- **Real-environment check.** {{slot:real-environment}}
<!-- rule:W-25 roles=coordinator -->
{{local:W-25}}
<!-- rule:W-26 roles=all -->
- **Report what was not tested.** A report that omits an untested path claims it passed.

<!-- rule-section roles=coordinator -->
## PR and merge

<!-- rule:W-27 roles=all -->
The coordinator commits, opens PRs, and merges. Executors never commit or push. This overrides the generic "leave changes for user review" default for this repository.

<!-- rule:W-28 roles=coordinator -->
1. Work on a feature branch, never on `{{repository.baseBranch}}`.
2. Commit, then `pwsh scripts/merge-pr.ps1 -PinOnly -Worktree <path>`, then `pwsh scripts/verify.ps1` on the committed tree. The executor's earlier run does not satisfy the merge: a receipt from before the commit is refused.
3. Record the independent review for the committed head: `pwsh scripts/record-review.ps1 -Worktree <path> ...`.
4. Push and `gh pr create`.
5. `pwsh scripts/merge-pr.ps1 -Pr <n> -Worktree <path> -Auto -WaitMerged` under a watcher. It needs the receipt and the review record to match the PR head and tree. "AUTO-MERGE ENABLED" is not merged.

For a package run through `scripts/package.ps1`, `accept` covers step 3 and `ship -Id <slug>` runs steps 2, 4 and 5 bound to the accepted head.

<!-- rule:W-29 roles=coordinator -->
- **Checks.** Every required check ({{slot:required-checks}}) must be present. Every check on the PR head, required or otherwise, must have completed without failure or cancellation; skipped checks are allowed. Never delete the branch or worktree until GitHub says MERGED.
<!-- rule:W-30 roles=coordinator -->
- **Serial.** Merge PRs one at a time; KB edits conflict.{{slot:serial-note?}}
{{slot:pin-policy?}}
- **Repository settings** (rulesets, auto-merge) change only on the owner's explicit wording.

<!-- rule-section roles=coordinator -->
## Status and handoff

<!-- rule:W-31 roles=coordinator -->
- **Status (in force).** the local status file under `plan/` is the store; `scripts/status.ps1 -Set`, `-Done`, `-Decision`, `-Publish` write the pinned status issue and the handoff region. Each active task has a record: objective, accountable agent, stage, SHA or artifact, evidence, next step, blocker, budget and desktop use. Update it at start, material decisions, ready, release, regression, and done; add a daily digest comment on the issue; raise alerts only when intervention is needed. Publish at every stage change; never hand-edit generated regions.
<!-- rule:W-32 roles=coordinator -->
- **Session closure{{slot:closure-ref?}}.** A coordinating session is one work package. {{slot:closure-triggers}}: write the checkpoint (status plus handoff; `/kb-done` does it), finish or safely hand off the running operation (a desktop run, a merge watcher), start nothing new, then close.
<!-- rule:W-33 roles=coordinator -->
- **Owner questions.** Ask with `AskUserQuestion`: 2-4 real options, recommendation first (numbered plain options where it is unavailable), and only for a decision the charter or an approved plan leaves open; destructive or third-party actions are asked about only when neither the charter (A or B) nor an approved plan already authorizes them. A general "yes" approves only what was stated clearly.
<!-- rule:W-34 roles=coordinator -->
- **Final report.** What was done; what was tested and what was not; what was fixed after review; which work went to Claude and which to Codex; the model and effort used for each executor and reviewer run; both providers' limits; what awaits a decision.
{{slot:planned-store?}}

<!-- rule-section roles=coordinator -->
## Incidents and measurement

<!-- rule:W-38 roles=coordinator -->
{{local:W-38}}
<!-- rule:W-35 roles=all -->
- **Trap to avoid.** Rules that live only in memory were not followed{{slot:trap-ref?}}. When a correction is durable, put it in the owning section here or in a feature file as scope, check, and required evidence; use `{{kb.lessons}}` when there is a reusable failure mechanism.

{{slot:additions?}}
{{slot:files?}}
