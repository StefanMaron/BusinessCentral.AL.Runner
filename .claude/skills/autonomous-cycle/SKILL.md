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

The test for whether a fix owes a corpus test is wider than "is this a claim about BC?":
**wherever it is possible to red-test something with AL tests, that should add tests to the
corpus** — that is, wherever the fix can be proven by AL running against a real service tier. A
BC-behaviour claim is the common case, not the whole rule. The service-tier clause is what keeps
the rule legal: runner-only claims are red-testable in AL too — out-of-scope reasons, AL-output
cache HIT/MISS, provisioning-gap messages, exit codes — and they stay in `tests/runner-extras/`,
because the corpus does not know about AL Runner and must stay that way
(`al-language-submodule.md`).

The runner fix is still the priority, and the work never stalls on the corpus. Open the corpus
PR and keep the runner fix moving — implemented, pushed, reviewed — rather than idling until
the corpus legs report; priority 3's merge bar decides when it may *merge*, and nothing before
that waits.

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

Exactly one **implementation** agent runs, plus one **reviewer** alongside it. That pairing is
the default and the human changes it at session start, not the loop. One implementer produces at
most one PR at a time, so a single reviewer always outpaces it and review cannot fall behind by
construction. When the implementer returns, you act on it and start the next; when the reviewer
returns, you start the next reviewer rather than a second implementer.

## Reviewing is not overhead, and it is where queues actually stall

**The default is one implementation agent and one reviewer.** Not a ratio to compute — a
baseline to start from, changed only by the human at session start. One implementer produces at
most one PR at a time and one reviewer clears roughly four an hour, so review cannot fall behind
by construction, and the pile-up this section describes never begins.

The measured throughput below is what to scale *by* when a human raises the concurrency, not a
license to raise it. At six implementation agents you need roughly two reviewers to hold steady;
work that out from the numbers rather than adding implementers because slots are free.

When a coordinator does run several agents at once — the attended mode this skill's serial rule
does not cover — the thing that breaks first is review, not implementation. Measured across one
attended session on 2026-09-06:

| agent | work | wall | rate |
|---|---|---|---|
| reviewer (runner PRs) | 6 PRs in one pass | 93 min | ~15.6 min/PR |
| reviewer (corpus PRs) | 3 PRs in one pass | 64 min | ~21 min/PR |
| implementation agent | 1 PR each | 35-85 min | ~1 PR/hour |

So **one reviewer sustains roughly 4 PRs/hour, and six implementation agents produce 5-6.**
The queue grows by arithmetic, not by anyone choosing badly. The balancing ratio is about
**one reviewer per four implementation agents**, and a coordinator that spawns implementation
agents whenever a slot frees will fall behind indefinitely without ever making an obvious
mistake.

**Count an open unreviewed PR against the concurrency budget, exactly like an unfinished
implementation.** A coordinator running 6 implementation agents with 6 unreviewed PRs is
running at 12, not 6, and should stop starting new work. This is the accounting that makes
priority 3 below fire on its own instead of needing to be remembered — the priority order
already puts "a PR is waiting on review" *above* "an issue is ready to work", and it still got
skipped, because starting an implementation agent feels like progress and starting a reviewer
feels like overhead.

**Review in batches of three or four, not one at a time and not six or more.** Both extremes
were measured:

- **Batches that are too large go stale.** A batch of six took 93 minutes; during it, three PRs
  from the original brief merged and two of the remaining six had their head SHA move. Two of
  six verdicts came back "no verdict on current head" — a third of the batch wasted. Review
  takes ~15 min/PR and heads move every 30-60 min under load, so the batch has to finish inside
  the window in which its subjects hold still.
- **Batches of one lose the findings that matter most.** The single most valuable result in that
  session was cross-PR: two PRs bumped the same submodule pin to different revisions, conflicting
  three ways, and the reviewer worked out which had to merge first because its revision was an
  ancestor of the other. **A one-PR-at-a-time reviewer cannot see that**, and neither can the
  coordinator, who is not reading the diffs.

**A reviewer that approves a PR arms auto-merge on it immediately, in the same pass.** Do not
hand an approval back to the coordinator and wait for it to act — that round trip is where the
verdict goes stale, and staleness is the main cost of reviewing in batches. The reviewer has
just read the head SHA; it is the only actor that knows the verdict and the SHA are consistent
at that instant.

