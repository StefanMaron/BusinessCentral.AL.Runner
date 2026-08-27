# CI verdicts: three recurring mistakes

## A verdict is about one commit, not one PR

`gh pr checks` reports the newest *completed* run, which can predate your last
push. Reporting green from a stale run has happened at least four times.
Confirm the check's commit SHA matches local `HEAD` before trusting it — a
mismatch means "not yet reported," not "green." The one required check on
`main` is **`All BC versions passed`** (`.github/workflows/test-matrix.yml`);
matrix legs report as `bc-tests / BC <ver> (required)`.

## Never re-run a failed job

`gh run rerun` and the web "Re-run" button overwrite the failed run's logs
permanently. Read the log first (`gh run view <id> --log-failed`, or
`mcp__github__get_job_logs` with `failed_only: true, return_content: true`),
save what you need, then push a new commit for a fresh run. A re-run is never
a diagnostic step — it destroys the evidence a diagnosis needs.

## "Pre-existing unrelated flake" needs evidence

Dismissed twice without checking; both times it was real and both blocked a
release. Require one of: the same failure reproducing on `origin/main` at a
commit predating the branch; a *changing failing-leg set* across repeated runs
of the same commit (load-dependent, not the commit); or an existing issue
describing that exact failure. `mergeStateStatus: CLEAN` only checks textual
conflicts — it says nothing about whether CI ran on your current head.
