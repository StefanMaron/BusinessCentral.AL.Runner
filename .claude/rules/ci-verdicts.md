# Driving a PR through CI

"PR opened" is not the deliverable; "PR merged" is. After opening a PR, keep
driving it — fix CI failures and address review comments yourself, don't wait
for someone else to notice a PR is red.

The steps below are in the order you actually need them. Each one records a
mistake that has been made here more than once.

## 1. Check for merge conflicts first

```bash
gh pr view <N> --json mergeStateStatus --repo <owner>/<repo>
```

`DIRTY` / `CONFLICTING` → rebase on the base branch, resolve, force-push with
`--force-with-lease`, re-check until it reads `BLOCKED` or `CLEAN`.

**CI will not run on a PR with conflicts.** Check this before investigating any
CI problem — "no checks reported" almost always means merge conflicts, not a CI
outage.

`mergeStateStatus: CLEAN` only checks *textual* conflicts. It says nothing about
whether CI ran on your current head, and nothing about semantic conflicts — a
clean merge can still break because `main` moved underneath you.

## 2. A verdict is about one commit, not one PR

`gh pr checks` reports the newest *completed* run, which can predate your last
push. Reporting green from a stale run has happened at least four times.

Confirm the check's commit SHA matches local `HEAD` before trusting it — a
mismatch means "not yet reported," not "green." Never report a PR as done while
its CI is still running.

The one required check on `main` is **`All BC versions passed`**
(`.github/workflows/test-matrix.yml`); matrix legs report as
`bc-tests / BC <ver> (required)`.

## 3. Never re-run a failed job

`gh run rerun` and the web "Re-run" button overwrite the failed run's logs
permanently. Read the log first (`gh run view <id> --log-failed`, or
`mcp__github__get_job_logs` with `failed_only: true, return_content: true`),
save what you need, then push a new commit for a fresh run.

A re-run is never a diagnostic step — it destroys the evidence a diagnosis
needs.

## 4. Diagnose from the log, not from a theory

Wait for the run to complete before reading it — a partial log reads as an
unrelated failure. Then find the actual failing assertion. A theory formed
before the log arrives has been wrong every time it was tried here.

## 5. "Pre-existing unrelated flake" needs evidence

Dismissed twice without checking; both times it was real and both blocked a
release. Require one of:

- the same failure reproducing on `origin/main` at a commit predating the branch;
- a *changing failing-leg set* across repeated runs of the same commit
  (load-dependent, not the commit);
- an existing issue describing that exact failure.

## Sister rules

- `no-backgrounding-long-commands.md` — how to wait on anything long-running
- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee boundary
