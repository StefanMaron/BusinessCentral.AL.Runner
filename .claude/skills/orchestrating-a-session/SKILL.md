---
name: orchestrating-a-session
description: How to run a coordinator session on this repo — what you decide versus what you delegate, the implementation-agent identity pool and when to reuse it, corpus-PR authority, when to re-run triage, the merge bar, the measurement rules, and the environment traps that silently produce wrong answers. Use at the START of any session where you will drive work through subagents, review and merge PRs, or keep the issue queue moving. Invoke it instead of being told these things again.
---

# Orchestrating a session on AL Runner

You are the coordinator. You spawn implementation agents, review what they produce, drive
PRs to green, and merge. You do not usually write the fix yourself — but you do own the
judgement calls, the merges, and the honesty of what gets reported.

Everything below is here because it had to be explained more than once. Read it once at the
start of a session rather than rediscovering it.

## What you decide, what you delegate

**Delegate:** diagnosing a cluster, writing the fix, writing the proving test, driving that
one PR through CI.

**Keep:** which clusters are worth attacking and in what order; whether a result is real;
whether a PR meets the bar; every merge, in both repos; corrections to issues whose premise
has been falsified.

**Do the work yourself when you already have the answer.** If you have just measured
something, spawning an agent to re-measure it wastes a full context. Write the PR.

## Authority — you do not need to ask for these

- **Filing issues** on `StefanMaron/BusinessCentral.AL.Runner`, and **correcting the body of
  an issue you filed** when a measurement contradicts it.
- **Merging PRs** in both this repo and the corpus repo
  (`StefanMaron/BusinessCentral.AL.Language.Tests`), once they meet the bar below.
- **Agents opening corpus PRs.** This is not gated. They open; you review and merge when all
  8 BC legs are green. Agents never merge a corpus PR themselves. If an agent stops to ask
  permission for this, tell it to go ahead — its definition may predate the change.

**Still gated, ask first:** comments on issues or PRs (including the corpus repo), PR review
comments, anything posted to another repo. That is editorial content, not a workflow step.

## Implementation agents

Use the `impl-agent` subagent type, not `general-purpose`. Its definition carries the
workflow contract — branch naming, labels, the CI rules, the navigation tooling — so your
brief only needs the cluster context and the traps.

**Do not ramp up identity numbers.** The documented pool is `impl-1`/`impl-2`, widened by the
owner when concurrency is raised. **A finished agent's identity is immediately free — reuse
it.** Every new identity leaves a permanent worktree behind; inventing `impl-3` … `impl-12`
across a session leaves a dozen. Reuse first, and only invent one when every identity is
genuinely in flight.

**Brief with cluster data and traps, not with pre-resolved symbols.** Resolving an agent's
symbols for it moves cost onto your own long-lived context, which is backwards. Give it the
failing test names, the stack top, the counts, the falsified hypotheses — and let it navigate.

**A good brief says what a *complete answer* looks like**, including a negative one. "These 6
are cause A and these 10 are cause B, here is the evidence" is a complete answer with no fix.
Say so explicitly, or agents will force one fix over two causes to make the PR look bigger.

**Check in on long runners.** Past ~90 minutes, ask: where are you, is anything unpushed, is
there a PR, are you blocked. Agents will sit on finished work waiting for permission they
already have.

## Triage

Run the `triager` subagent at the **start** of a cycle, and again whenever the open-issue
count has grown by roughly 20 or the queue has visibly drifted. Sonnet is a fine fit.

The queue grows for a reason worth naming: **issues get fixed by a PR that cites a different
number, so nothing auto-closes them.** Ask triage for three things — already-fixed issues
with the commit that fixed each, duplicate clusters with a canonical, and status labels for
the untriaged. Have it **apply labels directly** (mechanical) but **close nothing and comment
nowhere** — bring the closure list back for approval.

## The merge bar

Merge when **all of**:

1. All 8 required legs green **on the PR's current head SHA**. `gh pr checks` reports the
   newest *completed* run, which can predate the last push — confirm the SHA.
2. `git merge-tree --write-tree --messages origin/main origin/<branch>` is clean.
   `mergeStateStatus: CLEAN` only covers textual conflicts.
3. The proving test exists. If the claim is about BC's behavior, that test is upstream and
   merged, or merging in the same pass.

**Use `tools/ci-wait.py <PR>`** rather than a poll loop. It polls internally and returns one
verdict: 0 green on current head, 1 failed with the log already fetched, 2 still running
(*not* a verdict), 3 undetermined.

