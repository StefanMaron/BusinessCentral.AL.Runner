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
| Temporarily revert one file to compare RED vs GREEN | **Commit your work first.** Then `git checkout <rev> -- <path>`, and `git checkout HEAD -- <path>` to restore. On uncommitted work the restore step **deletes** the change instead of restoring it — if you do not want to commit yet, use the patch row below instead. Read the two failure modes below before you run either half. |
| Set aside changes you will bring back | `git diff HEAD > /tmp/mine.patch`, then `git apply /tmp/mine.patch`. Use `git diff HEAD`, not `git diff` — plain `git diff` captures **nothing** once the change is staged. Neither form captures untracked files; copy those by hand. |
| Keep work safe across a crash or reboot | Commit it on your own branch. A commit on an agent branch is free and cannot be popped by anyone else. |
| Move to another branch with changes in hand | You should not need to — each agent owns one worktree on one branch. |

Committing early is the preferred answer to all of these. An agent's branch is
its own, a reboot cannot take a commit, and no other agent can touch it.

## The RED-baseline recipe has two ways to destroy work

Both were hit by real agents within a single day, following the first table row
literally. The behaviour below is measured on git 2.55.0, not inferred.

**1. The restore step is not a restore.** `git checkout HEAD -- <path>` sets the
file to whatever `HEAD` says. If your fix is still uncommitted, `HEAD` is the
state *without* the fix — so the command does not give you your work back, it
throws it away. There is no stash entry and no reflog entry to recover from.
Commit first, or use the `git diff HEAD` / `git apply` row instead.

**2. `git checkout <rev> -- <paths>` writes the index, not just the working
tree.** After the revert those paths are **staged** as the pre-fix content.
Restore the working tree from a copy and `git status` reads `MM`; restore only
some of the paths and the rest read `M `. Commit from that state and:

- `git commit` commits the **index** — the reverted, pre-fix content, for every
  path you reverted.
- `git commit -a` commits the **working tree** — right for the paths you
  restored, still pre-fix for any you did not.

Both are silent. Mode 1 announces itself, because the tests stop passing and you
notice. Mode 2 does not: it produces a PR carrying a green CI verdict for code
that is not what CI measured.

So, around any `git checkout <rev> -- <paths>`:

1. **Copy the affected files outside the repository before you revert** —
   `mkdir -p /tmp/red-baseline && cp <paths> /tmp/red-baseline/`. This is what
   makes the restore verifiable instead of hopeful; you can diff against it
   afterwards.
2. Restore, then `git add` those paths again — the revert staged them and the
   restore does not necessarily unstage them.
3. Read `git diff --cached` and confirm it is your fix, not the revert, before
   you commit.
4. Never `git commit -a` straight after a RED baseline.

## Why the obvious workarounds do not help

`git stash push --` with a pathspec, or naming a stash with `-m`, still writes
to the same shared `refs/stash`. `git stash list` shows every agent's entries
interleaved with yours, and index positions (`stash@{0}`) shift under you when
another agent pushes. There is no per-worktree stash.

## Polling loops must not match themselves

`pgrep -f <pattern>` matches the polling shell's own command line, so
`while pgrep -f "dotnet run"; do ...; done` never terminates. This has hung an agent
turn. Use `$!` on a job you started, or `wait`.

Filtering the shell's own PID back out does **not** rescue the loop, and this rule
used to recommend it. Measured: `pgrep -f <pat>` also matches every *ancestor*
whose command line contains the pattern — including the outer tool shell that ran
your command — so `pgrep -f <pat> | grep -v $$` still returns a match and the loop
still spins. `grep -v $$` is a substring filter besides, so with `$$` of `123` it
also drops PIDs `1234` and `4123`; `grep -vx` fixes that half and not the ancestor
half. Better: don't poll — run it in the foreground.

## Sister rules

- `branch-and-pr.md` — one branch per agent, one open PR per agent
- `tdd.md` — the RED → GREEN cycle the revert recipe above exists to serve
- `no-backgrounding-long-commands.md` — why the answer to "is it done yet" is a
  foreground wait, not a polling loop