```bash
gh pr merge <N> --repo <owner>/<repo> --squash --auto
```

Arm **only** when all of these hold. Any one missing means report it to the coordinator instead:

- **The PR is on a branch this loop owns.** Check the **branch prefix**, never the author field
  — every loop running under one account reports that account as the author, and an outside
  contributor's PR is never merged by us.
- No release run is in progress (`publish.yml` pushes a fast-forward; a merge during its
  ~40-minute run kills it).
- `git merge-tree --write-tree --messages origin/<base> origin/<branch>` is clean.
- If the PR asserts anything about BC's behaviour, its corpus PR has **merged**, and the pin bump
  and count-baseline update are folded in.
- No *other* PR in the same batch conflicts with it. Where two do — two submodule pin bumps to
  different revisions, say — arm only the one that must merge first and report the ordering.

**Record the SHA you armed against** in the verdict. If the head moves afterwards, GitHub keeps
auto-merge armed against the new head, which nobody has reviewed; the coordinator needs the SHA
to notice.

**Arming is refused on a PR that is already mergeable** — GitHub answers `Pull request is in
clean status`, because there is nothing left to wait for. That is not an error to retry or
report as a failure: it means the PR can merge now, so say so and let the coordinator merge it
directly. Check the exit code; a loop that printed "armed" regardless of it once left four green
PRs sitting unarmed.

Arming is not merging, and it does not replace the merge bar — it is the bar expressed as a
standing instruction to GitHub, so a PR lands the moment its checks go green instead of at the
coordinator's next sweep.

**Keep one reviewer continuously alive rather than spawning one when a queue becomes visible.**
Reactive spawning is what produces the pile-up: by the time the queue is obvious it is already
several PRs deep, and the batch needed to clear it is large enough to go stale. Start the
replacement when a reviewer returns.

**A review verdict must name the head SHA it was made against.** Heads move under a review in
minutes when other loops and outside contributors are pushing. A verdict without a SHA cannot be
checked for staleness, and merging on a stale one has already nearly merged a commit whose CI was
red. Re-read the head immediately before merging and pass `--match-head-commit`, so the merge
refuses rather than silently taking something else.

These numbers come from a single session and review time varies with PR size. Re-measure with
`tools/agent-cost.py` before treating the ratio as fixed.

## Preflight — run this first, every time, and stop if it fails

A fresh or drifted box does not announce that it is broken; it produces numbers that look fine.
Every check below exists because its absence has silently corrupted a result.

**Run `tools/preflight.py`.** It is this section, executable, with a real exit code — 0 all
passed, 1 something failed and this box would produce untrustworthy results, 2 warnings under
`--strict`, 3 it could not complete. `--json` for a box profile, `--reap` to remove worktrees
whose PR is merged and whose tree is clean, `--with-corpus` to include step 1. The prose below
stays as the specification and the reasoning; the script is how it actually gets run, because a
check a busy coordinator can decline is not a check — one skipped step 5 across an evening of
~20 agents and filled a 7.7 GB tmpfs, after which every shell on the box failed without naming
the cause.

