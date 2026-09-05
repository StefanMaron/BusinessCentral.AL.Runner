---
name: autonomous-cycle
description: Run AL Runner development unattended — a never-idle loop that keeps exactly one agent working at a time, in a fixed priority order, across both the runner and the corpus repository. Starts with a preflight that refuses to run on a box that would produce wrong answers, paces itself against a weekly token budget rather than filling each window, and routes anything needing human judgement to a queue instead of guessing. Use when starting an unattended session; not needed for interactive work.
---

# Autonomous cycle

## What the loop is for

The runner exists to behave exactly like Business Central. Tests are not overhead and not a
box to tick — they are the only thing that establishes that claim, and **only the corpus
establishes it against a real service tier.** A runner-local test that passes proves the runner
agrees with itself; it says nothing about BC.

So the measure of a good cycle is not how many issues it closed. It is whether the proof that
the runner matches BC got larger and stayed green. The corpus is a ratchet: every behaviour
pinned there is validated on real BC on every push and can never silently regress afterwards.
As long as the corpus stays green and the runner stays green against the corpus, the runner is
doing its job — and anything not pinned there is a claim nobody is checking.

Read every priority below through that lens. A fix that closes an issue and adds no upstream
coverage has moved a number; a fix that lands with a corpus test has moved the guarantee.

## Working unattended

This runs without anyone watching. That changes what matters: not throughput, but never
producing confident wrong work. An unattended loop that files issues from a poisoned cache, or
merges on a stale verdict, does not just waste a window — it fills the backlog with plausible
garbage that a human then has to unpick.

So the loop is deliberately narrow: **one agent working at a time, in a fixed priority order,
behind a preflight that can stop everything.**

## Why one agent at a time

Measured on 2026-09-05, a coordinator ran nine agents in parallel for about ninety minutes.
Throughput was real, but every finding of lasting value came from *depth*, not parallelism:
4,905 Base App members measured to pin an identifier-mangling rule to exactly seven names; the
discovery that every Base App report's lifecycle triggers were silently empty; a cache-poisoning
diagnosis narrowed to a derived tier whose key has no term for what it was derived from.

In the same session at least four confident conclusions were wrong and were caught only because
a human pushed back or because something was re-measured. Parallelism multiplies that risk.
Serial work with a review step does not.

Exactly one agent runs. When it returns, you review, act, and start the next.

## Preflight — run this first, every time, and stop if it fails

A fresh or drifted box does not announce that it is broken; it produces numbers that look fine.
Every check below exists because its absence has silently corrupted a result.

1. **Known-good baseline.** Run one pinned bucket and compare against its recorded count. This
   single check catches a poisoned cache, a wrong BC version, a missing package cache, a broken
   artifact set and a bad backup reader — all of which otherwise surface as believable failure
   clusters that the loop would file as issues. **If the number does not match, stop and open an
   issue. Do not continue.** Use a private `--cache` dir so the check is about the box, not about
   whatever a previous run left behind.
2. **Push works.** `git ls-remote origin HEAD`. Push auth fails silently when it is routed
   through an interactive credential agent.
3. **Commits work.** Either signing succeeds, or signing is off. A locked signing agent makes
   `git commit` **hang forever** rather than fail — an unattended loop simply stops there.
4. **GitHub permissions.** Probe what this account can actually do — merge, label, assign,
   close. Do not assume, and do not branch behaviour on who is running: the loop behaves
   identically for everyone. A missing permission is a **precondition failure to report**, not a
   second mode to implement.
5. **Headroom.** Read free RAM and disk, and derive worker and job counts from them. Never
   hardcode. Set `MemoryHigh` below `MemoryMax` on any long run so a cgroup throttles before the
   kernel's global OOM killer starts choosing victims elsewhere on the machine.
