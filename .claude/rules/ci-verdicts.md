# Driving a PR through CI

"PR opened" is not the deliverable; "PR merged" is. Fix CI failures and address review
comments yourself — don't wait for someone else to notice a PR is red.

## 0. Read the verdict; never block on CI

Push the branch, open the pull request, carry on. `tools/ci-wait.py <PR> --timeout 0` is the
read — one pass, one answer, about a second:

```bash
tools/ci-wait.py 2379 --timeout 0     # reads the verdict now; does not block
```

**`--timeout 0` means a single pass** and says so in its own `--help`; a negative value is
refused by `argparse` rather than behaving like zero, and `--timeout 1` still means "wait up to
a second" (#3351). Never hand-roll `gh run view` plus `sleep` — one call replaces the loop.

**Who reads it, and when.** An implementation agent marks its PR ready and hands back; it never
waits and never merges (`.claude/agents/impl-agent.md`). The coordinator lists open PRs once
per cycle (`orchestrating-a-session`, one listing per sweep) and reads the verdict of each PR it
considers arming then, and nothing is lost by reading late:
`gh pr merge --auto` lands a reviewed PR the moment its checks go green with nobody present.

**Read the tool's exit code, not a pipeline's.** Every row of the table below is worthless if
`$?` came from the last command in a pipe: `tools/ci-wait.py <N> --timeout 0 | tail -25` leaves
`$?` as **`tail`'s** status, which is 0 whatever the verdict was — so a FAILED or a
still-running PR reads as green, and the printed text saying otherwise is the only thing left
that is true. Redirect to a file and check `$?`, or use `${PIPESTATUS[0]}`. Measured twice on
2026-09-11: an agent reported this tool "exits 0 on a non-verdict" from a piped read (it exits
2, and did), and the coordinator made the same mistake reading a `| tail` earlier the same
night. It is the same class as the trap below — an answer that could not have come out any
other way.

**And when you report a surprising exit code, say how you captured it.** Reading directly
protects you; it does nothing for a wrong number already written down. #3341 reported `rc=0`
beside a GraphQL failure, which is impossible directly (`rc=1`) and exact under `| tail` — that
artifact sat in the issue body for three days as an apparently-measured fact, and it was
load-bearing: it is what made the reporter conclude the exit code was unreliable **in both
directions**, a stronger and different claim than the true one. Anyone reading that issue
inherited a wrong belief about the instrument, sourced to a real transcript. A number nobody can
attribute to a capture method is not a measurement, and the reader cannot tell.

