# Incidents behind .claude/rules/ci-verdicts.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## 0. Never block on CI — read the verdict, and read it again later if it is not in yet

This file said the opposite until #3351 — *"it must be `--timeout 1`, never `--timeout 0`"* —
and the reasoning was correct about the code at the time. The poll was `deadline = time.time()
+ args.timeout` followed by `while time.time() < deadline:`, so a zero timeout never entered
the loop, fell through to a bare `return 2`, and made exits 0, 1 and 4 unreachable without
looking at a single check. Its only tell was empty parentheses — `STILL RUNNING after 0s ()` —
where the progress detail belongs. `--timeout 1` was standing in for a single-pass mode by
accident of timing: one second happens to be long enough for one poll.

The loop is now a do-while, so the body always runs at least once, and the still-running line
can no longer be emitted with an empty reason. **`--timeout 1` still works** and still means
"wait up to a second"; it is simply no longer the way to express "look once".

Everything else in this file is about *how* to read a verdict, and none of it changes: a
verdict belongs to one commit, a cancelled run is not a failure, and a failed job's log must
never be re-run away. What changed is only *when* you read — on your next pass over the PR,
rather than by keeping a turn open until the answer arrives.

The anti-poll argument this section used to make still holds *within* one read: never
hand-roll `gh run view` plus `sleep`. Measured across one session's 17 subagents, CI waiting
was 328 of 3,282 Bash calls, shaped as 107 `gh run view` polls and 37 `sleep` loops against
only 29 proper waits. One `--timeout 0` call replaces all of it.

