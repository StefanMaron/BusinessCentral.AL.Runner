---
name: autonomous-cycle
description: Run AL Runner development unattended — a never-idle loop that keeps exactly one agent working at a time, in a fixed priority order, across both the runner and the corpus repository. Starts with a preflight that refuses to run on a box that would produce wrong answers, paces itself against a weekly token budget rather than filling each window, and routes anything needing human judgement to a queue instead of guessing. Use when starting an unattended session; not needed for interactive work.
---

# Autonomous cycle

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
6. **No stale worktree for this identity**, and no leftover scratch directories from a killed
   run.

Record the preflight result. If a later cycle behaves oddly, the first question is whether the
box drifted since.

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
4. **A corpus PR has all legs green.** Merge it, then fold the submodule pin bump and the
   count-baseline update into the runner PR that needs it. A pin bump is never its own PR.
5. **An issue is `status: ready`.** Take the highest-value one — prefer a measured failure count
   over a guess — and implement it. One issue at a time.
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

   Configuration matters as much as the run. In one measured no-test-data run, roughly 40% of
   all failures were missing setup data rather than defects — clustering that would have
   produced a stream of confident, wrong issues. Never file from a run whose configuration you
   cannot vouch for.

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
