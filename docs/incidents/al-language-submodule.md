# Incidents behind .claude/rules/al-language-submodule.md

**The pin these incidents are about no longer exists.** `tests/al-language` stopped being a
submodule and a gitlink at #3737: the corpus is resolved per run, at `master` or at the head of
the corpus pull request a PR body names, and each run prints the SHA it resolved. Everything
below is kept as the record of why the pin machinery was built and what it cost — a reader
finding `tools/corpus-pin.py` in the history needs it — not as instructions.

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

## A corpus PR merged ahead of its pair, found by the wrong question (2026-09-14, #4168)

I merged corpus PR #350 while runner PR #4141, its pair, was open. `main` gained codeunit 60984
asserting `IsSandbox()` implies `IsSaaS()`, against a runner that forces `IsSandbox()` true
(`MetadataPatches.cs:79`, `SetTestTenantEnvironmentType(true)`) while `isSaaSConfig` stays false.
#4141 removes that call; without it the assertion cannot hold.

**The check I ran was clean and answered a different question.** Before merging I confirmed no
open runner PR carried a `Corpus-PR:` line naming #350 — true, and irrelevant: #4141 declares
`Corpus-NA:` ("no corpus literal can pin it"), so it names no corpus PR anywhere. The pair was
real and invisible to a citation-keyed search. #350's own body names runner issue #3514, which
#4141 closes; the link existed in the other direction the whole time.

**Why this shape recurs rather than being a one-off.** `Corpus-NA:` is written precisely when an
author believes no corpus test can pin the behaviour. That belief is the one most likely to be
overturned later by someone writing such a test — and when they do, the runner PR still carries
the declaration saying no corpus PR exists. So the population of `Corpus-NA:` runner PRs is
enriched for exactly the pairs a citation search cannot see.

#350's four tests were sound and are worth having: every assertion is about agreement between
predicates rather than a literal, which is why they are green on all eight cloud legs and on the
Windows nightly. The defect was the ordering, not the tests.

Cost: `main` acquires a fourth failing suite on top of the three tracked by #4167, and clears
only when #4141 lands.
