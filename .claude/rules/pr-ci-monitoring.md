# Monitor a PR through to merge — don't stop at "opened"

"PR opened" is not the deliverable; "PR merged" is. After opening a PR, keep
driving it — do not stop or assume "done" just because you pushed. Fix CI
failures and address review comments yourself; don't wait for someone else to
notice a PR is red.

## Check for merge conflicts first

```
gh pr view <N> --json mergeStateStatus --repo <owner>/<repo>
```

If `mergeStateStatus` is `DIRTY` or `CONFLICTING`: rebase on the base branch,
resolve conflicts, force-push (`--force-with-lease`), then re-check —
it must read `BLOCKED` or `CLEAN`.

**CI will not run on a PR with conflicts.** Always check this before
investigating a CI problem — "no checks reported" almost always means
merge conflicts, not a CI outage.

## Check CI status

```
gh pr checks <N> --repo <owner>/<repo>
```

- "no checks reported" → check `mergeStateStatus` first (see above).
- CI failing → read the job log, fix the cause, push a new commit. See
  `ci-verdicts.md` for the rules on reading a verdict correctly and never
  re-running a failed job.
- CI green → confirm the green check's commit SHA matches your current
  `HEAD` (see `ci-verdicts.md`) before reporting done.

## Sister rules

- `ci-verdicts.md` — per-commit verdicts, never re-run a failed job, evidence
  for "unrelated flake"
- `no-backgrounding-long-commands.md` — how to wait on anything long-running
