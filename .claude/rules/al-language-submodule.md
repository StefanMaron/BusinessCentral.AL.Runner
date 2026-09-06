# The `tests/al-language` submodule is read-only

`tests/al-language/` is a git submodule pinned at
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests),
the canonical AL-language test corpus, validated against a real BC service tier. **Never edit
any file under `tests/al-language/`.** The corpus does not know about AL Runner and must stay
that way.

## The corpus default branch is `master`, not `main`

This repository's default branch is `main`; the corpus repository's is `master`. Target `master`
when opening a corpus PR. `gh pr create` without an explicit `--base` picks the default correctly,
but a hand-written `--base main`, or an API call that assumes `main`, fails with a 422 that does not
say why — two agents lost time to it on 2026-09-05, one of them concluding the API was broken.

```bash
gh repo view StefanMaron/BusinessCentral.AL.Language.Tests --json defaultBranchRef \
  --jq '.defaultBranchRef.name'      # master
```

The same asymmetry applies to every command naming a branch: `git merge-tree --write-tree
origin/master origin/<branch>` for a conflict check in the corpus, `origin/main` for one here.

## What this means in practice

- **Test failures in the corpus are runner gaps**, not corpus bugs. `no-assumption-fixes`
  still applies — investigate before patching, but the patch lands in the runner or in
  `tests/expectations/`, never in the corpus.
- **`_fixtures/Assert.al`, table fixtures, helper codeunits — all off-limits.** If
  `Assert.IsNumber` excludes a type and that causes failures, the bug is that the runner
  classifies that type differently from real BC; fix the classification.
- **Updating the corpus** = bumping the submodule pin, together with the
  `tests/expectations/count-baseline/` update. Inspect the diff first:
  `git -C tests/al-language diff $OLD..$NEW`. **The corpus PR is the proof; the pin only
  decides when this repository's CI replays it** — a corpus PR merged with its BC legs green
  means a real service tier has already adjudicated the claim, whether or not our pin has
  caught up. Which PR the bump belongs in depends on what the new commits need:

  - **Fold** — the corpus test and the runner fix are both new. The bump goes **in the fix
    PR**; alone it is red by construction, because the new test fails without the fix.
  - **Catch-up** — the fix has already merged upstream and here. A bump alone is green and
    **is** legitimately its own PR. (Practice long before it was written down, which is how
    agents got told the opposite.)
  - **Blocked by an intervening commit** — you cannot pin corpus commit N without pinning its
    predecessors, and one of *those* may need a runner fix that is still open, possibly
    someone else's. Pin the newest commit whose predecessors are all satisfied, leave the
    rest, and name the open issue holding the remainder. Measured 2026-09-06: the corpus tip
    was 15 commits ahead with three predecessors gated on open PRs, and pinning the tip gave
    11 failures.

## Out-of-scope tests use the expectations manifest

Some corpus tests exercise surfaces the runner cannot support (report rendering, SMTP, HTTP
egress, real task scheduler, …). They pass against real BC and are expected to throw
`RunnerOutOfScopeException` here. Declare those in
[`tests/expectations/`](../../tests/expectations/README.md) per the schema in
[`docs/expectations.md`](../../docs/expectations.md). Four modes:

- `expect-oos` — must raise an out-of-scope signal with a matching reason anchor, either a
  typed `RunnerOutOfScopeException` or the documented `out-of-scope: <api> — <reason>` message
  convention
- `expect-fail-known-gap` — must fail; links to an open GH issue tracking the work
- `expect-divergence` — must fail because the runner *intends* to answer differently from BC;
  carries `Reason` + `Doc`, never an `Issue`
- `skip` — must not run (last resort, for compile gaps)

Manifest drift is loud in both directions: a test that starts passing despite an `expect-oos`
entry fails the run with "remove the entry"; one that starts throwing OOS without an entry
fails with "add an entry".

## Runner-specific positive tests live elsewhere

A test asserting runner-only behaviour (e.g. that a specific surface throws
`RunnerOutOfScopeException` with reason `email-smtp`) goes in `tests/runner-extras/`, not in
the corpus. The converse is a hard rule too: a test asserting plain BC behaviour may **not** be
written as a runner-local test just because that is quicker — it goes upstream so a real
service tier can adjudicate it. See `bc-behavior-tests-go-upstream.md`.

## Sister rules

- `ask-the-corpus-before-claiming-bc-behavior.md` — a corpus test green on real BC is
  evidence; what a known-gap entry may rest on, and never propose inverting a green
  upstream assertion
- `bc-behavior-tests-go-upstream.md` — which repo a new test belongs in, and why
- `precompiled-dll-respect.md` — what we may not rewrite in BC DLLs
- `loud-failures.md` — when to throw `RunnerOutOfScopeException`
- `no-assumption-fixes.md` — investigate before patching
- `file-issues-for-gaps.md` — gaps go to GH issues + expectation entries, never silent workarounds