6. **Claim a label slot, do not just derive one.** The account gives you a namespace; a slot
   number makes you unique *within* it, because one person may run several loops at once.

   Derive the namespace from the account the loop is logged in as — `gh api user --jq .login` —
   since logins are already unique, so a tag derived from one inherits that for free. Uniqueness
   only comes back into question when you **abbreviate**, and an abbreviation is what makes a
   readable label, so verify rather than assume: list the `agent:` labels already on the
   repository and confirm your intended namespace does not appear on issues or PRs belonging to
   someone else. On a clash pick another and re-check, or stop and report it.

   Then take the lowest free slot — `<tag>-1`, `<tag>-2`, … — by asking the repository, which is
   the only state two loops share:

   - A slot is **taken** if any open issue or PR carries that label and is still being worked.
   - A slot is **free** if its label exists but nothing open carries it, or it does not exist.
   - Claim the lowest free one, then **re-read**. Two loops starting together will pick the same
     slot; if another loop's work appeared under yours, take the next and re-check. Same
     compare-and-swap as issue claiming, and for the same reason.

   Hold the slot visibly. A loop between tasks owns no work, so its slot looks free to a loop
   starting up — keep your label on whatever you are working, and release it when you finish, so
   the repository always shows which slots are live.

   Use that one identity everywhere: labels, branch names, worktree directories, scratch and
   cache paths. Several loops can then run under one account, and several accounts against one
   repository, without ever writing the same name.

   The existing `agent: impl-N` convention is the counter-example worth avoiding: a global
   counter with no owner, which drifted to `impl-69` while leaving 82 worktrees and 10 GB of disk
   behind. Slots numbered *inside* an account namespace cannot drift that way — they are
   reclaimed by the next loop that starts, instead of incremented forever.

7. **No stale worktree for this identity**, and no leftover scratch directories from a killed
   run.

**Print the preflight as a readable report** — one line per check, PASS or FAIL with the reason
and the command that produced it. That report is what a new contributor reads to find out
whether their box is set up correctly, so write it for a person who has never run this before,
not as a log line. A FAIL should say what to do about it.

Record the result. If a later cycle behaves oddly, the first question is whether the box
drifted since.

## Pin the model on every dispatch

Every agent definition here is pinned to Opus, but **pass the model explicitly when you dispatch
anyway.** Frontmatter is easy to overlook and a default is silent: `impl-agent` and
`orchestrator` sat on `model: sonnet` for a long time, so an unattended loop would have run its
implementation and its merge decisions on the smaller model without anything saying so.

The coordinator loop itself, and every agent it spawns — implementation, review, triage — run on
Opus, at high reasoning effort where the harness exposes it. This work is diagnosis: today's
findings came from decompiling BC to pin an identifier rule to seven names out of 4,905, and
from separating a cascade of 47 failures into one defect. That is not throughput work, and the
cheaper model is a false economy when a wrong diagnosis becomes a filed issue nobody can trust.

If you cannot confirm what the running model is, say so in the cycle's report rather than
assuming the pin took.

## The box profile

The preflight measures the machine and records what it found. Everything downstream reads that
instead of re-measuring or guessing, and it is what makes the same skill work on someone else's
hardware without being told anything about it.

Write it to a **gitignored** file — it describes this box and this loop, not the repository, and
it must never be committed or shared. Suggested `.claude/autonomous-state.json`, already ignored.

What belongs in it:

- **The machine** — total and available RAM, free disk, core count.
- **What those imply** — the worker and job counts derived from them, so later cycles do not
  re-derive numbers inconsistently. Roughly 1.1 GB per worker without test data and ~2.3 GB with
  it, but derive from what you measured, not from those figures.
- **The identity** — the account, the label namespace and the slot claimed at startup.
- **The baseline** — which bucket was run, the expected count and the count observed, with a
  timestamp. That is the record that says this box was healthy at a known moment.
- **The preflight verdict** — every check with PASS or FAIL, so a later cycle behaving oddly can
  be compared against a known-good starting state.
- **Pacing observations** — how much budget a cycle actually consumed, so the gap between cycles
  can be tuned from evidence rather than guessed.

Rewrite it at every startup rather than trusting yesterday's. A box changes: disks fill, other
work starts, an artifact set goes stale. A stale profile is worse than none, because it looks
authoritative.

## Priority order

Work the first item that applies. Re-evaluate from the top after every completed unit of work —
a merge can turn `main` red, which outranks everything you were about to do.

1. **`main` is red.** Nothing else matters. Read the failing log (never re-run a failed job —
   it destroys the log), diagnose, fix.
