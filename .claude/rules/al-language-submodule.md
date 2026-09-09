# The `tests/al-language` submodule is read-only

`tests/al-language/` is a git submodule pinned at
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests),
the canonical AL-language test corpus, validated against a real BC service tier. **Never edit
any file under `tests/al-language/`.** The corpus does not know about AL Runner and must stay
that way.

## The corpus default branch is `master`, not `main`

Target `master` on a corpus PR. `gh pr create` with no explicit `--base` picks it up correctly;
a hand-written `--base main`, or an API call assuming `main`, fails with a 422 that does not say
why. The same asymmetry applies to every command naming a branch — `git merge-tree --write-tree
origin/master origin/<branch>` for a conflict check in the corpus, `origin/main` for one here.

```bash
gh repo view StefanMaron/BusinessCentral.AL.Language.Tests --json defaultBranchRef \
  --jq '.defaultBranchRef.name'      # master
```

## The pin is `git ls-tree`, not the submodule working directory

Two plausible commands, both exit 0, both print a real SHA, and they answer different questions:

```bash
git ls-tree origin/main tests/al-language     # THE PIN — what CI replays
git -C tests/al-language rev-parse HEAD       # whatever the last process left checked out
```

**Use the tool; do not choose between them** (#3404):

```bash
tools/corpus-pin.py                  # all three readings, labelled; exit 1 if drifted
PIN=$(tools/corpus-pin.py --quiet)   # always the pin, never the shared checkout
```

`--quiet` prints the pin even when it exits 1, so a caller that ignores the exit code still
captures the right value; `tools/preflight.py` reports the same divergence as a WARN.

`tests/al-language/` is a **submodule working directory shared by every worktree of this
repository** — like `refs/stash` (`no-git-stash-with-worktrees.md`). Any process that checks a
different corpus commit out inside it leaves it there for everyone, and nothing resets it.

**Trap: behind is not mid-bump.** The index carries a gitlink at all times, so "the index
differs from `origin/main`" usually means the checkout is behind — the ordinary state of an
agent worktree — and only a genuinely **staged** bump (`git diff --cached`) outranks
`origin/main`. A stale checkout announces nothing: it shows as `M tests/al-language`, and `git
log` inside it prints a real history at a real commit. What it produces is a confident
*overestimate* of remaining work, which justifies a large measurement run against commits that
are already pinned (#3404).

**To measure against the corpus, give your worktree its own clone.** A `git checkout` inside
`tests/al-language/` poisons it for every other worktree, so it belongs only in a bump, where
the `git add` that follows makes the checkout and the pin agree again.

## What this means in practice

- **Test failures in the corpus are runner gaps**, not corpus bugs. `no-assumption-fixes.md`
  still applies — investigate before patching, but the patch lands in the runner or in
  `tests/expectations/`, never in the corpus.
- **`_fixtures/Assert.al`, table fixtures, helper codeunits — all off-limits.** If
  `Assert.IsNumber` excludes a type and that causes failures, the bug is that the runner
  classifies that type differently from real BC; fix the classification.
- **Updating the corpus** = bumping the submodule pin together with the
  `tests/expectations/count-baseline/` update. Inspect the diff first —
  `git -C tests/al-language diff $OLD..$NEW`, where `$OLD` is the pin above and **not** the
  submodule's `HEAD`. **The corpus PR is the proof; the pin only decides when this repository's
  CI replays it**, so a corpus PR merged with green BC legs has already been adjudicated by a
  real service tier whether or not our pin has caught up. Which PR the bump belongs in depends
  on what the new commits need:

  - **Fold** — the corpus test and the runner fix are both new. The bump goes **in the fix
    PR**; alone it is red by construction, because the new test fails without the fix.
  - **Catch-up** — the fix has already merged upstream and here. A bump alone is green and is
    legitimately its own PR.
  - **Blocked by an intervening commit** — you cannot pin corpus commit N without pinning its
    predecessors, and one of *those* may need a runner fix that is still open, possibly someone
    else's. Pin the newest commit whose predecessors are all satisfied, leave the rest, and name
    the open issue holding the remainder.

## Out-of-scope tests use the expectations manifest

Some corpus tests exercise surfaces the runner cannot support (report rendering, SMTP, HTTP
egress, real task scheduler, …). They pass against real BC and are expected to raise an
out-of-scope signal here (`loud-failures.md`). Declare those in
[`tests/expectations/`](../../tests/expectations/README.md) in one of the four modes —
`expect-oos`, `expect-fail-known-gap`, `expect-divergence`, `skip` — whose exact meaning and
required fields live in [`docs/expectations.md`](../../docs/expectations.md).

Manifest drift is loud in both directions: a test that starts passing despite an `expect-oos`
entry fails the run with "remove the entry"; one that starts throwing OOS without an entry
fails with "add an entry".

## Runner-specific positive tests live elsewhere

A test asserting runner-only behaviour (for example that a specific surface throws
`RunnerOutOfScopeException` with reason `email-smtp`) goes in `tests/runner-extras/`, not in the
corpus. The converse is a hard rule too: a test asserting plain BC behaviour may **not** be
written as a runner-local test because that is quicker (`bc-behavior-tests-go-upstream.md`).

## Sister rules

- `ask-the-corpus-before-claiming-bc-behavior.md` — a corpus test green on real BC is
  evidence; what a known-gap entry may rest on, and never propose inverting a green
  upstream assertion
- `bc-behavior-tests-go-upstream.md` — which repo a new test belongs in, and why
- `precompiled-dll-respect.md` — what we may not rewrite in BC DLLs
- `loud-failures.md` — when to throw `RunnerOutOfScopeException`
- `no-assumption-fixes.md` — investigate before patching
- `file-issues-for-gaps.md` — gaps go to GH issues + expectation entries, never silent workarounds

History: docs/incidents/al-language-submodule.md