**Never `gh run rerun` a failed job** — it destroys the log permanently. Read
`--log-failed` first, then push a new commit.

**Order matters when PRs carry submodule pins.** Two PRs both bumping the pin and the
count-baseline will conflict; merge one, then tell the other to rebase and *re-measure*
rather than carrying its old number forward.

## Measurement rules

These exist because each was violated at real cost.

- **Never conclude from a run that has not finished.** A partial local run once produced a
  three-class failure list; the completed CI run found five failures in two further classes.
- **Wall clock lies on a loaded box.** With several agents running, identical work measured
  1.9s and 3.1s. Use instructions-retired (`perf stat`) for anything CPU-bound; it held to
  ±0.1% across the same runs.
- **A children-inclusive profile percentage is not a saving.** In a JIT-dominated process it
  measures what is *reachable* from a call site; deleting the caller moves the cost to the
  next one. Price a change by removing the work and re-measuring, not by reading a call tree.
- **Include a control.** Convert three classes, leave two untouched, and show the untouched
  ones flat. That is what makes the deltas believable.
- **Do not rank work by the bc-linux container comparison.** That tier patches BC's binaries
  at startup, and filtering by it once hid the single largest cluster in the bucket — 102
  tests, worth +93 when fixed.

## Settling a claim about BC

Order of evidence, highest first: a corpus test green on a real service tier; BC's own
shipped IL; Microsoft's documentation; the name of a codeunit. A name is not evidence.

If no corpus test covers the shape, write one and let the corpus CI adjudicate — minutes, not
a local container. If no verdict is available at all, say so plainly and land the runner
change with whatever coverage is legitimately available. Never write an unmeasured claim into
an issue, a doc table, or a comment as though it were established.

**When an agent's measurement falsifies an issue you filed, correct the issue body.**
Otherwise the next agent starts from the wrong premise — which has happened here.

## Environment traps that produce silently wrong answers

- **`grep` is a shell function.** It rejects `-E` and `--include` with
  `error: unknown option '-G'` **and exits 0 with no output**, which reads exactly like "no
  matches". Use `command grep` or `rg` before believing an empty result.
- **Always pass a private `--cache <dir>`** to runner invocations. The shared
  `~/.cache/al-runner` is not keyed on the runner binary, so a concurrent agent's payload can
  make a fix look like it did nothing.
- **One hung AL test ends the whole run**, reporting a partial count that looks like a
  regression. Park the codeunit rather than reaching for `--test`.
- **The MS test company is `CRONUS International Ltd_`** — underscore, not a period.
- **`.mcp.json` changes need a session restart.** Registering an MCP server mid-session does
  nothing for that session or its subagents.
- **The network here times out intermittently.** Wrap `gh` in a retry; a bare `i/o timeout`
  is not an answer, and treating one as "no results" corrupts whatever you concluded.

## Tooling you should be using

- `tools/context-pack.py <Name>...` — definition + source + call sites, one round trip.
- `tools/lsp-query.py callers|symbol <Name>` — exit 2 means the server failed, **not** "none".
- `mcp__bc-decompiler__*` — BC's own code. Contexts `bc260` … `bc284` are pre-registered;
  `search_members` → `memberId` → `get_decompiled_source` / `find_callers`.
  `compare_symbols` diffs a method between BC versions, which is how a Cecil rewrite that
  stopped being reached gets caught.
- `tools/agent-cost.py <tasks-dir>` — where a session's agents actually spent their calls.
  Measured once: 85% of Bash calls were shell read/search and the navigation tools were used
  3 times in 3,237 calls. Re-measure rather than assuming it improved.

## Reporting to the owner

State what was measured and what was assumed, separately. When you got something wrong, say
so plainly in a sentence and move on — no ceremony. Give the number that the reasoning
actually produces, including when it is worse than hoped: an estimate of "about 9 minutes"
that holds beats "under a minute" that does not.

## Sister rules

`.claude/rules/` is auto-loaded and remains authoritative. The ones this skill leans on most:
`ci-verdicts.md`, `branch-and-pr.md`, `public-posting-approval.md`,
`bc-behavior-tests-go-upstream.md`, `ask-the-corpus-before-claiming-bc-behavior.md`,
`no-base-app-in-csharp-tests.md`, `local-test-scope.md`, `no-git-stash-with-worktrees.md`.
