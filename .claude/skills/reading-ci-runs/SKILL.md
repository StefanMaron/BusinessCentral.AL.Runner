---
name: reading-ci-runs
description: Recipes for reading a GitHub Actions run by hand when tools/ci-wait.py does not answer the question — which run produced a check, whether a commit has a real failure under a cancellation, how deep the Actions queue is, which harness scripts a corpus job ran, and which corpus codeunits a failing leg names. The rules they serve live in .claude/rules/ci-verdicts.md; load this when you are about to type one of these queries.
---

# Reading a CI run by hand

`.claude/rules/ci-verdicts.md` owns the rules — what a verdict is, what never to re-run, what
counts as flake evidence. This file holds the queries those rules send you to. Read the rule's
trap before you trust any of these answers. `tools/ci-wait.py <PR> --timeout 0` already does
most of this for a PR's own verdict; reach for these when it does not answer your question.

`head_sha` needs the **full** 40-character SHA everywhere below: an abbreviated one returns an
empty list, and an empty one drops the filter (`ci-verdicts.md` §0 and §5).

## Which run produced a check (a cancelled run's leftovers)

The run, not the rollup — `gh pr checks` does not say which run a row came from
(`ci-verdicts.md` § "A cancelled run's leftovers sit in the same rollup as the live run"):

```bash
gh run view <run-id> --json headSha,conclusion,status
# and, for the whole commit:
gh api "repos/StefanMaron/BusinessCentral.AL.Runner/actions/runs?head_sha=<FULL-SHA>&per_page=100" \
  --jq '.workflow_runs[] | "\(.id) \(.name) \(.status) \(.conclusion)"'
```

## How deep the queue is (a count, not a page)

`?per_page=100` caps the array, so counting it counts the page. Ask for the total:

```bash
gh api "repos/<o>/<r>/actions/runs?per_page=1&status=queued" --jq '.total_count'
```

## Did anything on this commit fail on its merits?

Ask before re-running a cancelled required context (`ci-verdicts.md` § "\"Cancelled\" does not
mean \"no log to lose\""):

```bash
gh api "repos/StefanMaron/BusinessCentral.AL.Runner/commits/<FULL-SHA>/check-runs?per_page=100" \
  --jq '.check_runs[] | select(.conclusion=="failure") | "\(.name) \(.id)"'
```

## A failing or cancelled job's log, when `--log-failed` came back empty

`gh run view --log-failed` prints nothing for a cancelled job; the API form refuses a coloured
log unless asked (`ci-verdicts.md` § "An empty log fetch is a refusal, not an empty log"):

```bash
gh api repos/<owner>/<repo>/actions/runs/<run-id>/jobs \
  --jq '.jobs[]|select(.conclusion=="failure" or .conclusion=="cancelled")|"\(.id) \(.name)"'
gh api repos/<owner>/<repo>/actions/jobs/<job-id>/logs --allow-escape-sequences
```

## Whether a `main-verdict-floor.yml` run measured anything

A floor run can succeed with `floor-matrix: skipped` (`ci-verdicts.md` §0). Read the job:

```bash
gh api repos/<o>/<r>/actions/runs/<id>/jobs --jq '.jobs[]|"\(.name): \(.conclusion)"'
```

## Which corpus codeunits a failing leg names

`tools/ci-wait.py` prints this beside the failing log it fetched, matching each codeunit to an
open runner PR:

```
--- failing corpus codeunits (#3922: is this red mine?) ---
  Codeunit60285  x2  <- open runner PR #3996 fixes this
  Codeunit60976  x6  <- open runner PR #3985 fixes this
```

By hand, when you have a log rather than a PR number:

```bash
command grep -E "^\S+Z *FAIL " <leg.log> | command grep -oE "Codeunit[0-9]+" | sort | uniq -c
```

Match per codeunit, never on the total (`ci-verdicts.md` § "A red you inherited from the
corpus is not a flake").

## In the corpus, "which harness code ran" has two dials, and the obvious one is wrong

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
queue backlog. So compare the **job's** start time against the harness commit's timestamp:

```bash
gh api repos/StefanMaron/BusinessCentral.AL.Language.Tests/actions/runs/<id>/jobs \
  --jq '.jobs[] | select(.name|test("/ test")) | "\(.name) started=\(.started_at)"'
gh api repos/StefanMaron/MsDyn365Bc.On.Linux/commits/<sha> --jq .commit.committer.date
```

A job whose `started_at` precedes the commit cannot have checked it out; one that started after
it almost certainly did, with an honest ambiguity window of a few tens of seconds. Getting this
backwards argues for reverting a fix that was never in the run (`MsDyn365Bc.On.Linux#61`;
`docs/incidents/ci-verdicts.md`).