2. **One of our PRs is red.** Drive it green, or close it with the reason. A red PR left open is
   worse than no PR: it looks like progress.
3. **A PR is waiting on review.** Dispatch a review agent, or review it directly. Ours can be
   merged on a green pipeline; someone else's is reviewed and its findings go to the human queue
   — never merged, never commented on unattended.

   **Every review must answer one question before the merge bar: does this PR assert something
   about what BC does?** If it does, the proving test belongs upstream in the corpus, and this PR
   must either link a corpus PR or explain why the corpus cannot express it. Runner
   infrastructure — process configuration, CI plumbing, error handling, caching, parallelism —
   asserts nothing about BC and owes nothing upstream. A change to what the runner makes AL code
   *observe* almost always does.

   "The corpus cannot express this" is a claim needing evidence like any other. It is sometimes
   true, and the reason is structural: corpus tests are compiled from AL source *by the runner*,
   so a defect that only affects **precompiled** dependency artifacts cannot be reproduced there
   — the test would take the source-compiled path and pass. That answer is acceptable when the
   PR names that structural reason and puts its proving test in `tests/runner-extras/`. It is not
   acceptable as a way to avoid writing the upstream test.

   **Do not merge a PR that closes a BC-behaviour issue on the strength of a runner-local test
   alone.** That is the exact failure `bc-behavior-tests-go-upstream.md` exists to prevent, and it
   is invisible: the runner-local test is green and nothing complains. Unattended, nobody will
   notice — so the review step is the only place it can be caught.

   Corollary for the loop's own output: if it merges several fixes and opens no corpus PR, treat
   that as a signal to check rather than as evidence the work was all infrastructure.
4. **A corpus PR has all legs green.** Merge it, then fold the submodule pin bump and the
   count-baseline update into the runner PR that needs it. A pin bump is never its own PR.
5. **An issue is ready to work.** Take the highest-value one — prefer a measured failure count
   over a guess — and implement it. One issue at a time.

   Use the `status: ready` label where it exists, but **do not depend on it.** The loop must work
   on a repository whose labels are absent, stale, or organised differently. Fall back to: open,
   unclaimed, and carrying enough to act on — a reproducer, a failing test name, or a concrete
   file and mechanism. An issue too thin to act on is not a candidate; say so on it and move on
   rather than guessing (`.claude/rules/no-assumption-fixes.md`).
6. **The ready queue is empty — generate work.** This is what keeps the loop from idling, and it
   is the step most able to do harm, so it has a mandatory gate:

   ```
   run a Microsoft BaseApp bucket in a known-good configuration
     -> cluster the failures
       -> re-run the top cluster against a CLEAN cache to confirm it is real
         -> only then file an issue, with the measured count
   ```

   **The clean-cache confirmation is not optional.** It is the difference between the loop
   compounding value and compounding noise.

7. **Still nothing to do — grow the corpus.** Pick AL behaviour the runner supports but nothing
   upstream pins, write the test, and open the corpus PR. This is real work, not filler: it
   converts behaviour that currently happens to work into behaviour a real service tier
   guarantees, and it is the only priority that makes the runner harder to regress rather than
   just less broken.

   Good candidates are surfaces a fix recently touched without adding upstream coverage, areas
   where a runner-local test is doing a job the corpus should be doing, and anything a past fix
   proved by reasoning rather than by a service tier's verdict. The corpus CI adjudicates the
   claim, so a wrong guess costs a red leg and nothing worse.

   Configuration matters as much as the run. In one measured no-test-data run, roughly 40% of
   all failures were missing setup data rather than defects — clustering that would have
   produced a stream of confident, wrong issues. Never file from a run whose configuration you
   cannot vouch for.

## Claiming, and not colliding with other contributors

Several people may run this loop at once, against the same repository, with no coordination
between them. Nothing may depend on them talking to each other.

**The assignee locks; your label discriminates.** Both matter and they do different jobs: the
assignee is what stops two agents working the same issue, and your own agent label is what lets
you (and anyone reading the repository later) tell which work came from which loop. Set both.