**A green names how many required contexts it accounted for**, and that number is worth
reading — against the live ruleset, not against a number written here. Every false green in
#3002 named **one** check, while the real greens that night named nine or ten; that asymmetry
is the only reason a human caught three wrong verdicts, before the tool was taught to refuse
them. The `N/N ruleset context(s)` figure moves whenever the ruleset does (#3165 raised it),
so a green that accounts for fewer contexts than the ruleset requires is the signal, not any
particular value of N.

### Run it from a tree you have fetched — the tool being right says nothing about your copy

You invoke `tools/ci-wait.py` by relative path, so you run the copy in **your worktree**, and a
worktree is created once at the start of a task and never fast-forwarded again. Measured over
this box's 109 worktrees on 2026-09-06, **71 of the 99 copies of `ci-wait.py` were not
`origin/main`'s**, across four distinct versions, and replaying the recorded PR #2971 rollup
through them, **59 printed a false GREEN** that had already been fixed on `main`. The same
count for `pr-body.py` and `preflight.py` was 40 of 59. A tool nobody has changed lately —
`context-pack.py` — was identical in all 105, which is the point: it is the tools **under
active repair** that run old, so the copy most likely to carry a fixed bug is the one most
likely to be on disk.

**Expect it to fire in bursts, and do not read a burst as an outage.** A worktree only goes
stale when `ci-wait.py` (or `pr-body.py`) next changes on `main`, so nothing refuses for days
and then every worktree older than that merge refuses at once. That file changed five times in
four days. A wave of exit-3 refusals right after a merge is the guard working exactly as
designed; the fix is `git fetch origin main` in each worktree, not reverting anything.

  Expect two loud `unknown` notes from the copy above — a directory under `/tmp` is not inside
  a git repository, so neither file can check its own freshness. That is fine *here* and only
  here: you just extracted both from `origin/main` yourself, so their provenance is the
  guarantee the check would otherwise provide. It is not fine in general, and the same
  fails-open-on-`unknown` path was reachable by other routes in `pr-body.py` and
  `preflight.py` too; #3296 fixed that.

### A cancelled run's leftovers sit in the same rollup as the live run, and `gh pr checks` hides which is which

This is the trap behind #3002, and it bites in **both** directions. The API attaches every
check run for a commit to that commit, including check runs from workflow runs that were
cancelled and superseded — #3003 measured **35 of the last 40 `Test Matrix` runs on `main`
cancelled as superseded**, so this is the common case here, not an edge case.

Two things make it hard to see:

- **A cancelled run does not necessarily report `cancelled`.** Recorded on PR #3010's head
  `95c16b20`: `Test Matrix` run `34002828792` has run-level `conclusion: cancelled`, and its
  aggregate job **`BC test matrix passed` concluded `failure`** — the aggregate runs
  `if: always()` over `needs` that were killed. The live run `34004261321` was green
  throughout. Read literally, that leftover is a failing *required* context.
- **`gh pr checks` shows one row per context name and drops the rest.** Measured on PR #3016's
  head `d56466a7`: the API reports **8 `cancelled` check runs**, and `gh pr checks` prints 25
  rows, every one of them `pass` — not one cancellation appears. So it cannot corroborate a
  supersession question in either direction; it never shows you *which run* produced the row,
  and when the newest entry for a name is a leftover from a cancelled run, that leftover is the
  entire output and is indistinguishable from a live failure.

## 2. A verdict is about one commit, not one PR

exists because it used to be invisible: all twelve `pr-check.yml` jobs reported without
gating, and #3116, #3112 and #3095 each merged with one of them in a `FAILURE` state (#3165).
So a red tick on `PR body closing references must be correct, both directions` now stops the
merge, and a red tick on `Required-context list must match the live branch ruleset` does not
— that one talks to `api.github.com`, and a required check that can go red because a
third-party API was unreachable blocks every merge in the repository for something no author
can fix.

### In the corpus, "which harness code ran" has two dials, and the obvious one is wrong

Recorded because it was published as evidence and was not: diagnosing
`MsDyn365Bc.On.Linux#61`, two corpus runs were reported as having "resolved `9dc3244` and
`ccb401a`" and therefore predating the retry gate. The conclusion was right and the evidence
named the wrong dial — the gate lives in `scripts/run-tests-altool.py`, so what decided it was
when each job's checkout ran. It agreed only because the queue was short that day. Getting this
backwards argues for reverting a fix that was never in the run.

### An empty log fetch is a refusal, not an empty log

Measured 2026-09-07 against `gh` 2.98.0, on two jobs of the same repository:

```
job 101602519097 (BC 28.4, failure)   --log-failed 875921 B   api 0 B   api+flag 709372 B
job 101605637617 (Smoke, cancelled)   --log-failed      0 B   api 0 B   api+flag  26388 B
```

The cost of not knowing this was a wrong diagnosis, not a lost minute: diagnosing PR #3305's
red 28.4 leg, an empty `--log-failed` was read as "the log is not available yet" and the PR was
handed back with a guessed hypothesis instead of the failing test name, which was in the log
the whole time.

### "Cancelled" does not mean "no log to lose"

This rule used to exempt cancelled runs outright, on the reasoning that a cancelled run never
produced a failure log. It can have produced one: a check run concludes `failure` **on its
merits** and its parent workflow run is cancelled only afterwards, so the log is real and
`gh run rerun` overwrites it just the same. Measured on head `c6377b30` (#3142), where
`preflight.py unit tests` carried three check runs for the one name — a `failure`, a
`cancelled` from run 34037049850, and a newest `failure` from run 34037050361 whose own
run-level conclusion was `failure`.

## 5. "Pre-existing unrelated flake" needs evidence

That is how impl-4 got a second, independent verdict on corpus PR #144's BC 28.4 leg without
disturbing the original: run 33962643816, dispatched against the same SHA that had just
failed, came back 2495/2495 clean.

**The corpus has no equivalent guarantee** — there the eight `BC <ver> / test` legs ARE the
required contexts, so a dispatched leg reports a check run with the same name as the one that
gated. Measured once, on corpus PR #144: the dispatch produced a second `BC 28.4 / test` check
run with conclusion `success` on the same head SHA as the original `failure`, and the PR stayed
`BLOCKED` with `gh pr checks --required` still reporting the failing one. So it did not clear
the gate — but that is one observation of undocumented GitHub behaviour, not a rule. Never
dispatch a corpus leg expecting it to turn a PR green, and check `gh pr checks --required`
rather than assuming either way.

Re-rolling CI *hoping* the same failure won't recur, as a way to get past a red required check,
is separately and explicitly not sanctioned, by any mechanism — tried for real with a genuine
content-changing push on corpus PR #145, it moved the failing leg from BC 28.2 to BC 28.0
without fixing anything, at roughly 8 minutes of the whole account's shared Actions queue per
attempt. GitHub Actions concurrency is scoped per account, not per repository, so that cost is
shared with every other repo and agent using the same account, not just this one. Nobody
bypasses a red required check. The recipes above are for finding out whether a failure is
real, not for making it go away.