1. **Known-good baseline — run the corpus.** `tests/al-language` is green or it is not, and its
   expected count lives in `tests/expectations/count-baseline/`, checked in and only moved by a
   PR that deliberately bumps it. That makes it the right health check and means no new baseline
   needs inventing: a box that cannot reproduce it is a box whose results cannot be trusted.

   Do **not** gate on a Microsoft bucket's pass count. There is no green there — it is a number
   that rises as the runner improves, so equality-gating on it would halt the loop on its first
   success, and recording "whatever this box last saw" would ratify drift instead of catching it.

   Run it against the **shared cache the work will actually use**, not a private one. The failure
   this catches is a cache left inconsistent by a killed run, which once cost 76% of passing
   tests with no error and an unchanged exit code — a private cache is blind to exactly that.

   If it does not reproduce: stop, notify, and open an issue. Everything downstream is untrusted
   until it does.

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

   Keep your label on whatever you are working, so a loop starting up can see the slot is live.
   Between units you hold nothing, so a concurrent startup may pick the same slot — that is what
   the re-read above is for. Do not read the assignee as the lock that makes up for it: where
   every loop pushes as one account, the assignee cannot say *which* loop holds an issue, and an
   **open PR carrying `Closes #N`** is the signal that decides it (#2891). Do not try to judge
   whether someone else's open work is "still being worked" either: you cannot tell a dead box
   from a contributor who is asleep, and the design refuses that judgement elsewhere for the
   same reason.

   Note the slot is bookkeeping, not safety. The incident it is often credited with preventing —
   `impl-69`, 82 worktrees, 10 GB — was caused by nothing ever *deleting* a worktree. Preflight's
   stale-worktree check is the actual fix for that.

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

Keep two files, because they have opposite lifetimes:

- **The measured profile is rewritten at every startup.** A box changes — disks fill, other work
  starts, an artifact set goes stale — and a stale measurement is worse than none because it
  looks authoritative.
- **The cycle log is append-only and survives restarts.** Without it, three things the design
  depends on are impossible: knowing when the baseline last passed (so it can run on an interval
  instead of every cycle), comparing an odd cycle against a known-good starting state, and
  detecting "the same failure several cycles running" — which is one of the three conditions
  that is supposed to notify a human, and is undetectable if each startup erases the evidence.

Record per cycle: what it worked, the outcome, the failure signature if any, and what it
consumed.

## Priority order

Work the first item that applies. Re-evaluate from the top after every completed unit of work —
a merge can turn `main` red, which outranks everything you were about to do.

1. **`main` is red.** Nothing else matters. Read the failing log (never re-run a failed job — it
   destroys the log), diagnose, fix.

   **Cap this.** A load-dependent flake — a red `main` whose *failing-leg set moves between
   runs* — cannot be fixed and will otherwise re-enter priority 1 every cycle until the weekend
   is gone. Apply `ci-verdicts.md`'s evidence bar before treating red as real, and after a few
   consecutive cycles on the same red, notify a human and demote it so other work continues.
   Never ship a speculative fix to get past it: unattended and self-reviewed, that is how a wrong
   change reaches `main`.
2. **One of our PRs is red, or conflicted.** Drive it green, or close it with the reason. A red
   PR left open is worse than no PR: it looks like progress.

   **Check `mergeStateStatus` for `DIRTY` on every sweep, not just the check conclusions.** A
   conflicted PR is *not* red — **CI does not run at all on a PR with conflicts**, so it keeps
   whatever green checks it last earned and reads as healthy in `gh pr list` and in any
   conclusions-based summary. Nothing about it looks wrong, and it can sit indefinitely. This
   happened on 2026-09-06: a PR sat `DIRTY` for hours behind a green-looking check set and was
   found only by a manual sweep, because the priority order below asks about *red* and a
   conflicted PR never becomes red.

   ```bash
   gh pr list --repo <owner>/<repo> --state open \
     --json number,mergeStateStatus,statusCheckRollup
   ```

   `DIRTY`/`CONFLICTING` → rebase on the base branch, resolve, force-push with
   `--force-with-lease`, re-check until it reads `BLOCKED` or `CLEAN`. **Resolve on the merits,
   not mechanically:** a conflict means someone else changed the same code, so read what landed
   and decide whether this PR's change is still the right one. If `main`'s newer version already
   does what the PR intended, closing it as superseded is the correct outcome.

   **Never force-push a branch you do not own.** Where several loops or outside contributors
   share a repository, check the *branch prefix*, not the author field — loops running under the
   same account all report that account as the author. Getting this wrong means force-pushing
   another loop's work.
3. **A PR is waiting on review.** Dispatch the `reviewer` agent. A PR authored by the account
   the loop runs as may be merged unattended **only when every one of these aligns** — any one
   missing sends it to the human queue instead:

   - the reviewer says it meets the bar, having actually reviewed this head SHA;
   - every required check is green **on the current head**, with no `CANCELLED` required context;
   - `git merge-tree` is clean against current `main`, and the affected tests were re-run if the
     branch was rebased;
   - if it asserts anything about BC's behaviour, the corpus PR proving it **has merged**, and
     its pin bump and count-baseline update are folded into this PR;
   - it is not a release window (`publish.yml` pushes a fast-forward; a merge during its run
     kills it).

   A PR from anyone else is reviewed, and its findings go to the human queue. Never merged.

   The reviewer is dispatched by the loop, so it is not independent oversight — it is a second
   pass by the same lineage. It catches carelessness, not a shared wrong assumption. That is why
   the other gates are mechanical and checkable rather than judgement calls, and why repeated
   failure in one area escalates to a person.

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

   That claim is the **implementer's** to test up front, not only the reviewer's to challenge,
   and the answer belongs in the PR body whichever way it comes out. Both have happened: a
   defect assumed precompiled-only also reproduced on a source-compiled table, and the corpus
   app declares a Base Application dependency, so a corpus test does reach the precompiled path
   after all (issue #2518, corpus PR #165, merged green on all 8 legs); while table `2000000001`
   really is out of reach, being `Scope = OnPrem` and in `SystemTables.InternalTables`, which
   `NavRecordRef.IsSystemTableAllowedForRecordRefUsage` refuses outright. Hold that second answer
   to what actually carries it: the service tier measured a **sibling id** — corpus PR #153 put
   `2000000071` in front of one, all eight legs came back red, and the PR was withdrawn —
   and `2000000001` follows by **set membership in the same refused list**, not by its own
   measurement. Make the PR body say which of the two it has (issue #2774).

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

**Between two loops on the same account, neither signal decides ownership**, and the check that
does is one call: an **open PR carrying `Closes #N`** means the issue is in progress no matter
what the assignee and labels say. Resolve it before claiming, and — as a coordinator — build the
whole map once per cycle before dispatching. Three collisions in four hours came from skipping
it. `.claude/rules/check-open-prs-before-claiming.md` has the command and the incidents.

**Look in this order, and it works for any account:**

1. **Issues assigned to your account AND carrying your own `agent: <tag>-N` label.** Both, not
   either. This is how a restarted loop resumes — a box that died mid-issue comes back, sees its
   own claim and continues instead of stranding it.

   **The assignment alone is not enough.** A maintainer assigns issues to themselves to mean "I
   am thinking about this", and `branch-and-pr.md` uses a non-self assignee to mean "a human is
   handling it" — the field is overloaded. On this repository the owner currently holds 22 open
   issues that way. A loop running as that account and resuming on assignment alone would adopt
   all of them on its first cycle. The label is what distinguishes "my loop took this" from "I
   took this".
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

**Release when you stop.** Finished without a fix, blocked, or shutting down for budget: release
the issue so it returns to the pool. An issue you cannot finish should not stay parked under your
name. Release by removing **your own** `agent:` label — on a shared account the assignee you would
remove may be another loop's lock, and a foreign `agent:` label is never yours to clear.

**Your own stale claims are yours to reclaim** — a claim of yours with no linked PR and no
activity for hours is from a run that died, and rule 1 above picks it up automatically.
**Someone else's stale claim is not yours to take**, even if it looks abandoned. You cannot tell
a dead box from a contributor who is asleep — and that refusal covers a foreign `agent:` label
too, which looks identical whether the loop that wrote it is live or gone. Surface it to the
human queue and move on.

**Namespace anything per-contributor** that lives on disk or in a branch name — worktrees,
branches, scratch directories, cache directories. Two contributors must never write to the same
path or push to the same branch. Derive the namespace from the account running the loop.

## How the loop is driven

Two shapes work, and both need a supervising timer — a `/loop` session dies with its session
exactly as a shell loop dies with its process, so something outside has to notice and restart
either way. That is not the difference between them.

**A `/loop` session** keeps one session alive and re-entering the cycle. It is the more
comfortable shape to watch and to tune. It is viable here for a specific reason: this design
keeps state in the **repository** and the **box profile**, never in agent context, so a
compaction cannot lose anything the loop depends on. In a design that carried state in context,
running for days would be reckless.

**A fresh process per cycle** — a plain `while` loop in bash or PowerShell — starts cold every
time. Deliberately not an OS scheduler as the mechanism: contributors run Windows as well as
Linux and macOS, and a shell loop behaves identically on all three, with a systemd unit or Task
Scheduler entry as optional restart-on-boot hardening.

**Bound the session's lifetime either way.** Even with compaction, a session running for days
accumulates state that is not context — tool handles, temp files, harness state. Have the loop
end itself after a set period or number of cycles and let the timer restart it. That keeps the
`/loop` experience while capping accumulation, and it is better than either pure option.

**Where compaction lands matters more than when it fires.** A compaction inside a unit of work
discards that unit's working context; one between units costs nothing, because everything
durable is already in git and the profile. So end every cycle at a durable checkpoint, and
prefer a threshold that compacts often and cheaply at those boundaries over one that compacts
rarely and expensively in the middle of something. A high threshold is not safer — it makes each
compaction larger and likelier to interrupt work.

Whichever shape is used, the properties that matter are the same: a dead cycle costs one unit of
work, context does not grow without bound, and the five-hour window boundary is harmless because
a cycle either fits or the next starts after the reset. Nothing should ever be stranded
mid-task; that is the likeliest way to lose work.

The cost of starting cold is that preflight repeats. Most of it is cheap; the known-good
baseline is not, at a couple of minutes. Record in the box profile when the baseline last
passed and re-run it on an interval rather than every cycle — often enough to catch a box that
has drifted, rarely enough that it is not most of the work.

## Pacing

The five-hour windows are real, but **exhausting every window burns the weekly limit in a
couple of days.** The budget that matters is weekly, so run continuously *below* per-window
capacity rather than sprinting and then idling.

One agent at a time is most of the pacing. Beyond that, **the loop can measure both its own
consumption and its remaining budget**, where the tooling for it is installed. Check for these
two sources during preflight and record in the box profile which ones this machine has:

- **Consumption, on any machine:** `npx ccusage@latest claude` for daily totals and
  `npx ccusage@latest blocks` for per-5-hour-block ones, read from Claude Code's own local
  JSONL. Absolute tokens and cost only — see the two misreadings below.
- **Remaining budget, where the Omarchy agents panel is installed:**
  `/usr/share/omarchy/bin/omarchy-agent-usage-claude --limits-only`, or read its state file
  `~/.local/state/omarchy/agents/usage/claude.json` directly. It carries the authoritative
  limits from Anthropic's OAuth usage endpoint — a `percent` and a `resetsAt` for the 5-hour
  and the 7-day window. `percent` is a 0..1 fraction, so `0.47` means 47%. `--limits-only`
  re-probes the limits while reusing a recent transcript scan; it does not narrow the output.

**A missing limits source leaves the loop half-sighted, not blind.** Measure absolute tokens and
cost with ccusage, ask the human for the cap percentage, and record any snapshot they give you
so a burn rate can be derived. One sample for calibration, taken from a panel screenshot:
35% → 37% of a session across 8 minutes at 9–12 concurrent agents, roughly 15 percentage points
per hour. Where the limits source *is* present, measure the rate directly rather than
extrapolating from that figure.

Two ways to misread these numbers, both of which cost real time on 2026-09-05:

- **ccusage's block `%` is not your cap.** It is measured against a *guessed* limit — the largest
  block ccusage has ever seen. A coordinator read it as "at the cap" and concluded there was no
  headroom while the authoritative figure was 37%. Take absolute tokens and cost from ccusage;
  take the percentage from the limits source, or from the human.
- **ccusage counts the whole machine.** It reads all local Claude Code data, so its totals cover
  every loop running on the box, not only yours.

**Cost is almost entirely cache reads, so the lever is round trips, not agent count.** Of
738,650,170 tokens in one measured day, 723,088,774 (97.9%) were cache reads and 8,761 were
input. Cache reads scale with tool round-trips × context size, so fewer round trips per agent
and tighter briefs cut cost far more than running fewer agents does — the same finding
`CLAUDE.md`'s code-navigation section reaches about grep round-trips, and it applies here too.

Put concrete values in the box profile and obey them: a fixed wall-clock gap between cycles, and
a hard cap on cycles per rolling 24 hours. Count cycles, subagents spawned and elapsed time —
all observable. Tuning those numbers is a human's job between runs, not the loop's during one.

Never leave an agent mid-task when a budget boundary is near. Have it commit and push what it
has, even incomplete, and report where it got to. A work-in-progress commit on a pushed branch
survives; an uncommitted worktree does not.

## Posting, and leaving an audit trail

Within **this repository and the corpus repository**, the loop acts without asking: filing
issues, commenting on issues and PRs, closing issues, applying labels. Waiting for approval on
each of those would stall an unattended loop for no benefit, and a comment on the issue is a
better home for a decision than an agent's memory, which is discarded.

**Every state change carries its reasoning.** Not as a permission step — as the thing that makes
the loop auditable. Closing an issue, claiming or reclaiming one, marking it needs-input,
declaring a surface out of scope, filing from a measured cluster: each gets a short comment
saying why, with the evidence. Someone reading the repository months later should be able to
question any decision from the repository alone, without a transcript that no longer exists.

**Say who is writing — on everything, not just comments.** Issues, comments, PR bodies and PR
reviews all post under the account the loop runs as, so a reader cannot otherwise tell an agent
from a person. On a public repository with outside contributors that is actively misleading:
someone who asks the maintainer for a decision and receives an agent's answer in the
maintainer's name has been misled, even with good intent.

Every agent-authored issue, comment and PR body carries a footer saying it was written by an
agent acting on the account holder's behalf, naming the agent tag and cycle. Where the thread is
asking the account holder for a judgement, say explicitly that the decision remains theirs.

This is not optional and it is easy to forget: in the session this skill was written from, 14
issues and a comment were filed under the owner's name with no such marker, including a reply in
a thread where a contributor had specifically asked the owner to decide.

**Comment on state changes and findings, never on progress.** A loop that narrates itself
drowns the signal it exists to produce. No "starting work on this". Never repeat a conclusion
already on the thread — if nothing changed, there is nothing to say.

**Outside these two repositories, nothing is automatic.** Another repository, email, anywhere
else: that needs a human, whatever the loop concludes.

## What still needs a person

Some things are not the loop's to decide. Label them, comment with the question and what you
would need to answer it, and move on — do not block, and do not guess:

- A genuine product decision. Whether `--test-data` should present a company configured
  differently from the backup it loads is a judgement call about what the runner is *for*, not a
  defect.
- Merging a PR authored by anyone other than the account the loop runs as.
- A refusal that might be a correct scope boundary rather than a gap. Getting that wrong in
  either direction is expensive: implementing something that should throw, or throwing on
  something that should work.
- Anything where the loop has failed the same way several cycles running. That is not a hard
  problem, it is a wrong assumption, and it needs a person to see it.

## The status page

Publish one public artifact and republish it at the end of every cycle. It answers two
questions at a glance, from any device, without being signed in as the account running the loop
— which matters, because a person watching an unattended box usually has a different account
open on their phone.

**Is it alive, and does it need me?**

The page carries, in roughly this order of prominence:

- **When it last updated.** The most important line on the page.
- What it is working on now, and what it finished in the last few cycles.
- What is queued for a human decision, with links.
- The last known-good baseline result and when it ran.
- A short box summary from the profile — memory, disk, the slot in use.

**The timestamp is a dead-man's switch, and that is the point.** Notifications only cover
failures the loop can recognise and report. A crashed process, a power cut, a hung agent, a
wedged network — none of those can send anything. A timestamp that has stopped advancing is the
only signal that catches them, and it needs no infrastructure to work. Treat the two as
complementary: notifications for known problems, a stale page for everything else.

Two practical points:

- **Republish to the same artifact every cycle**, so the link never changes. Record its URL in
  the box profile; publishing a fresh one each cycle gives a link nobody can bookmark and
  destroys the timestamp's meaning.
- **It is public, so put nothing on it that should not be.** No tokens, no notification
  endpoints, no absolute paths from the machine, no personal detail. Counts, states, timestamps
  and links to public issues only.

## Telling a person something is wrong

Labels and issues are passive — someone has to go and look. An unattended loop that stops at
03:00 tells nobody, and the machine sits idle until it is noticed. So the loop pushes a
notification for the few things that genuinely need a person, and for nothing else.

Notify on:

- **It stopped.** Preflight failed, the baseline did not match, or it cannot complete any unit
  of work at all. The machine is now idle and will stay idle.
- **It is repeating itself.** The same failure several cycles running is a wrong assumption
  rather than a hard problem, and only a person will see that.
- **A decision has gone unanswered.** The human queue is untouched after several cycles and work
  is piling up behind it.

Never notify on ordinary progress. A channel that pings for every merged PR gets muted, and then
it does not work when it matters — the same reasoning as commenting only on state changes.

The transport is the contributor's own choice — a push service such as ntfy.sh, an email, a
webhook. **Its configuration belongs in the gitignored box profile, never in the repository**: a
notification endpoint is personal, and committing one exposes it to everyone who can read the
repo. The skill specifies what is worth interrupting someone for; the contributor decides how it
reaches them.

Expect to also want a way in — SSH or equivalent — to inspect a box that has stopped. The
notification says something is wrong; reading the box profile and the last cycle's report says
what.

## Blast radius

Hard limits, whatever the loop concludes:

- Never push to `main`; always a PR.
- **Never merge while a release run is in progress.** `publish.yml` pushes the release as a
  fast-forward, so any merge during its ~40-minute run kills it.
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
