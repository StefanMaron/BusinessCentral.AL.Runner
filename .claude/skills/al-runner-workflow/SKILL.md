---
name: al-runner-workflow
description: Multi-agent workflow contract for this repo — orchestrator vs implementation agents, GitHub label state machine (status, agent), PR lifecycle, and the role of `al-runner --guide`. Use when acting as orchestrator/impl-agent without the dedicated sub-agent, when triaging the issue/PR queue manually, or when updating runner capabilities (must update PrintGuide() in lockstep).
---

# Agent workflow

This repository uses a multi-agent workflow. Agents are identified by GitHub issue/PR labels.

## Identity

Your agent identity (`impl-1`, `impl-2`, `orchestrator`) is given in the task prompt. It maps to a GitHub label (`agent: impl-1`, etc.).

## Implementation agent loop

If you are `impl-1` or `impl-2`:

1. Check for issues labeled `agent: <your-id>` AND `status: in-progress` — that is your active issue if one exists.
2. If no active issue, find the next unclaimed issue: `status: ready` with no `agent:` label. Claim it: add `agent: <your-id>` + `status: in-progress`, remove `status: ready`.
3. Implement following TDD rules. Branch: `agent/<your-id>/issue-<N>`.
4. Open PR with `Closes #N`. Label the PR `agent: <your-id>` + `status: review-ready`.
5. Monitor: fix CI failures, address review comments, rebase on conflicts. If blocked, label `status: blocked` with a comment and stop.
6. Once merged, return to step 1.

**One issue at a time.** No second claim while a PR is open.

## Orchestrator loop (priority order)

If you are `orchestrator`:

1. **PRs first.** Find PRs labeled `status: review-ready`. CI green + no unresolved threads + no `CHANGELOG.md` in diff + `coverage.yaml` updated → approve and `gh pr merge --auto --squash`. Otherwise leave actionable review comments.
2. **Unblock.** Review `status: blocked` issues; resolve if possible.
3. **(Done.)** Triage of new untriaged issues is owned by the dedicated **`triager`** sub-agent (Opus), which runs at the start of a cycle and decides `status: ready` vs. `status: needs-input`. The orchestrator does not triage and does not mass-create issues from `docs/coverage.yaml` or `docs/limitations.md` (that was a one-time backfill). If the `status: ready` queue is empty and there are no PRs to review, the iteration is done.

Workers self-select from the `status: ready` queue. The orchestrator does not assign issues to specific workers.

## Concurrency with human maintainers

This is a public repo with multiple maintainers. The **GitHub assignee field** is the boundary between agent-owned and human-owned work:

- When an impl agent claims an issue, it adds `@me` as the assignee alongside the labels. PRs the bot opens are also assigned to `@me`.
- Every agent (triager, orchestrator, impl) skips any issue or PR whose assignee is a user other than `@me` — that means a human maintainer is on it.
- A human maintainer who wants to take over an in-flight agent task can simply re-assign the issue/PR to themselves; the agents will back off on their next pass.

## Hard rules (all agents)

- Never push directly to `main` — always via PR.
- Never touch an issue or PR assigned to a non-`@me` user.
- Impl agents never self-assign. Only work on issues the orchestrator queue or your own existing claim.
- Branch name: `agent/<agent-id>/issue-<N>` — no exceptions.
- PR body must contain `Closes #N`.
- Set `status: review-ready` on the PR once CI is green.
- One PR at a time per impl agent.
- `--repo StefanMaron/BusinessCentral.AL.Runner` on every `gh` command (when running outside the repo's default).

## Label state machine

| Label | Meaning |
|---|---|
| `status: ready` | Unclaimed, ready for an impl agent to pick up |
| `status: in-progress` | Currently being worked on by the labeled `agent: *` |
| `status: review-ready` | PR is open and ready for orchestrator review/merge |
| `status: blocked` | Needs human or cross-issue input; orchestrator triages |
| `status: needs-input` | Issue body too thin to identify root cause; reporter must elaborate (set by triager — see `no-assumption-fixes` rule) |
| `agent: impl-1` / `agent: impl-2` | Identity claim on an issue or PR |
| `coverage-gap` | Auto-generated from telemetry / coverage scan |

## `--guide` flag

`al-runner --guide` prints a comprehensive test-writing reference for AI agents. It is the primary discovery mechanism for external agents.

**Always update `PrintGuide()` in `AlRunner/Program.cs` when runner capabilities change** — alongside `README.md` and (if relevant) `docs/limitations.md`. Treat the guide as part of the public API.