**The GitHub assignee is the lock**, and it decides the order you look in. It is visible to
everyone, survives a crashed box, and needs no shared state between contributors. A dedicated
label is useful for telling afterwards which work the loop produced — but the label is
bookkeeping; the assignee is what prevents two agents doing the same issue.

**Look in this order, and it works for any account:**

1. **Issues already assigned to the account you are logged in as.** Finish your own work before
   starting more. This is also how a restarted loop resumes: a box that died mid-issue comes
   back, sees its own claim, and picks up where it left off instead of stranding it.
2. **Unassigned issues.** Take the highest-value one and assign it to yourself.
3. **Issues assigned to anyone else — leave them alone.** Someone is on it, whether that is a
   human maintainer or another contributor's loop. Do not take it, do not work it in parallel,
   do not comment on it.

That ordering is the whole cross-contributor story: every loop works its own queue first, draws
from the common pool second, and never touches another's. No coordination needed.

**Claiming is read-then-write, so it races.** Two loops listing candidates at the same moment
pick the same top issue. Use compare-and-swap rather than trusting the write: assign it, then
**re-read**. If another assignee appeared, or yours did not stick, release and take the next
candidate. Losing a race costs one API call; two agents silently doing the same issue costs
both.

**Release when you stop.** Finished without a fix, blocked, or shutting down for budget: remove
your assignment so the issue returns to the pool. An issue you cannot finish should not stay
parked under your name.

**Your own stale claims are yours to reclaim** — a claim of yours with no linked PR and no
activity for hours is from a run that died, and rule 1 above picks it up automatically.
**Someone else's stale claim is not yours to take**, even if it looks abandoned. You cannot tell
a dead box from a contributor who is asleep. Surface it to the human queue and move on.

**Namespace anything per-contributor** that lives on disk or in a branch name — worktrees,
branches, scratch directories, cache directories. Two contributors must never write to the same
path or push to the same branch. Derive the namespace from the account running the loop.

## Pacing

The five-hour windows are real, but **exhausting every window burns the weekly limit in a
couple of days.** The budget that matters is weekly, so run continuously *below* per-window
capacity rather than sprinting and then idling.

One agent at a time is most of the pacing. Beyond that, keep a deliberate gap between cycles and
tune it from observed consumption rather than from a guess — start conservative and measure how
usage actually grows over a full day before increasing it.

Never leave an agent mid-task when a budget boundary is near. Have it commit and push what it
has, even incomplete, and report where it got to. A work-in-progress commit on a pushed branch
survives; an uncommitted worktree does not.

## The human queue

Some things must not be decided unattended. Label them and move on — do not block, and do not
guess:

- A genuine product decision. Example: `--test-data` presents a company whose configuration
  differs from Microsoft's test database. Whether hydration should change that is a judgement
  call, not an agent's.
- Anything posted publicly on someone else's behalf — comments, review comments, anything in
  another repository.
- Merging a PR authored by anyone other than the repository owner.
- A refusal that might be a scope boundary rather than a defect.

## Blast radius

Hard limits, whatever the loop concludes:

- Never push to `main`; always a PR.
- Never force-push a branch you do not own, and never `--admin` past a failing check.
- Never merge a PR authored by someone else.
- Never re-run a **failed** CI job — it destroys the log permanently. Re-running a **cancelled**
  one is fine; it has no failure log to lose, and a cancelled required context can block a merge
  with everything green.
- Never delete a shared cache or a scratch directory you did not create. Other work may be live.
- Never read a secret, and never try to unlock a credential agent.

## What "done" looks like for one unit of work

A cycle ends with something durable and checkable: a pushed branch, a PR whose head SHA matches
what was tested, or an issue carrying a measured count and a reproducer. A cycle that ends with
a conclusion only in an agent's context has produced nothing — that context is discarded.

Prefer a complete negative result over a speculative fix. "I could not reproduce it, here is
what I ruled out and why" is a finished unit of work, and on a busy repository it is often worth
more than a patch that makes a symptom disappear.

## Sister material

`orchestrating-a-session` — the judgement calls, the merge bar, the measurement rules and the
environment traps. This skill is that one, narrowed to run unattended.
`.claude/commands/work-cycle.md` — the interactive, parallel equivalent.
