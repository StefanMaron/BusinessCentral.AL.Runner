---
description: Run one full work cycle — triage untriaged issues, then implement every `status: ready` issue in parallel worktrees with the orchestrator merging PRs as they land. Stops when the ready queue is empty and no PRs are open.
---

# /work-cycle

You are driving one full work cycle on the AL Runner repo. Use the `Agent` tool to dispatch sub-agents — do **not** do triage / implementation / merging yourself. Your job is the conductor's: kick things off, watch state, decide when to stop.

Always pass `--repo StefanMaron/BusinessCentral.AL.Runner` on every `gh` command.

**Concurrency with human maintainers.** This is a public repo. The **GitHub assignee field** is the boundary: agent-owned items are assigned to `@me`; human-owned items are assigned to a maintainer's account; anything assigned to a non-`@me` user is hands-off across every phase. The sub-agents enforce this internally — your own state-reads in Step A should also filter on `--assignee @me` (or empty assignee) so you don't count human-owned work toward the queue.

## Phase 1 — Triage pass (foreground)

Spawn the `triager` sub-agent **once**, foreground, and wait for it to finish.

```
Agent({
  subagent_type: "triager",
  description: "First-pass triage of untriaged issues",
  prompt: "Run one full triage pass per your agent definition. Cover every open issue without a `status:` or `agent:` label. Mark `status: ready` or `status: needs-input`, close obvious out-of-scope/duplicate cases, leave genuinely ambiguous ones for human review. Stop after one pass — do not loop."
})
```

Report the triager's summary numbers (ready / needs-input / closed / left-alone) to the user before continuing.

## Phase 2 — Implementation loop

Settings:
- **Concurrency**: up to 2 implementation agents in parallel (identities `impl-1`, `impl-2`).
- **Isolation**: each impl agent runs with `isolation: "worktree"` so it has its own checkout and branch.
- **Background**: impl agents run with `run_in_background: true` so the orchestrator pass can run alongside them.

### Loop body — repeat until terminal

**Step A — Read state.** Resolve the authenticated user once, then filter to that user or empty (skip human-owned work):
```bash
ME=$(gh api user --jq .login)

gh issue list --label "status: ready" --state open --json number,title,assignees --repo StefanMaron/BusinessCentral.AL.Runner \
  | jq --arg me "$ME" '[.[] | select(.assignees | length == 0 or any(.login == $me))]'

gh pr list --label "status: review-ready" --state open --json number,title,assignees --repo StefanMaron/BusinessCentral.AL.Runner \
  | jq --arg me "$ME" '[.[] | select(.assignees | length == 0 or any(.login == $me))]'
```
Also check which impl identities are currently busy: an identity is busy if there is an open issue or PR labelled `agent: <id>` that is not yet merged/closed.

**Step B — Spawn impl agents to fill free slots.**
For each free identity slot (`impl-1`, `impl-2`) where the queue still has unclaimed `status: ready` issues, spawn one agent in background with worktree isolation:

```
Agent({
  subagent_type: "impl-agent",
  description: "impl-<id> claim and implement next ready issue",
  prompt: "You are <AGENT-ID>. Follow your agent definition exactly: claim the next `status: ready` issue with no `agent:` label, implement with strict TDD, open a PR with `Closes #N`, label it `agent: <AGENT-ID>` + `status: review-ready`, then monitor through CI until merged or blocked. Hard stop after one issue — do not loop to a second.",
  isolation: "worktree",
  run_in_background: true
})
```

Substitute `<AGENT-ID>` with the actual identity (`impl-1` or `impl-2`).

**Step C — Run an orchestrator pass (foreground).**
While impls work, sweep the PR queue once:

```
Agent({
  subagent_type: "orchestrator",
  description: "PR queue sanity-review and merge pass",
  prompt: "Run one orchestrator pass per your agent definition. Sanity-read every `status: review-ready` PR against its linked issue, apply mechanical checks (CHANGELOG / coverage.yaml / SA-implementation), merge what passes, leave actionable comments on what doesn't. Handle `status: blocked` issues if any can be resolved. Do **not** triage new issues (the triager owns that). Exit after one full pass with no further actions."
})
```

**Step D — Wait and re-evaluate.**
You will be notified when background impl agents finish. When any impl finishes (success or blocked):
1. Re-read state (Step A).
2. If new `status: ready` issues exist and a slot freed up, go to Step B.
3. Otherwise go to Step C to merge whatever the impl just produced.

### Terminal conditions

Stop the loop when **all** of the following hold simultaneously after a fresh state read:

1. `status: ready` queue is empty.
2. No `status: review-ready` PRs are open.
3. No background impl agents are still running.

At that point every issue that started this cycle as `status: ready` is now in one of:
- **Done** — PR merged, issue auto-closed via `Closes #N`.
- **Blocked** — labelled `status: blocked` with an explanatory comment from the impl agent.
- **Needs input** — re-flagged `status: needs-input` if the impl agent discovered the issue body was actually too thin once they tried to reproduce it.

## Phase 3 — Final report

Print to the user:
- Issues triaged (from Phase 1).
- Issues merged this cycle (count + numbers).
- Issues blocked this cycle (count + numbers + one-line reason each).
- Any PRs that landed but failed the orchestrator's sanity-read — list them with the comment that was posted.
- Worktree paths the runtime did not auto-clean (those still hold work; mention them so the user can inspect).

## Hard rules

- **You don't do the work.** Triage, implementation, and PR review all happen inside sub-agents. Your only direct `gh` calls are the read-only state reads in Step A.
- **Don't deep-poll.** When background agents are running, wait for the runtime's notification rather than busy-checking.
- **Don't re-run the triager mid-loop.** It runs once at the start of the cycle. New telemetry-driven issues that arrive mid-cycle will be picked up by the next `/work-cycle` invocation.
- **Don't escalate concurrency past 2** without an explicit user instruction — the conventional impl identities are `impl-1` and `impl-2`, and going higher means inventing new identities and reasoning about queue contention.
- **Stop when the terminal condition holds.** Do not invent more work to do; report and exit.
