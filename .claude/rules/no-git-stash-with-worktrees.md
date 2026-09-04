# Never use `git stash` — the stash is shared across every worktree

`refs/stash` belongs to the **repository**, not to a worktree. Every
`.claude/worktrees/impl-*` directory is a worktree of the same repository, so `git stash`
and `git stash pop` in one agent's worktree operate on the same single stack every other
agent is using. On 2026-08-27 two impl agents stashed concurrently while working different
issues; one agent's `git stash pop` restored the *other* agent's changes into its own
worktree, and a fix landed in a worktree that had nothing to do with it. Recovered only
because the agent noticed. Nothing in git warns you.

## The rule

Do not run `git stash`, `git stash pop`, `git stash apply`, or `git stash drop` in this
repository. Not in a worktree, not in the top-level checkout.

The obvious workarounds do not help: `git stash push --` with a pathspec, or naming a stash
with `-m`, still writes to the same shared `refs/stash`; `git stash list` interleaves every
agent's entries with yours, and `stash@{0}` shifts under you when another agent pushes.
There is no per-worktree stash.

## What to use instead

| Goal | Use |
|---|---|
| Temporarily revert one file to compare RED vs GREEN | **Commit first.** Then `git checkout <rev> -- <path>`, and `git checkout HEAD -- <path>` to restore. On uncommitted work the restore step **deletes** the change — use the patch row instead. Read the two failure modes below before running either half. |
| Set aside changes you will bring back | `git diff HEAD > /tmp/mine.patch`, then `git apply /tmp/mine.patch`. `git diff HEAD`, not `git diff` — plain `git diff` captures **nothing** once the change is staged. Neither captures untracked files; copy those by hand. |
| Keep work safe across a crash or reboot | Commit it on your own branch. Free, and nobody else can pop it. |
| Move to another branch with changes in hand | You should not need to — each agent owns one worktree on one branch. |

Committing early is the preferred answer to all of these.

## The RED-baseline recipe has two ways to destroy work

Both hit real agents within a single day, following the first table row literally. Measured
on git 2.55.0, not inferred.

1. **The restore step is not a restore.** `git checkout HEAD -- <path>` sets the file to
   whatever `HEAD` says. With your fix still uncommitted, `HEAD` is the state *without* it,
   so the command throws your work away. No stash entry and no reflog entry to recover from.
2. **`git checkout <rev> -- <paths>` writes the index, not just the working tree.** Those
   paths end up **staged** as pre-fix content: restore the working tree from a copy and
   `git status` reads `MM`, restore only some and the rest read `M `. `git commit` then
   commits the **index** — the revert — for every path you reverted; `git commit -a` commits
   the **working tree**, right for paths you restored and still pre-fix for any you did not.

Mode 1 announces itself: the tests stop passing. Mode 2 is silent — it produces a PR carrying
a green CI verdict for code that is not what CI measured.

So, around any `git checkout <rev> -- <paths>`:

1. **Copy the affected files outside the repository before you revert** —
   `mkdir -p /tmp/red-baseline && cp <paths> /tmp/red-baseline/` — so the restore is
   verifiable instead of hopeful.
2. Restore, then `git add` those paths again; the revert staged them and the restore does not
   necessarily unstage them.
3. Read `git diff --cached` and confirm it is your fix, not the revert.
4. Never `git commit -a` straight after a RED baseline.

## Polling loops must not match themselves

`pgrep -f <pattern>` matches the polling shell's own command line, so
`while pgrep -f "dotnet run"; do ...; done` never terminates — this has hung an agent turn.
Filtering the shell's own PID out does **not** rescue it, and this rule used to recommend it:
measured, `pgrep -f <pat>` also matches every *ancestor* whose command line contains the
pattern, including the outer tool shell that ran your command, so `pgrep -f <pat> | grep -v $$`
still matches and the loop still spins. `grep -v $$` is a substring filter besides — with `$$`
of `123` it also drops PIDs `1234` and `4123`; `grep -vx` fixes that half and not the ancestor
half. Use `$!` on a job you started, or `wait`. Better: don't poll, run it in the foreground.

## Sister rules

- `branch-and-pr.md` — one branch per agent, one open PR per agent
- `tdd.md` — the RED → GREEN cycle the revert recipe above exists to serve
- `no-backgrounding-long-commands.md` — why the answer to "is it done yet" is a
  foreground wait, not a polling loop
