# The retired corpus gitlink read as uncommitted work

History for the `worktree_dirt` / `classify_dirt` / `corpus_gitlink_is_workless`
group in `tools/preflight.py`. Issue #3335; fixed alongside #3759.

## What happened

#3737 removed the `tests/al-language` submodule — but only on `main`. A worktree
whose branch was cut before that still carries the `160000` gitlink in its own
`HEAD`, and nothing updates that pin any more. So `git status` prints

```
 M tests/al-language
```

for the rest of that worktree's life, `--reap` reads it as uncommitted work, and
the worktree is kept forever. Before #3737 the remedy was
`git submodule update --init`; after it, there is no submodule left to update, so
the state is permanent rather than transient.

Measured on the box 2026-09-11, five days after #3737 merged: **7 worktrees held
back, every one a MERGED pull request, every one with that single line and
nothing else.** The issue had measured 5 of 10 on 2026-09-07 under the old
mechanism, so the fraction grew rather than shrank after the submodule was
removed.

## The thing that made the obvious fix wrong

The first implementation keyed on the status line plus the index mode, and a test
caught it: **git prints the same line for three different states.** Measured on
git 2.55.0, corpus as a submodule:

| state | superproject `status --porcelain` | submodule's own status |
|---|---|---|
| pin moved, checkout otherwise empty | ` M tests/al-language` | *(empty)* |
| untracked file inside the checkout | ` M tests/al-language` | `?? RreLayout.rdl` |
| both at once | ` M tests/al-language` | `?? RreLayout.rdl` |

`--ignore-submodules=dirty` does not separate them either: it suppresses row 2
entirely, which turns "there is work in there" into "clean" — the wrong direction
for a reaper.

So the superproject line establishes only the *shape*. Whether there is work is a
separate question, answered by descending into the checkout and asking it
directly (`corpus_gitlink_is_workless`). An unreadable or non-empty answer keeps
the worktree.

This was not hypothetical: `corpus-stma-auto-32-issue-2943` was on the box at the
time holding six untracked `.rdl`/`.rdlc` layouts under `tests/al-language`. A
path-keyed rule would have deleted them.

## Why `gitlink_workless` defaults to False

`classify_dirt(status, modes)` without the third argument treats the gitlink as
dirt. A caller that forgets to measure gets the conservative answer rather than
the permissive one — the same reason `agent_self_freshness.py` refuses an
`unvouched` copy (`guards-need-a-third-state.md`).

## End-to-end result

`tools/preflight.py --agent-id stma-auto-11 --reap`, exit code captured directly
into `$?` with no pipe: **rc=0**, 13 worktrees removed, of which 3 were
gitlink-only. Four other gitlink worktrees survived, each for an unrelated
pre-existing reason the fix does not touch — one unpushed commit, three with no
resolvable pull request. The shared `.git/config` was unchanged and
`corpus-stma-auto-32-issue-2943` kept all six of its files.

## `os.getuid` (#3759), fixed in the same file

`check_stale_scratch` called `os.getuid()` unconditionally. It does not exist on
Windows, so the `AttributeError` propagated out of `main()` and **every** check
preflight would otherwise have reported was lost — including `branch-ownership`,
whose job is to stop an agent committing onto another loop's PR branch, and which
is exactly what an agent on a Windows checkout needs. Where there are no uids
there is no foreign-owner case to exclude, so the filter is skipped rather than
faked with a substitute value.
