---
name: kb-done
description: Close a {{slot:product}} work session - bring the knowledge base up to date or confirm it is unchanged, run the KB check, run the done gate, write the status checkpoint (coordinator), and report. Use when the user types /kb-done, and at the end of every task or session before the final message.
---

# /kb-done: finish every session here

`/kb-done [-Role coordinator|executor|reviewer]` closes the work. The role defaults to the one this session's `/kb-init` used, else `executor`. It does not commit, push, or merge; executors never do, and the coordinator does those through the workflow's own steps.

Do these in order. Skip a step only when it cannot apply; with no changes at all, say so and stop after step 2.

1. **Knowledge base.** Run `{{slot:kb-command}} changed -Base <merge-base of HEAD and {{repository.baseRef}}>`; the uncommitted changes count too. For each section or feature it lists, apply the `/kb-write` rules: update it, or record `reviewed unchanged: <reason>` for the final message. A renamed or moved file always needs an update. A reusable trap you diagnosed in this session goes to `{{kb.lessons}}` and is linked from its owning section.
2. **Check.** Run `{{slot:kb-command}} check`, then `{{slot:kb-command}} check -NoFix`, and fix every `ERROR` until it prints `0 errors`.
3. **Done gate.** {{slot:done-format}} Then run `pwsh scripts/verify.ps1` until it prints `VERIFY PASSED`, iterating on the failure details. After several honest attempts, or if passing would need a weaker test, stop and report what fails and why.
4. **Status checkpoint (coordinator only).** Follow "Status and handoff" in `{{kb.workflow}}`: `pwsh scripts/status.ps1 -Set` for each active task, then `pwsh scripts/status.ps1 -Publish`. Never hand-edit the generated regions.
5. **Final message, short.** KB (updated sections, reviewed unchanged with reasons, or nothing), check result, verify result, and what is left open.