**Trap: an answer that could not have come out any other way is not evidence.** Under the
pre-#3351 zero-timeout path a green PR, a red PR and a PR with no checks all printed `STILL
RUNNING`, and its only tell was the empty parentheses of `STILL RUNNING after 0s ()`, where the
progress detail belongs. Check a new invocation against a PR whose state you already know.

| exit | meaning |
|---|---|
| 0 | every required check passed **on the current head** — safe to report green |
| 1 | a required check failed; **the failing log is already printed**. The list is what has reported SO FAR — while other required checks are still running it can still grow, and the verdict says how many have not reported. Do not scope a diagnosis to those names until every check has reported. |
| 2 | timed out while still running — **not a verdict**, call again |
| 3 | could not determine (auth, network, no checks) — **or** the running copy of `ci-wait.py` is itself behind `origin/main` (#3020) — **or** the required-context set could not be established without narrowing it (#3002) |
| 4 | **blocked, not failing** — every check passed but the merge is still refused and nothing else says why. Two causes: a *required* context is `cancelled` on this commit (#2726) — the one case where `gh run rerun` can be correct, but only after checking that no check run on this commit concluded `failure` before the cancellation (section 3); or a *required* context produced **no check run at all** and every workflow run for the commit has finished (#2807), a trigger/`paths:` filter question rather than a re-run. Never reach for `--admin` for any of these. **A third cause is invisible to every check-reading tool, this one included**: the `main` ruleset sets `require_extra_approval_for_unattributed_changes`, so a PR carrying a commit attributed to **another real GitHub account** is `BLOCKED` until a human approves, with 13/13 green and `mergeable: MERGEABLE` (#3942). It is not a check, so nothing here can see it — read the authors before arming: `gh pr view <N> --json commits --jq '[.commits[].authors[]|{login,name}]|unique'`. **Read the `login`, and ignore an empty one and `claude`**: this loop's own commits carry `{login:"", name:"Test"}` and `{login:"claude"}`, and those never block — measured on #3943, CLEAN with both. What blocked #3927 was a third entry resolving to a real account. A check keyed on "any login that is not the pushing identity" false-positives on every PR this loop writes. Never self-approve to clear it; that rule exists to put a human in front of exactly this. |

**Exit 2 is the ordinary answer here, not a failure**, and never a green: the checks have not
reported, so move on and read again later.

**A `main-verdict-floor.yml` run whose conclusion is `success` may have measured nothing.** Its
`verdict-needed` job skips `floor-matrix` when the SHA already has a conclusive matrix run, so the
*workflow* succeeds while the job that would answer the question never ran — deliberate debounce,
not a fault. Measured: on `c028bf3f`, run `34656743829` FAILED across five legs at 23:06 and run
`34658754607` reported `success` at 23:37 with `floor-matrix: skipped`. Reading the second as "main
recovered" would invert the truth on identical code. **Check the `floor-matrix` job's own result
before reading a floor run as a verdict** — `gh api repos/<o>/<r>/actions/runs/<id>/jobs --jq
'.jobs[]|"\(.name): \(.conclusion)"'` — and treat `skipped` as "no new measurement", never as green.

**Beside every verdict it prints one line about `main` itself** — `main floor: RED on 8b6885f4
(main-verdict-floor.yml, 1h ago)` (#3679), so a PR branched during a red window is visible as
inheriting a failure it did not cause. It is a report: it never changes the exit code, and a
read that did not happen prints `unavailable`, never a verdict.

**And one line per corpus PR the body cites** — `corpus PR #226: NOT-MERGEABLE (head 321ac71a)`
(#3674), from `.github/scripts/corpus_pr_state.py`, the same module `pr-gate.yml`'s
`A cited corpus PR must be able to merge` job runs. Same contract as the floor line: a report,
never an exit code, `unavailable` on a read that did not happen and `UNREADABLE` on a malformed
declaration. **That gate is a status check, so it evaluates on push, `edited` and `labeled` and
not when the corpus PR moves** — a corpus PR that goes green or merges later leaves a stale red
until something re-triggers it, and editing the body or applying `status: review-ready` is the
cheap re-trigger. It is deliberately not in the branch ruleset (it reads `api.github.com`), so
that stale red does not block a merge; the arming list in `orchestrating-a-session` is what
holds out for `MERGED`.

`ci-wait.py` reads the required contexts from the **live branch ruleset** on each invocation
(`GET /repos/{owner}/{repo}/rules/branches/main`, which reports only *active* rulesets),
falling back to its built-in list and saying so loudly; `check_required_contexts.py` fails CI
when the built-in lists and the live ruleset drift apart (#2785). A ruleset answer *narrower*
than the built-in list returns exit 3 rather than a verdict, because a partial read and a
deliberate removal are indistinguishable from here and the smaller set is the one that produces
a false green (#3002).

**Read the `N/N ruleset context(s)` figure on a green**, against the live ruleset rather than
any number written down: a green accounting for fewer contexts than the ruleset requires is the
signal, whatever N is (#3002, #3165).

Where several runs of one workflow exist on one commit — normal for `require-tests.yml`, which
has no `concurrency` block and triggers on `labeled`/`unlabeled` — the verdict comes from the
**newest workflow run**, not the highest check-run id, because a check run's id is allocated
when its *job starts* and id order inverts once two runs overlap (#2748).

The tool enforces the two rules agents keep getting wrong: checks are matched against the PR's
**current head SHA**, so a newer completed run for an older push is never reported as this
push's result; and on failure it fetches `--log-failed` for you, so there is never a reason to
reach for `gh run rerun`, which destroys the log permanently.

### Run it from a tree you have fetched — the tool being right says nothing about your copy

`git fetch origin main` in your worktree before you trust any tool in `tools/`. You invoke
`tools/ci-wait.py` by relative path, so you run **your worktree's** copy, and a worktree is
created once and never fast-forwarded again (#3020).

`ci-wait.py`, `pr-body.py` and `preflight.py` refuse rather than answer when `origin/main` has
moved their own file since your checkout branched (`tools/agent_self_freshness.py`; #3164) —
exit 3 for `ci-wait.py`, refuse-to-write for `pr-body.py`, and **exit 3 for `preflight.py` both
when its running copy is stale and when nothing vouches for it** (#3164). There is no flag to
switch the check off. A branch that legitimately *edits* one of them is not stale and is not
refused: what makes a copy stale is `origin/main` moving the file since the branch point.

**Expect refusals in a burst right after such a merge, not as an outage** — the fix is
`git fetch origin main` in each worktree, never a revert.

Two things the guard cannot do:

- **It cannot help a copy older than itself**, so run `origin/main`'s copy directly:
  ```bash
  git fetch origin main
  d=$(mktemp -d) && mkdir -p "$d/tools"
  for f in ci-wait.py agent_self_freshness.py agent_stdio.py; do
    git show "origin/main:tools/$f" > "$d/tools/$f"
  done
  python3 "$d/tools/ci-wait.py" <PR> --timeout 0
  ```
  **Extract all three files** (#3295, #3658): a lone copy cannot import its sibling guard and
  now exits 3 rather than judging the PR with the safety check skipped, and without
  `agent_stdio.py` the copy prints through the console codec, so on a cp1252 box one
  non-cp1252 character in a failing-log tail raises `UnicodeEncodeError` and exits **1** — this
  tool's "a required check failed" code — a two-file copy says so in a `note:` line on stderr.
  The two loud `unknown` freshness notes a `/tmp` directory produces are fine *there only*,
  because you extracted the files from `origin/main` yourself; elsewhere an `unknown` that fails
  open is the defect #3296 fixed.
- **It cannot turn a network failure into a verdict.** `refs/remotes/origin/main` is shared by
  every worktree, so the check costs no network, and one `git ls-remote` confirms that shared
  ref against the remote. An unreachable remote is a loud note and the local check stands,
  never a refusal.

### A cancelled run's leftovers sit in the same rollup as the live run, and `gh pr checks` hides which is which

Two traps, and `gh pr checks` corroborates neither: a cancelled run's aggregate job can conclude
`failure` because it runs `if: always()` over killed `needs` (#3010), and `gh pr checks` prints
one row per context name without saying which run produced it, so a leftover from a cancelled
run can be the row you see and is then indistinguishable from a live failure (#3016).
Superseded runs are the common case here, not an edge case (#3003).

**The reliable check is the run, not the rollup:**

```bash
gh run view <run-id> --json headSha,conclusion,status
# and, for the whole commit:
gh api "repos/StefanMaron/BusinessCentral.AL.Runner/actions/runs?head_sha=<FULL-SHA>&per_page=100" \
  --jq '.workflow_runs[] | "\(.id) \(.name) \(.status) \(.conclusion)"'
```

A `cancelled` conclusion — on an older `headSha`, or on a run of the same workflow that a newer
run has replaced — is **not this push's verdict**, whatever its individual jobs say. Two runs of
one workflow on one SHA is normal (#2726).

`head_sha` needs the **full** 40-character SHA. An abbreviated one returns `"workflow_runs": []`
— a false negative that reads exactly like "no runs for this commit".

`tools/ci-wait.py` applies all of this itself since #3002: a conclusion from a cancelled run is
reclassified as a cancellation, and a name whose newest check run belongs to a run being
replaced by one still in flight gets no verdict in either direction. You still need the recipe
above when reading a run by hand.

## 1. Check for merge conflicts first

```bash
gh pr view <N> --json mergeStateStatus --repo <owner>/<repo>
```

`DIRTY` / `CONFLICTING` → rebase on the base branch, resolve, force-push with
`--force-with-lease`, re-check until it reads `BLOCKED` or `CLEAN`.

**CI will not run on a PR with conflicts.** Check this before investigating any CI problem —
"no checks reported" almost always means merge conflicts, not a CI outage. `CLEAN` covers only
*textual* conflicts: it says nothing about whether CI ran on your current head, and nothing
about a semantic break because `main` moved underneath you.

## 2. A verdict is about one commit, not one PR

**Verify every verdict against the PR's current head SHA**: confirm the check's commit matches
local `HEAD`, because a row can belong to an older push or to a superseded run, and a mismatch
means "not yet reported", never "green". Which run produced a row is the trap section 0 covers —
`gh pr checks` does not say. Report a PR with checks still running as exactly that, which is a
fine place to leave one (section 0).

**Which contexts gate.** Two come from the big workflows: **`BC test matrix passed`**
(`.github/workflows/test-matrix.yml`) and **`Tests updated`**
(`.github/workflows/require-tests.yml`, not `pr-check.yml`; #2726). The matrix legs report as
`bc-tests / BC <ver> (required)` — that `(required)` is part of the job's name and does NOT
make the leg a required context, which is why a single-leg diagnostic run cannot clear the gate
and a red leg still blocks through the aggregate.

The rest come from **`.github/workflows/pr-gate.yml`**, one context per job — most of which
gate, but not all: the ones listed as `PENDING_REQUIRED_CONTEXTS` in
`check_required_contexts.py` are deliberately out of the ruleset, because promoting one early
makes `ci-wait.py` answer exit 3 for everybody (#3002). Everything in `pr-check.yml` is
advisory and cannot block a merge (#3165). **A check that talks to a third-party API stays
advisory on purpose** — `Required-context list must match the live branch ruleset` reaches
`api.github.com`, and a required check that can go red on an outage blocks every merge in the
repository for something no author can fix. So a red tick is not by itself proof the merge is
blocked — ask the ruleset:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Runner/rules/branches/main \
  --jq '[.[]|select(.type=="required_status_checks")
         |.parameters.required_status_checks[].context]'
```

Do not read the exact list out of this file. `check_required_contexts.py` fails CI when that
answer and the hardcoded lists disagree, except for `PENDING_REQUIRED_CONTEXTS` names, which it
tolerates in either state while a by-hand ruleset edit catches up with a merged pull request.

**A pull request runs three legs, not eight** (#3141): `.github/pr-bc-versions.txt` — 27.0,
27.5 and 28.4. `main` runs all eight on every push, every 30 minutes from
`main-verdict-floor.yml` and again about ten minutes after a merge burst ends (#3003, #3679 —
that workflow's own header states both triggers), and on the release path. So a green pull request has not been
measured on 27.3, 28.0, 28.1, 28.2 or 28.3 — dispatch `bc-leg-rerun.yml` for one of those
against your branch — and section 5's leg-set evidence is thin on a PR, where three legs give
few distinguishable failing sets; prefer the dispatch or an empty commit.

**A pull request whose every changed path ends in `.md` runs no legs at all** (#2890):
`test-matrix.yml`'s `changes` job measures the diff through `pr_changed_files.sh`, `bc-tests` is
skipped, and `BC test matrix passed` reports success with a step log saying the matrix was not
run. The decision comes from the diff, never from the `docs-only` label. The guards that read
`.md` files do not depend on the matrix: `tools/test_doc_pointers.py` (#3425) and
`tools/test_matrix_docs_drift.py` (#3426) gate a docs-only PR under the required
`tools/ unit tests` context.

### In the corpus, "which harness code ran" has two dials, and the obvious one is wrong

When a corpus failure is traced to a change in `StefanMaron/MsDyn365Bc.On.Linux`, the run's
`referenced_workflows` is the wrong dial for a **script** fix — and nearly every harness fix
that matters here is one. The corpus calls
`MsDyn365Bc.On.Linux/.github/workflows/bc-test-from-source.yml@master`, and that reusable
workflow checks the harness out again for its scripts at `ref: ${{ inputs.bc_linux_ref }}`,
default `master`, which the corpus does not pass:

| dial | what it controls | resolved when |
|---|---|---|
| `referenced_workflows` SHA | the **workflow YAML** — job graph, steps, inputs | at dispatch |
| `bc_linux_ref` (default `master`) | the **scripts** — `run-tests-hybrid.py`, `run-tests-altool.py`, `run-tests.sh`, `publish-app.sh` | when the job's checkout step runs |

The two usually agree, because both track `master`; the gap between them is the account-wide
queue backlog, seconds on a quiet queue and hours on a busy one. So compare the **job's** start
time against the harness commit's timestamp:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs/<id>/jobs \
  --jq '.jobs[] | select(.name|test("/ test")) | "\(.name) started=\(.started_at)"'
gh api repos/StefanMaron/MsDyn365Bc.On.Linux/commits/<sha> --jq .commit.committer.date
```

A job whose `started_at` precedes the commit cannot have checked it out; one that started after
it almost certainly did, with an honest ambiguity window of a few tens of seconds. Getting this
backwards argues for reverting a fix that was never in the run (`MsDyn365Bc.On.Linux#61`).

## 3. Never re-run a failed job — not even to gather evidence

`gh run rerun` and the web "Re-run" button overwrite the failed run's logs permanently. Read the
log first (`gh run view <id> --log-failed`, or `mcp__github__get_job_logs` with
`failed_only: true, return_content: true`) and save what you need — then still do not re-run
that run. A second, independent run is what section 5's flake standard needs; "Getting a second
run of the same commit" has the routes.

### An empty log fetch is a refusal, not an empty log

Both recipes print **nothing at all** for some jobs, and neither says "refused" where a caller
reading stdout will see it — the same family as the `grep -E` and `rg --hidden` traps in
`CLAUDE.md`. They fail on **different** jobs, so neither is a fallback for the other:

| recipe | empty when | how it looks |
|---|---|---|
| `gh run view --log-failed` | the job has no step whose conclusion is `failure` — a `cancelled` job, or one whose failing step was cancelled | **exit 0**, zero bytes: indistinguishable from success on an empty log |
| `gh api .../jobs/<id>/logs` | the log carries terminal escape sequences, which BC logs do routinely | exit 1, zero bytes on **stdout**, the reason only on stderr |

So when one comes back empty, try the other before concluding anything (#3305):

```bash
gh api repos/<owner>/<repo>/actions/runs/<run-id>/jobs \
  --jq '.jobs[]|select(.conclusion=="failure" or .conclusion=="cancelled")|"\(.id) \(.name)"'
gh api repos/<owner>/<repo>/actions/jobs/<job-id>/logs --allow-escape-sequences
```

**`--allow-escape-sequences` is not optional on the API form**, and its refusal wears two faces:
unredirected it exits 1 with `the response contains terminal escape sequences` on stderr, while
redirected (`> leg.log`) it prints that message to the console, writes **zero bytes** to the file
and **exits 0** — so a `$(...)` capture or a saved log reads as "no matches" with no error at all.
Treat an empty body as *unavailable*, never as a zero. `tools/ci-wait.py` tries both fetch forms
on exit 1 and, when both come back empty, says both were refused rather than that the job has no
log (#3309).

### "Cancelled" does not mean "no log to lose"

A check run can conclude `failure` **on its merits** and have its parent workflow run cancelled
afterwards, so the log is real and `gh run rerun` overwrites it (#3142). Before re-running a
cancelled required context, ask whether anything on that commit failed on its merits:

```bash
gh api "repos/StefanMaron/BusinessCentral.AL.Runner/commits/<FULL-SHA>/check-runs?per_page=100" \
  --jq '.check_runs[] | select(.conclusion=="failure") | "\(.name) \(.id)"'
```

Nothing failing → re-run the cancelled run; that is the #2726 case. Something failing → read and
save its log first, or get the second run by a route in section 5.

## 4. Diagnose from the log, not from a theory

Wait for the run to complete before reading it — a partial log reads as an unrelated failure —
then find the actual failing assertion, never a theory formed before the log arrived.

## 5. "Pre-existing unrelated flake" needs evidence

Require one of:

- the same failure reproducing on `origin/main` at a commit predating the branch;
- a *changing failing-leg set* across two **independent** runs of the same code (load-dependent,
  not the commit) — see below for how to get the second run without `gh run rerun`;
- an existing issue describing that exact failure.

**"Same code" means the same tree, not the same commit SHA.** An empty commit
(`git commit --allow-empty`) changes no tree content, so its run is a legitimate second data
point; two commits with different trees are not the same code however closely related they look
(corpus PR 2639). Confirm the trees match before comparing across commits:

```bash
[ "$(git rev-parse <sha1>^{tree})" = "$(git rev-parse <sha2>^{tree})" ] && echo "same tree"
```

### A red you inherited from the corpus is not a flake — count it per codeunit

The commonest "is this red mine?" here is neither a flake nor your defect: the corpus is
resolved per run, so a corpus PR merging ahead of the runner PR its tests need reddens every
PR in flight, deterministically, until that runner PR lands. Measured four times on #3922 —
one window held `main` red for 9h45m and blocked five PRs.

None of §5's three tests applies, because it is not load-dependent: it reproduces on every
run, and a re-run destroys the log while proving nothing. `tools/ci-wait.py` prints the answer
beside the failing log it already fetched:

```
--- failing corpus codeunits (#3922: is this red mine?) ---
  Codeunit60285  x2  <- open runner PR #3996 fixes this
  Codeunit60976  x6  <- open runner PR #3985 fixes this
```

Every failing codeunit matched to an open runner PR ⇒ inherited; rebase once they land.
**Anything unmatched is possibly the PR's own**, whatever the others say.

**Match per codeunit; never on the total.** The inherited total shrinks as each foreign pair
lands — measured 17 → 13 in twenty minutes when one of four merged — so "17 means inherited"
is wrong within the hour, and it fails toward the dangerous side: a later run showing 13 reads
as "fewer than inherited, so something here is mine". By hand, when you have a log rather than
a PR number:

```bash
command grep -E "^\S+Z *FAIL " <leg.log> | command grep -oE "Codeunit[0-9]+" | sort | uniq -c
```

**A docs-only PR going green beside a red BC-leg PR points at the corpus** — docs-only PRs run
no legs, so they flow through the window untouched.

### Getting a second run of the same commit without `gh run rerun`

Both options create a brand-new, separate workflow run and leave the original run and its log
untouched.

**Preferred — dispatch the one leg**, one leg instead of eight, against the ref that already
carries the verdict:

```bash
gh workflow run bc-leg-rerun.yml --repo StefanMaron/BusinessCentral.AL.Runner \
  --ref <branch> -f bc-version=28.4
```

`bc-version` is a prefix from `.github/bc-versions.txt`; an unknown one fails the run rather
than resolving to an empty matrix. The leg does exactly the work that leg does on a normal run
— `required` and `unit-tests` are still computed from the full version list — and since #3141
it is also the only way to run 27.3, 28.0, 28.1, 28.2 or 28.3 against a branch.

In the AL-language corpus, `.github/workflows/ci.yml` (`bc_version` input):

```bash
gh workflow run ci.yml --repo StefanMaron/BusinessCentral.AL.Language.Tests \
  --ref <branch> -f bc_version=28.4
```

**A single-leg dispatch cannot satisfy this repository's required check**, by construction:
`BC test matrix passed` is declared in `test-matrix.yml` and `bc-leg-rerun.yml` does not contain
that job, so there is no conclusion for it to report; `AlRunner.Tests/BcLegRerunWorkflowTests.cs`
holds that property. Treat the result as evidence for a human, never as a cleared gate.

**The corpus has no equivalent guarantee** — there the `BC <ver> / test` legs ARE the required
contexts (`verify-execution-not-the-tick.md` § "Which legs were ever going to run it" says
which), so a dispatched leg reports a check run with the gating name. Never dispatch
a corpus leg expecting it to turn a PR green, and check `gh pr checks --required` rather than
assuming either way (corpus PR #144).

**Fallback, when a workflow has no per-leg dispatch** — push an empty commit:

```bash
git commit --allow-empty -m "chore: re-run CI to check a leg-set flake (no content change)"
git push
```

Same tree, so still "the same code", but it spends a full eight-leg run in a queue shared across
the whole account. Prefer the dispatch.

### What neither of these is

Neither makes a red required check pass without a fix, and neither overrides a human's call
about the result: their only use is deciding whether "pre-existing unrelated flake" is true. If
the failing-leg set repeats identically on the second run, that is the code — stop calling it a
flake and go fix it. **Nobody bypasses a red required check**, and re-rolling CI hoping a
failure will not recur is not sanctioned by any mechanism; Actions concurrency is scoped per
account, so each attempt spends the whole account's shared queue (corpus PR #145).

### A third possibility: the red is inherited, and the remedy is a REBASE, not a re-run

Before treating a red as yours or as a flake, ask whether your branch is simply missing a fix
that `main` already has. The corpus is **resolved per run, not pinned** (#3737), so a merged
corpus commit reaches every open branch on its next run, while the runner fix it needs reaches a
branch only by being *in* it. A PR that branched before that fix runs the new corpus test
against old runner code — indefinitely, because re-running measures the same pair again.

**The signature is a failing codeunit that matches no open runner PR**, shared across several
unrelated PRs:

```bash
command grep -E "^\S+Z *FAIL " <leg.log> | command grep -oE "Codeunit[0-9]+" | sort | uniq -c
git merge-base --is-ancestor <fix-merge-commit> <pr-head>   # false => needs a rebase
```

Then confirm the rebase preserved what a reviewer already read, so a standing verdict still
applies. `git patch-id --stable` over `git diff origin/main...HEAD` is the cheap first check —
but **`patch-id` hashes hunk context, so a changed id means "look", not "the content moved".**
Resolve a difference by diffing the two diffs' added and removed lines directly; equal there is
equal. Measured on thirteen PRs in one sweep: twelve identical, one changed id whose
added/removed lines differed in **zero** places (#4092).

Do not size this by leg count or failure count — they look like an ordinary red. One PR in that
same sweep had a different failing codeunit and needed separate treatment, which only the
per-codeunit read distinguished.

### Deliberately not in `tools/ci-wait.py`

`ci-wait.py` answers "has this PR's required check reported a verdict on its current head", a
different question from "get me an independent second run of this exact commit". Automating the
dispatch would be a new, narrowly-scoped tool, not an addition to it.

## Sister rules

- `no-backgrounding-long-commands.md` — how to wait on anything long-running
- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee boundary
- `verify-execution-not-the-tick.md` — the corpus-side companion: a green leg does not
  prove the tests you added executed, and the check for that false-zeros
- `guards-need-a-third-state.md` — why exit 3 exists at all, and the four other guards
  that did or do resolve "could not tell" toward success

History: docs/incidents/ci-verdicts.md
