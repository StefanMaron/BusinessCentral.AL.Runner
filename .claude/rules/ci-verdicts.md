# Driving a PR through CI

"PR opened" is not the deliverable; "PR merged" is. Fix CI failures and address review
comments yourself — don't wait for someone else to notice a PR is red.

The by-hand queries this rule sends you to live in the `reading-ci-runs` skill; the incidents
behind each trap are in `docs/incidents/ci-verdicts.md`.

## 0. Read the verdict; never block on CI

Push the branch, open the pull request, carry on. `tools/ci-wait.py <PR> --timeout 0` is the
read — one pass, one answer, about a second:

```bash
tools/ci-wait.py 2379 --timeout 0     # reads the verdict now; does not block
```

**`--timeout 0` means a single pass**; a negative value is refused, and `--timeout 1` still
means "wait up to a second" (#3351). Never hand-roll `gh run view` plus `sleep`.

**Who reads it, and when.** An implementation agent marks its PR ready and hands back; it never
waits and never merges (`.claude/agents/impl-agent.md`). The coordinator reads the verdict of
each PR it considers arming once per sweep (`orchestrating-a-session`); nothing is lost by
reading late, because `gh pr merge --auto` lands a reviewed PR the moment its checks go green.

**Read the tool's exit code, not a pipeline's.** `tools/ci-wait.py <N> --timeout 0 | tail -25`
leaves `$?` as **`tail`'s** status, 0 whatever the verdict was, so a FAILED or still-running PR
reads as green. Redirect to a file and check `$?`, or use `${PIPESTATUS[0]}` (#3864).

**A completion notification's "exit code" is the WRAPPER's.** A foreground call past the
harness's 600s cap is backgrounded whatever you asked for (`no-backgrounding-long-commands.md`
owns that mechanism and the hook refusing it), and the notification reports the shell wrapper's
status — so `ci-wait.py` exiting **2** arrives as `completed (exit code 0)` (#4288). **Read the
tool's own printed verdict out of the output file; never the notification's number.**

**When you report a surprising exit code, say how you captured it.** A number nobody can
attribute to a capture method is not a measurement: #3341's `rc=0` was exact under `| tail` and
impossible directly, and it led its reader to a wrong belief about the instrument.

**Trap: an answer that could not have come out any other way is not evidence.** Before #3351 a
zero timeout printed `STILL RUNNING` for green, red and check-less PRs alike. Check a new
invocation against a PR whose state you already know.

| exit | meaning |
|---|---|
| 0 | every required check passed **on the current head** — safe to report green |
| 1 | a required check failed; **the failing log is already printed**. The list is what has reported SO FAR — do not scope a diagnosis to those names until every check has reported. |
| 2 | timed out while still running — **not a verdict**, call again |
| 3 | could not determine (auth, network, no checks) — **or** the running copy of `ci-wait.py` is behind `origin/main` (#3020) — **or** the required-context set could not be established without narrowing it (#3002) |
| 4 | **blocked, not failing** — every check passed but the merge is still refused. Either a *required* context is `cancelled` (#2726) — the one case where `gh run rerun` can be right, after section 3's check — or a *required* context produced **no check run at all** once every run finished (#2807), a trigger/`paths:` question rather than a re-run. A third cause is the attribution block below. Never reach for `--admin`. |

**The third exit-4 cause, which no check-reading tool can see, this one included**: the `main` ruleset's
`require_extra_approval_for_unattributed_changes` holds a PR carrying a commit attributed to
**another real GitHub account** at `BLOCKED` with every check green (#3942). Run
`tools/pr-attribution.py <N>` before arming: 0 nothing to approve, 1 an approval will be
required, 3 authors unknown — not an all-clear. It ignores an empty `login` and `claude` (this
loop's own commits), and the REST/MCP commit listings carry **no login at all**, which is exit 3,
never a pass. Never self-approve to clear it.

**Exit 2 is the ordinary answer, not a failure**, and never a green: move on and read again later.

**A `main-verdict-floor.yml` run whose conclusion is `success` may have measured nothing.** Its
`verdict-needed` job skips `floor-matrix` when the SHA already has a conclusive run — deliberate
debounce — so the workflow succeeds while nothing ran. **Check the `floor-matrix` job's own
result** and treat `skipped` as "no new measurement", never as green. The skips continue for as
long as a red SHA sits on `main`, so a long streak of green floor runs is not evidence of
anything; the run that measured the SHA is.

**Beside every verdict it prints report lines that never change the exit code**, and print
`unavailable` when the read did not happen:

- **`main floor: <state> on <sha> (…, N commits behind main)`** (#3679, #4111) — a PR branched in
  a red window inherits a failure it did not cause. **Read the distance, not only the age**: a
  green about a `main` several merges back is not a verdict about the commit under suspicion.
- **`corpus PR #N: <state>`**, one per corpus PR the body cites (#3674), from
  `.github/scripts/corpus_pr_state.py` — the same module `pr-gate.yml`'s `A cited corpus PR must
  be able to merge` job runs. **That gate evaluates on push, `edited` and `labeled`, not when the
  corpus PR moves**, so with corpus-first merging its stored `failure` outlives its cause. It is
  not in the ruleset, **but a stale red still refuses the merge** (#4206): a failing non-required
  check reads `UNSTABLE`, which auto-merge refuses, while `ci-wait.py` exits 0.
- **`corpus gate: STALE`** when the stored conclusion disagrees with the corpus PR now (`UNKNOWN`
  when either could not be read). `tools/armed-prs.py --refire-stale-corpus-gate` clears it by
  toggling a label, never by pushing (which would restart the matrix and re-arm against an
  unreviewed head). **Only `STALE` is re-fired** — an `UNKNOWN` gate was never established as
  stale. The arming list in `orchestrating-a-session` holds out for `MERGED`.

`ci-wait.py` reads the required contexts from the **live branch ruleset** on each invocation,
falling back loudly to its built-in list; `check_required_contexts.py` fails CI when the two
drift (#2785). A ruleset answer *narrower* than the built-in list is exit 3, because a partial
read and a deliberate removal look alike and the smaller set produces a false green (#3002).
**Read the `N/N ruleset context(s)` figure on a green**: one accounting for fewer contexts than
the live ruleset requires is the signal (#3002, #3165).

Where several runs of one workflow exist on one commit (normal for `require-tests.yml`), the
verdict comes from the **newest workflow run**, not the highest check-run id — ids are allocated
when a job starts, so their order inverts once runs overlap (#2748). The tool matches checks to
the PR's **current head SHA**, and fetches `--log-failed` for you on failure, so there is never
a reason to reach for `gh run rerun`.

### Run it from a tree you have fetched — the tool being right says nothing about your copy

`git fetch origin main` in your worktree before you trust any tool in `tools/`: you run **your
worktree's** copy, and a worktree is never fast-forwarded (#3020). `ci-wait.py`, `pr-body.py`
and `preflight.py` refuse when `origin/main` has moved their own file since your branch point
(`tools/agent_self_freshness.py`; #3164) — exit 3, refuse-to-write, and exit 3 (also when
nothing vouches for the copy) respectively. No flag switches it off; a branch that *edits* one
of them is not stale. **Expect refusals in a burst right after such a merge** — the fix is a
fetch in each worktree, never a revert.

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
  **Extract all three files** (#3295, #3658): without the freshness helper the copy exits 3,
  and without `agent_stdio.py` a non-cp1252 character in a failing-log tail can crash it with
  exit **1**, this tool's "a required check failed". The `unknown` freshness notes a `/tmp`
  copy prints are fine *there only*; elsewhere an `unknown` that fails open is the defect #3296
  fixed.
- **It cannot turn a network failure into a verdict.** An unreachable remote is a loud note and
  the local check against the shared `origin/main` ref stands, never a refusal.

### A cancelled run's leftovers sit in the same rollup as the live run, and `gh pr checks` hides which is which

A cancelled run's aggregate job can conclude `failure` because it runs `if: always()` over
killed `needs` (#3010), and `gh pr checks` prints one row per context without saying which run
produced it (#3016). Superseded runs are the common case here (#3003). **The reliable check is
the run, not the rollup** — `gh run view <run-id> --json headSha,conclusion,status`, or every
run for the commit (`reading-ci-runs`). A `cancelled` conclusion on an older `headSha`, or on a
run a newer one replaced, is **not this push's verdict**. `ci-wait.py` applies this itself since
#3002; you need it only when reading a run by hand.

**Counting the returned page is not counting the queue.** `?per_page=100` caps the array, so
counting it answers "what is on the page I fetched"; ask for `.total_count` (#4110). The tell is
a count brushing its own page size. **`head_sha` needs the full 40-character SHA** — an
abbreviated one returns `"workflow_runs": []`, which reads exactly like "no runs".

## 1. Check for merge conflicts first

```bash
gh pr view <N> --json mergeStateStatus --repo <owner>/<repo>
```

`DIRTY` / `CONFLICTING` → rebase on the base branch, resolve, force-push with
`--force-with-lease`, re-check until it reads `BLOCKED` or `CLEAN`.

**CI will not run on a PR with conflicts** — "no checks reported" almost always means conflicts,
not an outage. `CLEAN` covers only *textual* conflicts: not whether CI ran on your head, and not
a semantic break from `main` moving underneath you.

## 2. A verdict is about one commit, not one PR

**Verify every verdict against the PR's current head SHA**: a row can belong to an older push
or a superseded run, and a mismatch means "not yet reported", never "green". Report a PR with
checks still running as exactly that.

**Which contexts gate.** **`BC test matrix passed`** (`.github/workflows/test-matrix.yml`),
**`Tests updated`** (`.github/workflows/require-tests.yml`; #2726), and one context per job in
**`.github/workflows/pr-gate.yml`** — except the `PENDING_REQUIRED_CONTEXTS` in
`check_required_contexts.py`, deliberately out of the ruleset because promoting one early makes
`ci-wait.py` answer exit 3 for everybody (#3002). The legs report as `bc-tests / BC <ver>
(required)`; that `(required)` is part of the job name, not a required context, so a single-leg
run cannot clear the gate. Everything in `pr-check.yml` is advisory (#3165), and **a check that
talks to a third-party API stays advisory on purpose** — a required check that goes red on an
outage blocks every merge for something no author can fix. So a red tick is not by itself proof
the merge is blocked — ask the ruleset:

<!-- Recipe-unpinned: reads the live branch ruleset from api.github.com; check_required_contexts.py is the executable half -->
```bash
gh api repos/StefanMaron/BusinessCentral.AL.Runner/rules/branches/main \
  --jq '[.[]|select(.type=="required_status_checks")
         |.parameters.required_status_checks[].context]'
```

Do not read the exact list out of this file; `check_required_contexts.py` fails CI when that
answer and the hardcoded lists disagree, tolerating `PENDING_REQUIRED_CONTEXTS` names in either
state.

**A pull request runs only the legs in `.github/pr-bc-versions.txt`**; `main` runs the full
`.github/bc-versions.txt` on every push, on `main-verdict-floor.yml`'s triggers, and on the
release path (#3141, #3003, #3679). So a green PR has not been measured on the versions only the
second file lists — dispatch `bc-leg-rerun.yml` for one of those — and section 5's leg-set
evidence is thin on a PR.

**A pull request whose every changed path ends in `.md` runs no legs at all** (#2890): the
`changes` job measures the diff, `bc-tests` is skipped, and `BC test matrix passed` reports
success. The decision comes from the diff, never from the `docs-only` label. The doc guards
(`tools/test_doc_pointers.py`, `tools/test_matrix_docs_drift.py`) gate a docs-only PR under the
required `tools/ unit tests` context.

**Tracing a corpus failure to a `MsDyn365Bc.On.Linux` change:** `referenced_workflows` names the
workflow YAML, not the harness scripts — compare the job's `started_at` with the harness commit
(`reading-ci-runs`, "which harness code ran").

## 3. Never re-run a failed job — not even to gather evidence

`gh run rerun` and the web "Re-run" button overwrite the failed run's logs permanently. Read the
log first (`gh run view <id> --log-failed`, or `mcp__github__get_job_logs` with
`failed_only: true, return_content: true`) and save what you need — then still do not re-run
that run. A second, independent run is what section 5's flake standard needs; "Getting a second
run of the same commit" has the routes.

### A job log prints the `run:` block AS SOURCE, so grepping finds the script

Before any stdout, an Actions log echoes the step's script, so `grep -c` for anything the script
mentions — a loop's `echo`, a `::warning::` it *might* emit — counts intent, not execution.
Measured three times in one session, each a plausible count meaning the opposite
(`docs/incidents/ci-verdicts.md`). The tell is the **`[36;1m` colour escape** wrapping the echoed
source (visible only with `--allow-escape-sequences`):

```
[36;1m  echo "main moved while pushing; recomputing (attempt $attempt of 5)"[0m
```

**Filter the escape out, or match on output the script cannot contain** — a timestamped result
line, a summary, an `##[error]`:

<!-- Recipe-pinned-by: tools/test_log_source_echo_filter_recipe.py -->
```bash
gh api repos/<o>/<r>/actions/jobs/<id>/logs --allow-escape-sequences \
  | command grep -v $'\x1b\[36;1m' | command grep -E "<pattern>"
```

### An empty log fetch is a refusal, not an empty log

Both fetch forms print **nothing at all** for some jobs, on **different** jobs, so neither is a
fallback for the other — when one comes back empty, try the other before concluding anything
(#3305; the recipe is in `reading-ci-runs`):

| recipe | empty when | how it looks |
|---|---|---|
| `gh run view --log-failed` | no step concluded `failure` — a `cancelled` job, or one whose failing step was cancelled | **exit 0**, zero bytes |
| `gh api .../jobs/<id>/logs` | the log carries terminal escape sequences, which BC logs do routinely | exit 1, zero bytes on stdout, reason on stderr |

**`--allow-escape-sequences` is not optional on the API form**, and redirected (`> leg.log`) its
refusal writes **zero bytes** and **exits 0**, so a capture reads as "no matches". Treat an empty
body as *unavailable*, never as a zero. `ci-wait.py` tries both and says when both were refused
(#3309).

### "Cancelled" does not mean "no log to lose"

A check run can conclude `failure` **on its merits** and have its parent run cancelled
afterwards, so the log is real and a re-run overwrites it (#3142). Before re-running a cancelled
required context, list the commit's check runs that concluded `failure` (`reading-ci-runs`).
Nothing failing → re-run the cancelled run (the #2726 case). Something failing → save its log
first, or get the second run by a route in section 5.

## 4. Diagnose from the log, not from a theory

Wait for the run to complete before reading it — a partial log reads as an unrelated failure —
then find the actual failing assertion, never a theory formed before the log arrived.

## 5. "Pre-existing unrelated flake" needs evidence

Require one of:

- the same failure reproducing on `origin/main` at a commit predating the branch;
- a *changing failing-leg set* across two **independent** runs of the same code (load-dependent,
  not the commit) — see below for how to get the second run without `gh run rerun`;
- an existing issue describing that exact failure.

**"Same code" means the same tree, not the same commit SHA.** An empty commit is a legitimate
second data point; two commits with different trees are not the same code however related they
look (corpus PR 2639). Confirm the trees match:

```bash
a=$(git rev-parse --verify -q "<sha1>^{tree}") &&
b=$(git rev-parse --verify -q "<sha2>^{tree}") &&
[ "$a" = "$b" ] && echo "same tree"
```
<!-- Recipe-unpinned: takes two commit SHAs of real runs; the comparison itself is git rev-parse equality with nothing to get subtly wrong -->

**`--verify -q` is not optional, and the plain form fails in BOTH directions.** Without it,
`git rev-parse` on a SHA this clone lacks **echoes the argument back** and the `fatal:` goes to
stderr, so two unknown SHAs print **`same tree`**; and in a shallow clone an unfetched SHA prints
nothing for byte-identical trees — the false negative, on exactly the unfetched CI SHAs this
check is for (PR #4140).

### A red you inherited from the corpus is not a flake — count it per codeunit

The corpus is resolved per run, so a corpus PR merging ahead of the runner PR its tests need
reddens every PR in flight, deterministically, until that runner PR lands (#3922). None of the
three tests above applies — it reproduces every run, and a re-run destroys the log while proving
nothing. `tools/ci-wait.py` prints the failing codeunits beside the failing log, each matched to
the open runner PR that fixes it. Every one matched ⇒ inherited; rebase once they land.
**Anything unmatched is possibly the PR's own**, whatever the others say.

**Match per codeunit; never on the total.** The inherited total shrinks as each foreign pair
lands, so a later, smaller total reads as "fewer than inherited, so something here is mine" —
the dangerous direction. The by-hand count from a log is in `reading-ci-runs`. **A docs-only PR
going green beside a red BC-leg PR points at the corpus** — docs-only PRs run no legs.

### The same red, one phase later: the fix has MERGED and your branch predates it

A match against a *merged* runner PR is still a match, and its remedy is a **rebase**, not a
wait: a re-run measures the same old code against the same new corpus forever. It does not
change what an **unmatched** codeunit means.

```bash
git merge-base --is-ancestor <that-PR's-merge-commit> <pr-head>   # false => rebase
```

The exposed population is every PR **already in flight** when a pair lands — even a pair merged
seconds apart, as `bc-behavior-tests-go-upstream.md` step 5 asks, reddens them (#4092).

Then confirm the rebase preserved what a reviewer read. `git patch-id --stable` over
`git diff origin/main...HEAD` is the cheap first check — but **`patch-id` hashes hunk context, so
a changed id means "look", not "the content moved"**; resolve it by diffing the two diffs' added
and removed lines. **A bulk rebase hides a real defect**: in #4092's sweep two red PRs were red
for their own reasons, and only the per-codeunit read separated them.

### Getting a second run of the same commit without `gh run rerun`

Both options create a brand-new run and leave the original run and its log untouched.

**Preferred — dispatch the one leg**, against the ref that already carries the verdict:

```bash
gh workflow run bc-leg-rerun.yml --repo StefanMaron/BusinessCentral.AL.Runner \
  --ref <branch> -f bc-version=28.4
```

`bc-version` is a prefix from `.github/bc-versions.txt`; an unknown one fails the run. The leg
does exactly the work it does on a normal run, and since #3141 it is the only way to run a
version `.github/pr-bc-versions.txt` omits against a branch.

In the AL-language corpus, `.github/workflows/ci.yml` (`bc_version` input):

```bash
gh workflow run ci.yml --repo StefanMaron/BusinessCentral.AL.Language.Tests \
  --ref <branch> -f bc_version=28.4
```

**A single-leg dispatch cannot satisfy this repository's required check**, by construction:
`bc-leg-rerun.yml` does not contain the `BC test matrix passed` job
(`AlRunner.Tests/BcLegRerunWorkflowTests.cs` holds that). Treat the result as evidence for a
human, never as a cleared gate.

**The corpus has no equivalent guarantee** — there the `BC <ver> / test` legs ARE the required
contexts (`verify-execution-not-the-tick.md` § "Which legs were ever going to run it"), so a
dispatched leg reports a check run with the gating name. Never dispatch a corpus leg expecting
it to turn a PR green, and check `gh pr checks --required` rather than assuming (corpus PR #144).

#### Read a corpus run's `event` before its leg set — a dispatch has ONE leg

The dispatch above leaves a second run on the head, and the two are told apart by their
`event`, never by how many legs they carry. Corpus `ci.yml`'s `prepare` job emits a
**one-entry** matrix when `github.event.inputs.bc_version` is set and the full version list
otherwise, so a `workflow_dispatch` run is one in which **every required cloud leg but one does
not exist**. Both corpus matrices are `fail-fast: false`, so reading that short leg
set as a matrix that fail-fasted or collapsed is never right — and nothing about the run
says which kind it is until you ask.

**Recency does not discriminate either, and it is the tempting substitute**: a head can carry
several runs, and the gating one is often the **oldest** of them, because the dispatches come
after it. So take the `pull_request` run by its event, never the newest run and never a run id
someone handed you:

<!-- Recipe-pinned-by: tools/test_corpus_run_event_discriminator.py -->
```bash
head=$(gh pr view <N> --repo StefanMaron/BusinessCentral.AL.Language.Tests \
  --json headRefOid --jq .headRefOid | command grep -E '^[0-9a-f]{40}$') || {
    echo "refusing: no 40-char head SHA" >&2; exit 3; }
gh api "repos/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs?head_sha=$head&per_page=100" \
  --jq '.workflow_runs[] | select(.event == "pull_request") | "\(.id) \(.conclusion)"'
```

A `BC <ver> / test` leg for every version in the corpus matrix on that run is a gating verdict;
fewer means you are holding a dispatch, which is a second opinion under this section and never the verdict.

**Validate `$head` before interpolating it — the two ways it can be wrong fail in opposite
directions.** An **abbreviated** SHA returns an empty list that reads as "no runs"; an **empty**
one drops the filter entirely and returns the repository's whole run history, well-formed and
attached to no commit you asked about. The second is the dangerous one, and a check for an empty
*result* cannot catch it — which is why the `grep -E` above refuses rather than letting an unset
variable through (#3389's sixth mechanism).

**`gh pr checks` can answer this, but its default output does not** — it prints name, state,
duration and link, so the discriminator is absent unless you ask for it by name with
`--json name,event,link`. Two review agents reached opposite wrong verdicts off one head this
way (#3389; corpus PR #254 in `docs/incidents/ci-verdicts.md`).

**Fallback, when a workflow has no per-leg dispatch** — push an empty commit
(`git commit --allow-empty -m "chore: re-run CI to check a leg-set flake (no content change)"`,
then `git push`). Same tree, so still "the same code", but it spends a full-matrix run in a
queue shared across the whole account. Prefer the dispatch.

### What neither of these is

Neither makes a red required check pass without a fix, and neither overrides a human's call
about the result: their only use is deciding whether "pre-existing unrelated flake" is true. If
the failing-leg set repeats identically on the second run, that is the code — stop calling it a
flake and go fix it. **Nobody bypasses a red required check**, and re-rolling CI hoping a
failure will not recur is not sanctioned by any mechanism; Actions concurrency is scoped per
account, so each attempt spends the whole account's shared queue (corpus PR #145).

## Sister rules

- `no-backgrounding-long-commands.md` — how to wait on anything long-running
- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee boundary
- `verify-execution-not-the-tick.md` — the corpus-side companion: a green leg does not
  prove the tests you added executed, and the check for that false-zeros
- `guards-need-a-third-state.md` — why exit 3 exists at all

History: docs/incidents/ci-verdicts.md
