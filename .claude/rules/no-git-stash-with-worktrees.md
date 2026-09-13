# Never use `git stash` — the stash is shared across every worktree

`refs/stash` belongs to the **repository**, not to a worktree, so every agent's
`.claude/worktrees/*` directory pushes and pops one stack. One agent's `git stash pop` has
restored another agent's changes into its worktree; nothing in git warns you.

## The rule

Do not run `git stash`, `git stash pop`, `git stash apply`, or `git stash drop` in this
repository — not in a worktree, not in the top-level checkout.

A `PreToolUse` hook refuses every form of it — `.claude/hooks/refuse-stash-and-ci-waits.py`,
`git stash list` included, in every session (#3707).

The workarounds do not help: a pathspec or an `-m` name still writes the shared `refs/stash`,
`git stash list` interleaves every agent's entries, and `stash@{0}` shifts under you. There is
no per-worktree stash.

## What to use instead

| Goal | Use |
|---|---|
| Temporarily revert one file to compare RED vs GREEN | **Commit first**, then `git checkout <rev> -- <path>`. On uncommitted work the restore step **deletes** the change — use the patch row instead, and read the two failure modes below. |
| Set aside changes you will bring back | `git diff HEAD > /tmp/mine.patch`, then `git apply /tmp/mine.patch`. `git diff HEAD`, not `git diff` — plain `git diff` captures **nothing** once the change is staged. Neither captures untracked files; copy those by hand. |
| Keep work safe across a crash or reboot | Commit it on your own branch; nobody else can pop it. |
| Move to another branch with changes in hand | You should not need to — one agent, one worktree, one branch. |
| Check whether your tree is clean, before a rebase or a push | `git status --porcelain` — empty output means clean. **Not the stash-listing command**, which is the pre-rebase reflex the hook refuses: it reports on the SHARED stack rather than on your worktree, so it is both blocked and the wrong instrument for the question. |

Committing early is the preferred answer to all of these.

## `origin/main` is a LOCAL ref with a remote-looking name

`git reset --soft origin/main` resets to whatever your **local** `origin/main` says. If `main`
has moved since your last fetch, every intervening merge is captured in your commit as a
**deletion**, and a force-push offers that as the PR's diff. Measured (#3907): a docs-only
branch whose only intended change was +22/-1 in one markdown file committed
`12 files changed, 60 insertions(+), 1032 deletions(-)` — another PR's entire contribution
staged for deletion — and was force-pushed before anyone noticed.

**The trap is that every ordinary guard is SILENT on this class.** `--force-with-lease` covers
someone else's push to your branch, not your branch's content; `git status --porcelain` is clean
because the deletions are committed; and the same-tree check (`ci-verdicts.md` §5) passes.
`git merge-tree --write-tree` also keeps the other PR's files, so the damage is a wrong *diff*
rather than a merge that reverts anything.

**No dot-count saves you here, and an earlier version of this rule said one did** (#4059). After
`git reset --soft origin/main`, `origin/main` *is* HEAD's parent, so `git merge-base origin/main
HEAD` returns `origin/main` itself and three-dot and two-dot are identical **by construction** —
measured in three scratch repositories covering both stale-ref orderings, merge base and
`origin/main` both `b6ce42df`. The figures the old text cited (`1 file changed` against
`2 files changed, … 50 deletions(-)`) do not reproduce, and the remedy they supported cannot work:
both forms are diffed against the **stale** ref, which is the thing that is wrong.

So the fetch comes first and is not optional: **`git fetch origin main` immediately before any
command naming `origin/main` as a base** — `reset`, `rebase`, `merge-tree`, `diff`. **Against a
stale ref no dot-count helps**; against a fresh one the dots diverge and **two dots is the form
that shows the damage** — the fetch moves `origin/main` forward while the merge base stays at the
commit the soft reset used, so three-dot still prints a clean `1 file changed, 1 insertion(+)`
while two-dot prints `2 files changed, 1 insertion(+), 50 deletions(-)`. Measured both ways, in
the same scratch repository, before and after the fetch.

Read `git diff --stat origin/main..HEAD` **after** fetching, and read the commit itself
(`git show --stat HEAD`) before pushing a rewritten branch — it is the only view that does not
depend on a ref being current at all.

## The RED-baseline recipe has two ways to destroy work

Both are properties of `git checkout` (git 2.55.0). `git checkout HEAD -- <path>` with your fix
uncommitted **deletes the fix**, with no stash or reflog entry. And `git checkout <rev> --
<paths>` writes the **index**, so those paths stay staged as pre-fix content and a later
`git commit` commits the revert — silent, and it ships code CI never measured.

So: copy the affected files outside the repository first, `git add` them again after restoring,
read `git diff --cached` to confirm it is your fix rather than the revert, and never
`git commit -a` straight after a RED baseline. Worked modes: the incidents file.

## Polling loops must not match themselves

`pgrep -f <pattern>` matches the polling shell's own command line **and every ancestor**
carrying the pattern, so `while pgrep -f "dotnet run"; do ...; done` never terminates, and
filtering `$$` out does not rescue it (why not: the incidents file). Use `$!` or `wait`.

## Sister rules

- `branch-and-pr.md` — one branch per agent, one open PR per agent
- `tdd.md` — the RED → GREEN cycle the revert recipe serves
- `no-backgrounding-long-commands.md` — wait in the foreground locally; never wait on CI

History: docs/incidents/no-git-stash-with-worktrees.md
