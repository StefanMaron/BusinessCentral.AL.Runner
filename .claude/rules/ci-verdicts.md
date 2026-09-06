# Driving a PR through CI

"PR opened" is not the deliverable; "PR merged" is. Fix CI failures and address review
comments yourself — don't wait for someone else to notice a PR is red. Each step below
records a mistake made here more than once.

## 0. Wait on CI with one call, not a poll loop

**`tools/ci-wait.py <PR>`** keeps the polling but moves it inside a single tool call: it
loops internally, prints nothing until it has an answer, and returns one verdict.

```bash
tools/ci-wait.py 2379                 # blocks, then reports once
tools/ci-wait.py 2379 --timeout 2400
```

| exit | meaning |
|---|---|
| 0 | every required check passed **on the current head** — safe to report green |
| 1 | a required check failed; **the failing log is already printed**. The list is what has reported SO FAR — while other required checks are still running it can still grow, and the verdict says how many have not reported. Do not scope a diagnosis to those names until every check has reported (a one-leg failure that turned out to be eight cost a version-specific diagnosis that was never relevant). |
| 2 | timed out while still running — **not a verdict**, call again |
| 3 | could not determine (auth, network, no checks) — **or** the running copy of `ci-wait.py` is itself behind `origin/main` (#3020) — **or** the required-context set could not be established without narrowing it (#3002). A ruleset read that comes back missing a context the tool already knows about is refused, because judging on the smaller set makes "every required check passed" vacuously true. |
| 4 | **blocked, not failing** — every check passed but the merge is still refused and nothing else says why. Two causes: a *required* context is `cancelled` on this commit (#2726) — the one case where `gh run rerun` can be correct, but only after checking that no check run on this commit concluded `failure` before the cancellation (see section 3); or a *required* context produced **no check run at all** and every workflow run for the commit has finished (#2807), which is a trigger/`paths:` filter question, not a re-run. Never reach for `--admin` for either. |

`ci-wait.py` reads the required contexts from the **live branch ruleset** on each
invocation (`GET /repos/{owner}/{repo}/rules/branches/main`, which reports only *active*
rulesets), falling back to its built-in list and saying so loudly if that call fails. A
required context added in the GitHub UI is therefore waited for immediately rather than
ignored until someone edits the tool (#2785); `check_required_contexts.py` fails CI when the
built-in lists and the live ruleset drift apart in either direction. What it will **not** do is
accept a ruleset answer that is *narrower* than its built-in list — that returns exit 3 rather
than a verdict, because a partial read and a deliberate removal are indistinguishable from here
and the smaller set is the one that produces a false green (#3002).

**A green names how many required contexts it accounted for**, and that number is worth
reading — against the live ruleset, not against a number written here. Every false green in
#3002 named **one** check, while the real greens that night named nine or ten; that asymmetry
is the only reason a human caught three wrong verdicts, before the tool was taught to refuse
them. The `N/N ruleset context(s)` figure moves whenever the ruleset does (#3165 raised it),
so a green that accounts for fewer contexts than the ruleset requires is the signal, not any
particular value of N.

Where several runs of one workflow exist on one commit — normal for `require-tests.yml`, which
has no `concurrency` block and triggers on `labeled`/`unlabeled` — the verdict comes from the
**newest workflow run**, not the highest check-run id. A check run's id is allocated when its
*job starts*, so id order inverts as soon as two runs overlap, and the older run's conclusion
would otherwise be reported as the verdict (#2748).

It enforces the two rules agents keep getting wrong: checks are matched against the PR's
**current head SHA**, so a newer completed run for an older push is never reported as this
push's result; and on failure it fetches `--log-failed` for you, so there is never a reason
to reach for `gh run rerun`, which destroys the log permanently.

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

`ci-wait.py` and `pr-body.py` now refuse rather than answer when `origin/main` has moved their
own file since your checkout branched (`tools/agent_self_freshness.py`) — exit 3 for
`ci-wait.py`, refuse-to-write for `pr-body.py`. A branch that legitimately *edits* one of them
is not stale and is not refused: what makes a copy stale is `origin/main` moving the file since
the branch point, not your working file differing from `origin/main`'s. There is no flag to
switch the check off.

**Expect it to fire in bursts, and do not read a burst as an outage.** A worktree only goes
stale when `ci-wait.py` (or `pr-body.py`) next changes on `main`, so nothing refuses for days
and then every worktree older than that merge refuses at once. That file changed five times in
four days. A wave of exit-3 refusals right after a merge is the guard working exactly as
designed; the fix is `git fetch origin main` in each worktree, not reverting anything.

Two things it cannot do, so do them yourself:

- **It cannot help a copy older than itself.** The stale copies already on disk have no guard
  in them. `git fetch origin main` in your worktree before you trust any tool in `tools/`, or
  run `origin/main`'s copy directly:
  ```bash
  git fetch origin main
  git show origin/main:tools/ci-wait.py > /tmp/ci-wait.py && python3 /tmp/ci-wait.py <PR>
  ```
- **It cannot turn a network failure into a verdict.** `refs/remotes/origin/main` is shared by
  every worktree of the repository, so the check itself costs no network; one `git ls-remote`
  confirms that shared ref against the remote. If the remote is unreachable, that is a loud
  note and the local check stands — never a refusal, because a network blip is not evidence of
  a stale checkout.

`tools/preflight.py` carries the same exposure and is **not** guarded yet (#3164).

Measured across one session's 17 subagents, CI waiting was 328 of 3,282 Bash calls, and the
shape was wrong — 107 `gh run view` polls and 37 `sleep` loops against only 29 blocking
`gh run watch` calls. Each poll re-sends the whole conversation; this turns ten-to-forty
round trips into one.

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

**The reliable check is the run, not the rollup:**

```bash
gh run view <run-id> --json headSha,conclusion,status
# and, for the whole commit:
gh api "repos/StefanMaron/BusinessCentral.AL.Runner/actions/runs?head_sha=<FULL-SHA>&per_page=100" \
  --jq '.workflow_runs[] | "\(.id) \(.name) \(.status) \(.conclusion)"'
```

A `cancelled` conclusion — on an older `headSha`, or on a run of the same workflow that a newer
run has replaced — is **not this push's verdict**, whatever its individual jobs say. Two runs of
the same workflow on one SHA is normal here, not exotic: `require-tests.yml` deliberately
carries no `concurrency` block and triggers on `labeled`/`unlabeled` (#2726).

`head_sha` needs the **full** 40-character SHA. An abbreviated one returns `"workflow_runs": []`
— which reads exactly like "no runs for this commit" and is a false negative, not an error.

`tools/ci-wait.py` applies all of this itself since #3002: a conclusion from a cancelled run is
reclassified as a cancellation (so it can block, but can never be reported as a failure or
counted toward a green), and a name whose newest check run belongs to a run being replaced by
one still in flight gets no verdict in either direction. You still need the recipe above when
reading a run by hand.

## 1. Check for merge conflicts first

```bash
gh pr view <N> --json mergeStateStatus --repo <owner>/<repo>
```

`DIRTY` / `CONFLICTING` → rebase on the base branch, resolve, force-push with
`--force-with-lease`, re-check until it reads `BLOCKED` or `CLEAN`.

**CI will not run on a PR with conflicts.** Check this before investigating any CI problem —
"no checks reported" almost always means merge conflicts, not a CI outage.

`mergeStateStatus: CLEAN` only checks *textual* conflicts. It says nothing about whether CI
ran on your current head, and nothing about semantic conflicts — a clean merge can still
break because `main` moved underneath you.

## 2. A verdict is about one commit, not one PR

`gh pr checks` reports the newest *completed* run, which can predate your last push;
reporting green from a stale run has happened at least four times. Confirm the check's commit
SHA matches local `HEAD` — a mismatch means "not yet reported," not "green." Never report a
PR as done while its CI is still running.

**Which contexts gate.** Two come from the big workflows: **`BC test matrix passed`**
(`.github/workflows/test-matrix.yml`) and **`Tests updated`**
(`.github/workflows/require-tests.yml` — not `pr-check.yml`, which is where it used to live
before #2726). The matrix legs report as `bc-tests / BC <ver> (required)` — the `(required)`
there is part of the job's own name and does NOT make the leg a required context; only the
aggregate gates. That is why a single-leg diagnostic run cannot clear the gate, and why a red
leg still blocks through the aggregate.

The rest come from **`.github/workflows/pr-gate.yml`**, one context per job. Everything in
that file gates; everything in `pr-check.yml` is advisory and cannot block a merge. The split
exists because it used to be invisible: all twelve `pr-check.yml` jobs reported without
gating, and #3116, #3112 and #3095 each merged with one of them in a `FAILURE` state (#3165).
So a red tick on `PR body closing references must be correct, both directions` now stops the
merge, and a red tick on `Required-context list must match the live branch ruleset` does not
— that one talks to `api.github.com`, and a required check that can go red because a
third-party API was unreachable blocks every merge in the repository for something no author
can fix.

Do not read the exact list out of this file. Ask, and let the answer be the measurement:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Runner/rules/branches/main \
  --jq '[.[]|select(.type=="required_status_checks")
         |.parameters.required_status_checks[].context]'
```

`check_required_contexts.py` fails CI when that answer and the hardcoded lists disagree, so
the two cannot rot apart silently — except for names it lists as `PENDING_REQUIRED_CONTEXTS`,
which are contexts the ruleset is about to require and tolerates in either state while a
by-hand ruleset edit catches up with a merged pull request.

**A pull request runs three legs, not eight** (#3141): `.github/pr-bc-versions.txt` — 27.0,
27.5 and 28.4. `main` still runs all eight on every push, and — the guarantee that holds when
merges are cancelling each other — every 30 minutes from `main-verdict-floor.yml` (#3003);
so does the release path. Two consequences for reading a verdict here. A green pull request has not
been measured on 27.3, 28.0, 28.1, 28.2 or 28.3, so a version-specific regression in one of
those surfaces on `main` rather than on the PR — which is what `bc-leg-rerun.yml` is for, since
it can dispatch any of the eight against your branch. And the leg-set evidence in section 5 is
thinner on a PR: three legs give far fewer distinguishable failing sets than eight, so prefer
the dispatch or an empty commit over reading a pattern out of three data points.

Wait in the **foreground** — `tools/ci-wait.py`, or `gh run watch <run-id>`. Never end a turn
while CI you are responsible for is still running (`no-backgrounding-long-commands.md`).
Re-check with `gh run view <id> --json status,conclusion` and treat anything other than
`completed` as "not yet reported".

### In the corpus, "which harness code ran" has two dials, and the obvious one is wrong

Same failure mode one level down: a verdict about **which code ran**, where the intuitive
identifier answers a different question. When a corpus failure is traced to a change in
`StefanMaron/MsDyn365Bc.On.Linux`, the obvious way to ask "did this run carry that change" is
the run's `referenced_workflows` — and for most harness fixes that is the wrong dial.

The corpus calls `MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master`, and
that reusable workflow then checks the harness out **again**, separately, for its scripts:
`ref: ${{ inputs.bc_linux_ref }}`, default `master`. The corpus does not pass `bc_linux_ref`.

| dial | what it controls | resolved when |
|---|---|---|
| `referenced_workflows` SHA | the **workflow YAML** — job graph, steps, inputs | at dispatch |
| `bc_linux_ref` (default `master`) | the **scripts** — `run-tests-hybrid.py`, `run-tests-altool.py`, `run-tests.sh`, `publish-app.sh` | when the job's checkout step runs |

Nearly every harness fix that matters here is a **script** change — the hybrid runner's leg
scheduling, the altool infrastructure-retry gate, the publish helper. The two dials usually
agree, because both track `master`, but they are read at different times and the gap between
them is the account-wide queue backlog this file already knows about. On a quiet queue that is
seconds; on a busy one, hours, and then one run's YAML and its scripts come from different
commits.

So compare the **job's** start time against the harness commit's timestamp, not the run's
`referenced_workflows`:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs/<id>/jobs \
  --jq '.jobs[] | select(.name|test("/ test")) | "\(.name) started=\(.started_at)"'
gh api repos/StefanMaron/MsDyn365Bc.On.Linux/commits/<sha> --jq .commit.committer.date
```

A job whose `started_at` precedes the commit cannot have checked it out. One that started
after it almost certainly did — the checkout runs within seconds of job start — with a genuine
ambiguity window of a few tens of seconds, worth stating rather than resolving by assumption.

Recorded because it was published as evidence and was not: diagnosing
`MsDyn365Bc.On.Linux#61`, two corpus runs were reported as having "resolved `9dc3244` and
`ccb401a`" and therefore predating the retry gate. The conclusion was right and the evidence
named the wrong dial — the gate lives in `scripts/run-tests-altool.py`, so what decided it was
when each job's checkout ran. It agreed only because the queue was short that day. Getting this
backwards argues for reverting a fix that was never in the run.

## 3. Never re-run a failed job — not even to gather evidence

`gh run rerun` and the web "Re-run" button overwrite the failed run's logs permanently. Read
the log first (`gh run view <id> --log-failed`, or `mcp__github__get_job_logs` with
`failed_only: true, return_content: true`), save what you need. **Then do not re-run that same
run — not even after saving the log.** A re-run is never a diagnostic step — it destroys the
evidence a diagnosis needs, and section 5's flake-evidence standard needs a second, independent
run, not the same one overwritten in place. See "Getting a second run of the same commit"
under section 5 for how to get one without this.

### "Cancelled" does not mean "no log to lose"

This rule used to exempt cancelled runs outright, on the reasoning that a cancelled run never
produced a failure log. It can have produced one: a check run concludes `failure` **on its
merits** and its parent workflow run is cancelled only afterwards, so the log is real and
`gh run rerun` overwrites it just the same. Measured on head `c6377b30` (#3142), where
`preflight.py unit tests` carried three check runs for the one name — a `failure`, a
`cancelled` from run 34037049850, and a newest `failure` from run 34037050361 whose own
run-level conclusion was `failure`.

So before re-running a cancelled required context, ask whether anything on that commit failed
on its merits:

```bash
gh api "repos/StefanMaron/BusinessCentral.AL.Runner/commits/<FULL-SHA>/check-runs?per_page=100" \
  --jq '.check_runs[] | select(.conclusion=="failure") | "\(.name) \(.id)"'
```

Nothing failing → re-run the cancelled run; that is the #2726 case and it stands. Something
failing → read and save its log first, or get the second run by one of the routes in section 5
instead.

## 4. Diagnose from the log, not from a theory

Wait for the run to complete before reading it — a partial log reads as an unrelated failure.
Then find the actual failing assertion. A theory formed before the log arrives has been wrong
every time it was tried here.

## 5. "Pre-existing unrelated flake" needs evidence

Dismissed twice without checking; both times it was real and both blocked a release. Require
one of:

- the same failure reproducing on `origin/main` at a commit predating the branch;
- a *changing failing-leg set* across two **independent** runs of the same code (load-dependent,
  not the commit) — see below for how to get the second run without `gh run rerun`;
- an existing issue describing that exact failure.

**"Same code" means the same tree, not the same commit SHA.** An empty commit
(`git commit --allow-empty`) changes no tree content, only commit metadata, so its CI run is a
legitimate second data point for this standard even though the SHA differs from the original.
Two commits with different trees are not the same code no matter how closely related they look
— reading a leg-set change between two *different* head SHAs as a flake signal proved nothing
and cost real time on corpus PR 2639. Confirm the trees actually match before trusting a
comparison across commits:

```bash
[ "$(git rev-parse <sha1>^{tree})" = "$(git rev-parse <sha2>^{tree})" ] && echo "same tree"
```

### Getting a second run of the same commit without `gh run rerun`

Both options below create a brand-new, separate workflow run. Neither touches the original
failed run or its log — that run and its log stay exactly as they were, so a diagnosis made
from the original log is never at risk.

**Preferred — dispatch the one leg.** Cheapest by far: one leg instead of eight, against the
ref that already carries the verdict (still the same commit as long as nobody has pushed
since).

In this repository, `.github/workflows/bc-leg-rerun.yml`:

```bash
gh workflow run bc-leg-rerun.yml --repo StefanMaron/BusinessCentral.AL.Runner \
  --ref <branch> -f bc-version=28.4
```

`bc-version` is a prefix from `.github/bc-versions.txt`; an unknown one fails the run rather
than resolving to an empty matrix. The leg does exactly the work that leg does on a normal
run — `required` and `unit-tests` are still computed from the full version list, so a
dispatched 28.0 leg does not suddenly run `AlRunner.Tests` that the real 28.0 leg skips. Since
#3141 the dispatch has a second use: it is how you get 27.3, 28.0, 28.1, 28.2 or 28.3 to run
against a branch at all, because the pull-request matrix no longer includes them.

In the AL-language corpus, `.github/workflows/ci.yml` (`bc_version` input):

```bash
gh workflow run ci.yml --repo StefanMaron/BusinessCentral.AL.Language.Tests \
  --ref <branch> -f bc_version=28.4
```

That is how impl-4 got a second, independent verdict on corpus PR #144's BC 28.4 leg without
disturbing the original: run 33962643816, dispatched against the same SHA that had just
failed, came back 2495/2495 clean.

**A single-leg dispatch cannot satisfy this repository's required check**, by construction:
`BC test matrix passed` is declared in `test-matrix.yml`, and `bc-leg-rerun.yml` does not
contain that job at all, so there is no conclusion — not success, not `skipped` — for it to
report. `AlRunner.Tests/BcLegRerunWorkflowTests.cs` holds that property in place. Treat the
result as evidence for a human, never as a gate that has been cleared.

**The corpus has no equivalent guarantee** — there the eight `BC <ver> / test` legs ARE the
required contexts, so a dispatched leg reports a check run with the same name as the one that
gated. Measured once, on corpus PR #144: the dispatch produced a second `BC 28.4 / test` check
run with conclusion `success` on the same head SHA as the original `failure`, and the PR stayed
`BLOCKED` with `gh pr checks --required` still reporting the failing one. So it did not clear
the gate — but that is one observation of undocumented GitHub behaviour, not a rule. Never
dispatch a corpus leg expecting it to turn a PR green, and check `gh pr checks --required`
rather than assuming either way.

**Fallback, when a workflow has no per-leg dispatch** — push an empty commit:

```bash
git commit --allow-empty -m "chore: re-run CI to check a leg-set flake (no content change)"
git push
```

Same tree, so it is still "the same code" by the rule above, but it spends a full eight-leg
run in a queue shared across the whole account. Prefer the dispatch.

### What neither of these is

Neither is a way to make a red required check pass without fixing anything, and neither
overrides a human's call about what to do with the result — their only legitimate use is
deciding whether "pre-existing unrelated flake" is actually true, evidence a person can act on.
If the failing-leg set repeats identically on the second run, that is the code, not the
environment — stop calling it a flake and go fix it.

Re-rolling CI *hoping* the same failure won't recur, as a way to get past a red required check,
is separately and explicitly not sanctioned, by any mechanism — tried for real with a genuine
content-changing push on corpus PR #145, it moved the failing leg from BC 28.2 to BC 28.0
without fixing anything, at roughly 8 minutes of the whole account's shared Actions queue per
attempt. GitHub Actions concurrency is scoped per account, not per repository, so that cost is
shared with every other repo and agent using the same account, not just this one. Nobody
bypasses a red required check. The recipes above are for finding out whether a failure is
real, not for making it go away.

### Deliberately not in `tools/ci-wait.py`

`tools/ci-wait.py` answers "has this PR's required check reported a verdict on its current
head" — a different question from "get me an independent second run of this exact commit."
Folding the dispatch recipe into it would conflate two different operations behind one tool, so
it stays out. If this gets used often enough to be worth automating, that is a new,
narrowly-scoped tool, not an addition to `ci-wait.py`.

## Sister rules

- `no-backgrounding-long-commands.md` — how to wait on anything long-running
- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee boundary
