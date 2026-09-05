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
| 1 | a required check failed; **the failing log is already printed** |
| 2 | timed out while still running — **not a verdict**, call again |
| 3 | could not determine (auth, network, no checks) |
| 4 | **blocked, not failing** — every check passed but a *required* context is `cancelled` on this commit, so the merge is refused and nothing else says why (#2726). The one case where `gh run rerun` is correct: re-run the cancelled run, it has no failure log to destroy. Never reach for `--admin`. |

It enforces the two rules agents keep getting wrong: checks are matched against the PR's
**current head SHA**, so a newer completed run for an older push is never reported as this
push's result; and on failure it fetches `--log-failed` for you, so there is never a reason
to reach for `gh run rerun`, which destroys the log permanently.

Measured across one session's 17 subagents, CI waiting was 328 of 3,282 Bash calls, and the
shape was wrong — 107 `gh run view` polls and 37 `sleep` loops against only 29 blocking
`gh run watch` calls. Each poll re-sends the whole conversation; this turns ten-to-forty
round trips into one.

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

The one required check on `main` is **`All BC versions passed`**
(`.github/workflows/test-matrix.yml`); matrix legs report as `bc-tests / BC <ver> (required)`.

Wait in the **foreground** — `tools/ci-wait.py`, or `gh run watch <run-id>`. Never end a turn
while CI you are responsible for is still running (`no-backgrounding-long-commands.md`).
Re-check with `gh run view <id> --json status,conclusion` and treat anything other than
`completed` as "not yet reported".

## 3. Never re-run a failed job

`gh run rerun` and the web "Re-run" button overwrite the failed run's logs permanently. Read
the log first (`gh run view <id> --log-failed`, or `mcp__github__get_job_logs` with
`failed_only: true, return_content: true`), save what you need, then push a new commit for a
fresh run. A re-run is never a diagnostic step — it destroys the evidence a diagnosis needs.

## 4. Diagnose from the log, not from a theory

Wait for the run to complete before reading it — a partial log reads as an unrelated failure.
Then find the actual failing assertion. A theory formed before the log arrives has been wrong
every time it was tried here.

## 5. "Pre-existing unrelated flake" needs evidence

Dismissed twice without checking; both times it was real and both blocked a release. Require
one of:

- the same failure reproducing on `origin/main` at a commit predating the branch;
- a *changing failing-leg set* across repeated runs of the same commit (load-dependent, not
  the commit);
- an existing issue describing that exact failure.

## Sister rules

- `no-backgrounding-long-commands.md` — how to wait on anything long-running
- `branch-and-pr.md` — branch naming, `Closes #N`, the assignee boundary
