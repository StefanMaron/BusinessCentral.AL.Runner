# Incidents behind .claude/rules/no-git-stash-with-worktrees.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## Never use `git stash` — the stash is shared across every worktree

`refs/stash` belongs to the **repository**, not to a worktree. Every
`.claude/worktrees/impl-*` directory is a worktree of the same repository, so `git stash`
and `git stash pop` in one agent's worktree operate on the same single stack every other
agent is using. On 2026-08-27 two impl agents stashed concurrently while working different
issues; one agent's `git stash pop` restored the *other* agent's changes into its own
worktree, and a fix landed in a worktree that had nothing to do with it. Recovered only
because the agent noticed. Nothing in git warns you.

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

## Polling loops must not match themselves

`pgrep -f <pattern>` matches the polling shell's own command line, so
`while pgrep -f "dotnet run"; do ...; done` never terminates — this has hung an agent turn.
Filtering the shell's own PID out does **not** rescue it, and this rule used to recommend it:
measured, `pgrep -f <pat>` also matches every *ancestor* whose command line contains the
pattern, including the outer tool shell that ran your command, so `pgrep -f <pat> | grep -v $$`
still matches and the loop still spins. `grep -v $$` is a substring filter besides — with `$$`
of `123` it also drops PIDs `1234` and `4123`; `grep -vx` fixes that half and not the ancestor
half. Use `$!` on a job you started, or `wait`. Better: don't poll, run it in the foreground.

## `git reset --soft origin/main` against a stale ref (#3907)

A coordinator was reworking a docs-only branch so its **commit message** would satisfy the
closing-reference gate — the gate reads commit messages as well as the PR body, so rewording
required rebuilding the commit. It ran `git reset --soft origin/main` and committed.

PR #3902 had merged as `d9c6f8f0` minutes earlier. The local `origin/main` predated it, so the
reset captured that merge's whole contribution as deletions:

```
12 files changed, 60 insertions(+), 1032 deletions(-)
  AlRunner/Infrastructure/EngineClosure.cs           |  74 -----
  AlRunner.Tests/PlatformAppsEntryGuardTests.cs      | 343 ---------------------
  docs/provisioning.md                               | 149 ---------
```

on a branch whose only intended change was +22/-1 in one markdown file. It was force-pushed
before being noticed, and caught only because `--stat` happened to be in the terminal afterwards.

**Why the ordinary guards all passed.** `--force-with-lease` protects against someone else's
push to your branch, and nobody had pushed — the branch content was the problem, not a race.
`git status` was clean, because the deletions were committed rather than sitting in the working
tree. And the same-tree check (`ci-verdicts.md` §5) passed.

**A correction to the first version of this account**, which claimed preserving the old tree *is*
the revert and that the same-tree check therefore confirms the defect. A reviewer built four
scratch repositories (both stale-ref orderings × `--soft`/`--hard`) and could not reproduce a
merge that reverts: `git merge-tree --write-tree` kept the other PR's files in all four. So the
guards are **silent** on this class, not confirming — a milder and more defensible claim, and the
one the evidence supports. The damage to the *diff* was real and is documented above; the claim
about what would have landed on merge was not measured and should not have been stated.

**The remedy line was also wrong**, and this is the more useful finding: the first version said to
read `git diff --stat origin/main...HEAD`. Three dots diffs against the **merge base**, and a soft
reset moves the merge base back with it, so re-added content reads as insertions and the command
prints a clean `1 file changed`. Measured in the same four repositories and reproduced
independently afterwards:

```
three-dot:  1 file changed, 1 insertion(+)
two-dot:    2 files changed, 1 insertion(+), 50 deletions(-)
```

**Use two dots.** A rule's recipe is prose that no CI job executes — a docs-only PR is green by
construction — so a recipe written from a correct memory of the *incident* can carry a command
that does not detect it. Nothing here forces a rule's recipe to be run once, the way `tdd.md`
forces a mutation.

CI would probably have caught it, but as a red leg on a docs-only PR — where the natural reading
is "unrelated flake", not "this PR deletes a merged feature".

**What generalises:** `origin/main` is local state wearing a remote-looking name. Every
command that treats it as authoritative — `reset`, `rebase`, `merge-tree`, `diff origin/main...`
— inherits however stale it is, silently, and the staleness window is exactly as long as the
interval since the last fetch.
