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

## 5b. The inherited red: a rebase, not a re-run (2026-09-13, #4092)

Two corpus/runner pairs merged within an hour, each correctly ordered:

| corpus PR | added | runner fix | merged |
|---|---|---|---|
| #336 | `TestPrecompiledReportLayouts.al`, codeunit 60974 | #4035 (`4d59608e`) | 01:21:23Z |
| #337 | `TestReportNestedLinkImplicitParent.al`, codeunit 60224 | #4036 (`8908e999`) | 02:07:45Z |

Corpus #336 merged at 01:21:19Z and its runner fix at 01:21:23Z — **four seconds apart**, which
is the pair landing in one step as `bc-behavior-tests-go-upstream.md` step 5 requires. Thirteen
open pull requests went red anyway, created between 20:59Z and 00:38Z, i.e. all before the pair.
So the exposure is not the gap between the two merges (what #3922 measured) but the in-flight
population at the moment the pair lands. A zero-length window bounds nothing.

**What made the diagnosis tractable** was reading the failing codeunits off the leg log rather
than sizing the failure by leg count. Twelve PRs failed on 60974 across three legs; one (#4056)
failed on 60224 across two. Those counts are indistinguishable from an ordinary red, and the two
groups needed different fix commits. Corpus #336's own body predicted the shape exactly —
*"Before its fix it failed 4 of these 5 tests"* — and the legs reported exactly four.

**The patch-id measurement.** Thirteen PRs were rebased and checked with `git patch-id --stable`
over `git diff origin/main...HEAD`, before and after:

- twelve returned an identical id;
- **#4056 returned a different one** (`33e86914` → `d92dc3c5`), with the same 9 files and the
  same 304 insertions / 10 deletions. Diffing the two diffs' `^[+-]` lines, excluding the
  `+++`/`---` headers, gave **zero differing lines**. Only hunk *context* had moved, because
  `main` advanced underneath — and `patch-id` hashes context.

So identity is conclusive in the safe direction and a difference is not conclusive in the unsafe
one. The rule records the resolution (diff the diffs) rather than this derivation.

**Two PRs in the same sweep were red for their own reasons** and had to be kept out of the
rebase batch: #4053 (four undeclared `[install-trigger]` sites, `Failed: 1, Passed: 5641`) and
#4078 (`IsFixtureItem` had no arm for a walk whose item type the PR itself changed,
`Failed: 2, Passed: 5692`). Both were found by the same per-codeunit / per-step read that
identified the inherited ones; a bulk "rebase everything red" would have shipped both defects.

## The same-tree recipe printed "same tree" for a commit git had never seen (2026-09-13, PR #4140)

§5's "same code means the same tree" check shipped as:

```bash
[ "$(git rev-parse <sha1>^{tree})" = "$(git rev-parse <sha2>^{tree})" ] && echo "same tree"
```

`git rev-parse` on a SHA the clone does not have **echoes the argument back on stdout** and exits
128. The `[ ]` therefore compares two echoed strings, and the `fatal:` lands on stderr where a
`$( )` capture never sees it. Both directions reproduce:

- **false positive** — the same unknown SHA twice prints `same tree`, for a commit git has never
  heard of;
- **false negative** — a shallow clone with two byte-identical trees prints nothing when one SHA
  is unfetched. That is this section's *actual* use case: judging a flake across two CI run SHAs,
  routinely on branches nobody fetched.

`git rev-parse --verify -q` collapses both: it prints nothing and fails rather than echoing.

Found by a reviewer on PR #4140, which is the PR that *added a guard for exactly this class* —
the recipe had been declared "unpinnable, the comparison itself is `git rev-parse` equality with
nothing to get subtly wrong", and the act of writing that declaration is what put a reviewer in
front of it. The declaration was wrong and the mechanism surfaced it on its first use.

Same shape as the three-dot `origin/main...HEAD` recipe that founded #3955: valid bash, exit 0,
a plausible answer to a different question.

## A paged count reported as a queue depth (2026-09-13, #4110)

Through a six-hour Actions stall I reported the backlog three times from

```bash
gh api ".../actions/runs?per_page=100" --jq '[.workflow_runs[]|.status]|group_by(.)|...'
```

giving 74, 75, 81, 90. Every one was the composition of **page one**, capped at 100 by the query.
The real figure, from the same response:

```
per_page=1 &status=queued  -> total_count: 438
per_page=100&status=queued -> [.workflow_runs[]]|length: 100     <- pinned at the page size
```

Two consequences, and the second is the expensive one:

- the depth was understated roughly five-fold;
- the number **moved between reads** — 76 → 40 at one point — as the status mix on page one turned
  over. I published that as "the queue is draining", which it was not.

The tell was present in every reading and I did not look at it: **a count that brushes its own
page size**. 90 out of a 100-row page is a paging artefact until `total_count` says otherwise.

The SHA-filtered recipes in this rule are not exposed to it — one commit never accumulates 100
runs — and that exemption was verified rather than assumed: `head_sha=<main>` returns
`paged=25 total=25`.

Same class as the `[36;1m` source echo recorded above: a correct instrument answering a question
nobody asked, returning a plausible number that nothing downstream contradicts.

## The floor debounce as an unbounded green streak (2026-09-14, #4178)

Checking whether `main` had recovered, a workflow listing filtered to `main-verdict-floor.yml`
showed **eight consecutive `success` runs over seven hours**, all on `917bbbf2`. Every one had
`floor-matrix: skipped`; the conclusive run for that SHA was the `failure` beneath them, red on
the nine codeunits tracked by #4167.

The debounce is correct — it keys on the SHA already having a conclusive run and declines to
re-measure. What makes it misleading is the interaction with a red `main` nobody has fixed: that
SHA stays put, so the skips never stop and the green streak grows without bound.

The rule's original example was one skip after one failure, which reads as a transient. The
shape that actually occurs is a growing run of greens, and a streak is far likelier to be read
as recovery than a single green is. Hence the addition: the LENGTH of the green run is not
evidence; the run that measured the SHA is.

Found while verifying, for the fourth time in one session, that main's red was still the same
nine and not something new.

## The `run:` block is echoed as source, and a grep counts it (2026-09-13)

Three instances in one session, on three unrelated questions, each producing a plausible count
that meant the opposite of what it looked like.

**1. A retry loop that never retried** (#3231 / PR #4120). `grep -c "attempt .* of 5"` on the
failing job returned **1**, read as "one iteration executed". The step had run 0.6 seconds and
executed **zero** iterations; the single hit was the loop body echoed as source. The real defect
was larger than filed — the loop aborted before its first iteration, so the retry mechanism had
never functioned at all.

**2. A `catch` that never fired** (PR #4109). The nightly's job concludes `success` over five
real BC failures, and the rule first attributed that to a `catch` downgrading the throw to a
`::warning::`. `grep -c "Run-TestsInBcContainer for"` returned **1**. Filtered, it returns
**0**: the hit was the `Write-Host "::warning::..."` line inside the echoed script. The actual
mechanism is `-returnTrueIfAllPassed | Out-Null` — a returned `$false` nobody reads.

**3. A guard credited with running.** Same shape, on a `verdict-needed` block.

Measured on both surviving logs:

```
                              raw grep   filtered
retry loop, "attempt N of 5"      1          0
nightly,  "Run-TestsInBc..."      1          0
```

**What makes it dangerous is that 1 is a believable answer.** A zero invites a second look; a
one reads as confirmation. And the escape is invisible unless you fetch with
`--allow-escape-sequences`, which is separately mandatory for these logs — so the same flag that
makes the log readable is what makes the trap visible.

The filter (`grep -v $'\x1b\[36;1m'`) was executed against both logs above before being written
into the rule, per #3955.

## Reading a corpus PR's leg set: `event` before leg count (#3389)

Two independent review agents, working different PR sets in the same hour, each reached a
**wrong verdict about which BC legs ran** on a corpus PR. Opposite conclusions, one root cause:
the corpus repository routinely carries several workflow runs per head SHA, and every obvious
way to read a verdict picks one silently.

### Mechanism 1 -- a `workflow_dispatch` run builds a ONE-LEG matrix

Corpus `ci.yml`'s `prepare` job branches on `github.event.inputs.bc_version`: with an override
it emits a single-entry matrix, otherwise the full eight-version one. So the legitimate
single-leg dispatch recipe in `ci-verdicts.md` section 5 produces a run where seven of the
eight required cloud legs **do not exist** -- which reads exactly like a matrix that
fail-fasted. Both matrices are declared `fail-fast: false`, so that reading is never right.

Re-measured against the live API while writing the fix, corpus PR #254, head
`84a63b262820eb645b39262f7d5ca10d71420bc8`:

| run | event | cloud legs |
|---|---|---|
| 34118581202 | `workflow_dispatch` | 1 |
| 34117863109 | `pull_request` | 8 |

A reviewer read the dispatch run, concluded "the matrix fail-fasted, seven legs never ran",
and escalated it to a repo-wide blocker degrading every corpus PR's evidence from eight
versions to one. No such blocker existed.

### Mechanism 2 -- the gating run can be the OLDEST of several

Corpus PR #257, head `e3d632ce`, carried three runs; re-measured:

```
34132233466 workflow_dispatch 14:18:26Z
34121459612 workflow_dispatch 12:21:49Z
34118430645 pull_request      11:47:29Z   <- the gating run
```

Dispatches follow the `pull_request` run, so recency systematically points at the wrong one.
A second reviewer reported `gh pr checks` surfacing the oldest run on #258, #257 and #254; on
#257 that was the difference between reading 8 failing legs and reading 3.

### What the fix rests on that the issue did not have

`gh pr checks --json` **does** expose `event` -- measured on #254, all eight legs report
`event=pull_request`. What it does not do is show it by default: the four columns printed are
name, state, duration and link. So the discriminator was available all along and nothing told
a reader the question existed, which makes the remedy one flag rather than a separate API call.

### Why the guard is shaped the way it is

`tools/test_corpus_run_event_discriminator.py` was written first and failed correctly (exit 3,
UNMEASURABLE -- the section did not exist yet). Three drafts of it were then defeated by its
own mutations, each reproducing a documented way a rule-text guard passes while the claim is
gone:

| the check as first written | what defeated it |
|---|---|
| the relation asserted per LINE | the shipped claim hard-wraps across two lines, so it pinned the author's line breaks |
| "a sentence mentioning newest/oldest near a run word" | a reword keeping the vocabulary and dropping the denial -- a sibling sentence satisfying the check |
| "a line with `pull_request` that also contains `select`" | a recipe inverted to `select(.event == "workflow_dispatch")` -- the verdict column pinned, the action column never read |

The third is the sharp one: the guard passed a recipe prescribing **exactly the wrong run**,
which is worse than prescribing none. The fix reads the selector's argument rather than its
neighbourhood.

**A fourth draft was defeated in review (#4483), and it is the subtlest**, because it survives
the first three. Both prose checks required a NEGATION somewhere in the sentence, **unbound to
the proposition it negates**. So `is never right` and `is not wrong` are the same to the regex,
as are `never the newest` and `never the oldest` -- and prose asserting the opposite passed,
with the guard reporting `ok rules out recency` on text telling the reader to take the newest
run and ignore `event`. That is item 3 above one level up: the recipe check had already learned
to read the selector's ARGUMENT rather than its presence, and the lesson had not reached the
prose checks, where the "argument" is which proposition the negation attaches to.

Why the author's own controls could not find it: C1 and C2 reword while **preserving** meaning,
which is the correct control for over-fitting and is exactly why they stayed green. The missing
control is the inverse -- a reword that keeps the regex vocabulary and **inverts** the claim,
which must RED.

The repair asserts the POSITIVE direction as a relation, so an inversion has to break it: read
which run the text tells you to TAKE (an imperative, with a negation before the object flipping
it to a rejection), and assert the configuration fact `fail-fast: false` on both matrices, which
is what makes the collapse reading impossible. Repairing it over-fitted once in passing -- both
meaning-preserving controls went red, one because the check demanded the word `gating` where the
control wrote `gates`, the other because it only looked for the rejection AFTER the topic word
-- so the final form reads a window either side and accepts the verb forms.

Final matrix: eight mutations each caught (two of them the review's inversions, one a third
inversion written independently to check the fix generalised rather than fitting the two
supplied), two meaning-preserving rewords green, clean tree green.

### The scope error, and why the gate could not see it

The PR originally declared `Closes #3389`. #3389 is a **nine-mechanism living reference record**,
four of them added by the repo owner after filing, and its triage comment asks for "a separate,
closeable docs issue that links here" rather than for #3389 to be closed. The PR's own quotation
from the issue body was accurate but predated both the added mechanisms and the triage call.

`reject-deferred-scope` passed, because it keys on a deferral's **destination** and the PR routed
its deferred work at `#3389` itself -- the declared closing target. That is the one shape the gate
cannot see, which is why a green tick is not a second opinion on scope.

Settled by `Part of #3389` plus a new docs issue (#4484) scoped to what the PR documents. One of
#3389's own mechanisms -- an **empty** `head_sha` returning the repository's whole run history
rather than an empty list -- was live in the shipped recipe, which interpolated `$head`
unvalidated while warning only about the abbreviated case; measured 2026-09-23, `head_sha=`
answers `total_count: 1158` against `0` for `head_sha=84a63b26`.

## Figures moved out of the rule (#4539)

The rule now cites these measurements rather than restating them; the figures are what each
citation measured, frozen to that moment. Population figures (counts of labels, files,
transcripts on one box) were deleted outright rather than moved, because they go stale here too.

- **#4288, the 600s cap.** Every one of the 52 foreground calls declaring a timeout above 600s,
  spanning 660000 to 3600000 ms, reported `within its 600s timeout`.
- **#3341.** The `rc=0` read under `| tail` sat in the issue body for three days before correction.
- **`c028bf3f`.** Run `34656743829` FAILED across five legs at 23:06; run `34658754607` reported
  `success` at 23:37 with `floor-matrix: skipped`.
- **`917bbbf2`.** Eight consecutive `success` floor runs over seven hours, every one skipped.
- **#4111.** At filing, `ci-wait.py` printed `GREEN on d50d41fd` beside a true distance of 8 commits.
- **#4203 / corpus #371.** The gate concluded `failure` at 20:04:28Z, corpus #371 merged at
  20:21:08Z, and a body edit at 20:50:05Z produced a fresh `success`: 46 minutes stale.
- **#3942.** The blocked PR showed 13/13 checks green and `mergeable: MERGEABLE`.
- **#4110.** A paged read reported about 81 queued runs across three comments while `total_count`
  said 433; a "76 -> 40, it is draining" was page turnover.
- **#3922.** Measured four times; one window held `main` red for 9h45m and blocked five PRs. The
  inherited total fell from 17 to 13 in twenty minutes when one of four foreign pairs merged.
- **#4092.** Twelve open PRs went red on codeunit 60974. Across thirteen rebases, twelve patch-ids
  were identical and one changed with zero differing added/removed lines; of fifteen red PRs, two
  were red on their own (`Failed: 1, Passed: 5641` and `Failed: 2, Passed: 5692`).
