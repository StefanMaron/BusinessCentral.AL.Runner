# Never use `git stash` — the stash is shared across every worktree

`refs/stash` belongs to the **repository**, not to a worktree, so every agent's
`.claude/worktrees/*` directory pushes and pops the same single stack. One agent's
`git stash pop` has restored another agent's changes into its own worktree, and nothing in git
warns you.

## The rule

Do not run `git stash`, `git stash pop`, `git stash apply`, or `git stash drop` in this
repository — not in a worktree, not in the top-level checkout.

The obvious workarounds do not help: `git stash push --` with a pathspec and `-m` names still
write to the shared `refs/stash`, `git stash list` interleaves every agent's entries with yours,
and `stash@{0}` shifts under you when another agent pushes. There is no per-worktree stash.

## What to use instead

| Goal | Use |
|---|---|
| Temporarily revert one file to compare RED vs GREEN | **Commit first.** Then `git checkout <rev> -- <path>`, and `git checkout HEAD -- <path>` to restore. On uncommitted work the restore step **deletes** the change — use the patch row instead. Read the two failure modes below before running either half. |
| Set aside changes you will bring back | `git diff HEAD > /tmp/mine.patch`, then `git apply /tmp/mine.patch`. `git diff HEAD`, not `git diff` — plain `git diff` captures **nothing** once the change is staged. Neither captures untracked files; copy those by hand. |
| Keep work safe across a crash or reboot | Commit it on your own branch. Free, and nobody else can pop it. |
| Move to another branch with changes in hand | You should not need to — each agent owns one worktree on one branch. |

Committing early is the preferred answer to all of these.

## The RED-baseline recipe has two ways to destroy work

Both are properties of `git checkout` (measured on git 2.55.0), and mode 2 is silent — it
produces a PR carrying a green CI verdict for code that is not what CI measured:

1. **The restore step is not a restore.** `git checkout HEAD -- <path>` sets the file to
   whatever `HEAD` says, so with your fix still uncommitted it throws the fix away, with no
   stash entry and no reflog entry to recover from.
2. **`git checkout <rev> -- <paths>` writes the index, not just the working tree**, leaving
   those paths **staged** as pre-fix content. Read `git status`: a path you restored from a copy
   reads `MM`, one you did not reads `M `. `git commit` then commits the index — the revert —
   and `git commit -a` commits the working tree, right for paths you restored and still pre-fix
   for any you did not.

So, around any `git checkout <rev> -- <paths>`:

1. **Copy the affected files outside the repository before you revert** —
   `mkdir -p /tmp/red-baseline && cp <paths> /tmp/red-baseline/` — so the restore is verifiable
   instead of hopeful.
2. Restore, then `git add` those paths again; the revert staged them and the restore does not
   necessarily unstage them.
3. Read `git diff --cached` and confirm it is your fix, not the revert.
4. Never `git commit -a` straight after a RED baseline.

## Polling loops must not match themselves

`pgrep -f <pattern>` matches the polling shell's own command line **and every ancestor** whose
command line contains the pattern, including the outer tool shell, so
`while pgrep -f "dotnet run"; do ...; done` never terminates. Filtering `$$` out does not rescue
it, and fails twice over: `grep -v $$` is a substring filter, so with a PID of `123` it also
drops `1234` and `4123`, and `grep -vx` fixes that half while leaving the ancestor half. Use
`$!` on a job you started, or `wait`. Whether to wait at all is
`no-backgrounding-long-commands.md`'s call, not this rule's.

## Sister rules

- `branch-and-pr.md` — one branch per agent, one open PR per agent
- `tdd.md` — the RED → GREEN cycle the revert recipe above exists to serve
- `no-backgrounding-long-commands.md` — why the answer to "is it done yet" is a foreground
  wait for local work, and for CI is not to wait at all

History: docs/incidents/no-git-stash-with-worktrees.md
