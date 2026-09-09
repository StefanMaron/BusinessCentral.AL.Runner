# Incidents behind .claude/rules/al-language-submodule.md

Narrative moved verbatim out of the rule (#3728). The rule keeps the instruction, its citation and its trap; this file keeps the incidents that produced them.

## The corpus default branch is `master`, not `main`

This repository's default branch is `main`; the corpus repository's is `master`. Target `master`
when opening a corpus PR. `gh pr create` without an explicit `--base` picks the default correctly,
but a hand-written `--base main`, or an API call that assumes `main`, fails with a 422 that does not
say why — two agents lost time to it on 2026-09-05, one of them concluding the API was broken.

## The pin is `git ls-tree`, not the submodule working directory

**Behind is not mid-bump**, and the tool distinguishes them because getting it wrong put a
wrong pin on stdout. The index carries a gitlink for the submodule at *all* times — normally
just a copy of `HEAD`'s — so "the index differs from `origin/main`" usually means the checkout
is behind, which is the ordinary state of an agent worktree, not that anyone is bumping the
pin. `git diff --cached` is what tells them apart. Only a genuinely **staged** bump outranks
`origin/main`, so `--quiet` on a behind checkout answers `origin/main`'s pin — what CI
replays — rather than that checkout's stale one.

**Why it does not announce itself.** A stale checkout shows as `M tests/al-language`, which
reads as an ordinary dirty submodule; `git log` inside it prints a real history, because it
*is* a real repository at a real commit; and every commit it lists genuinely exists. Nothing
is malformed. The failure is a confident **overestimate** of remaining work — the worse
direction, since it justifies a large measurement run and invites bisecting a range whose
predecessors are already pinned.

Documentation alone did not stop this. The rule was already written when it was violated
three times in one session on 2026-09-07 by three different actors — once producing a third
value belonging to neither end, and once reading a stale pre-rebase value immediately after
an otherwise-correct rebase. Hence the tool and the preflight check.

## What this means in practice

  - **Catch-up** — the fix has already merged upstream and here. A bump alone is green and
    **is** legitimately its own PR. (Practice long before it was written down, which is how
    agents got told the opposite.)
  - **Blocked by an intervening commit** — you cannot pin corpus commit N without pinning its
    predecessors, and one of *those* may need a runner fix that is still open, possibly
    someone else's. Pin the newest commit whose predecessors are all satisfied, leave the
    rest, and name the open issue holding the remainder. Measured 2026-09-06: the corpus tip
    was 15 commits ahead with three predecessors gated on open PRs, and pinning the tip gave
    11 failures.
