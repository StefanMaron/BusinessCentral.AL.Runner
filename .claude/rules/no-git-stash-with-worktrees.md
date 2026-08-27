# Never use `git stash` — the stash is shared across every worktree

`refs/stash` belongs to the **repository**, not to a worktree. Every
`.claude/worktrees/impl-*` directory is a worktree of the same repository, so
`git stash` and `git stash pop` in one agent's worktree operate on the same
single stack every other agent is using.

**This has already happened.** On 2026-08-27 two impl agents stashed
concurrently while working different issues. One agent's `git stash pop`
restored the *other* agent's changes into its own worktree, and a fix landed in
a worktree that had nothing to do with it. It was recovered — the diff was
extracted, the wrong worktree restored, and the change reapplied and verified
byte-for-byte — but only because the agent noticed. Nothing in git warns you.

## The rule

Do not run `git stash`, `git stash pop`, `git stash apply`, or `git stash drop`
in this repository. Not in a worktree, not in the top-level checkout.

## What to use instead

| Goal | Use |
|---|---|
| Temporarily revert one file to compare RED vs GREEN | `git checkout <rev> -- <path>`, then `git checkout HEAD -- <path>` to restore |
| Set aside changes you will bring back | `git diff > /tmp/mine.patch`, then `git apply /tmp/mine.patch` |
| Keep work safe across a crash or reboot | Commit it on your own branch. A commit on an agent branch is free and cannot be popped by anyone else. |
| Move to another branch with changes in hand | You should not need to — each agent owns one worktree on one branch. |

Committing early is the preferred answer to all of these. An agent's branch is
its own, a reboot cannot take a commit, and no other agent can touch it.

## Why the obvious workarounds do not help

`git stash push --` with a pathspec, or naming a stash with `-m`, still writes
to the same shared `refs/stash`. `git stash list` shows every agent's entries
interleaved with yours, and index positions (`stash@{0}`) shift under you when
another agent pushes. There is no per-worktree stash.

## Polling loops must not match themselves

`pgrep -f <pattern>` matches the polling shell's own command line, so
`while pgrep -f "dotnet run"; do ...; done` never terminates. This has hung an agent
turn. Use `$!` on a job you started, `wait`, or `pgrep -f <pat> | grep -v $$`.
Better: don't poll — run it in the foreground.

## Sister rules

- `branch-and-pr.md` — one branch per agent, one open PR per agent
