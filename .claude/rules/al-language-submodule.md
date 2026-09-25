# The `tests/al-language` corpus is read-only, and resolved rather than pinned

`tests/al-language/` holds
[`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests),
the canonical AL-language test corpus, validated against a real BC service tier. **Never edit
any file under `tests/al-language/`.** The corpus does not know about AL Runner and must stay
that way.

It is **not a submodule and not a gitlink** since #3737: `git ls-tree origin/main
tests/al-language` answers nothing, the directory is gitignored, and every run resolves the
corpus for itself.

## Which corpus a run measures

| where | resolves | how |
|---|---|---|
| a pull request with no `Corpus-PR:` line | `master` | `.github/actions/resolve-corpus-ref` |
| a pull request declaring `Corpus-PR: …/pull/<M>` | that PR's branch head while it is open; `master` once it has merged | the same action, which asks the corpus PR its state |
| a push to `main`, the floor, a release | `master` | the same |
| your worktree | whatever you last checked out | `tools/corpus-checkout.py` |

**Every run prints `corpus: <full sha> (<ref>)`** — in the job log, in the run summary, and
locally from `tools/corpus-checkout.py` (`--print` reads it back without a network call).
That line is the whole record: with nothing pinned in the tree, a result nobody can attribute
to a corpus commit is a result nobody can reproduce. Quote the SHA, never the ref — `master`
moves, and so does a corpus PR's branch head.

```bash
tools/corpus-checkout.py                   # master, into tests/al-language/
tools/corpus-checkout.py --corpus-pr 293   # a corpus pull request's head
tools/corpus-checkout.py --print           # what this worktree holds now
```

**Trap: a worktree made before #3737** still has a `.git` *file* under `tests/al-language/`
pointing into `.git/modules/`, which every worktree of this repository shares. No tool here
removes it for you — `rm -rf tests/al-language && tools/corpus-checkout.py`, which loses
nothing, because the corpus is read-only and re-cloned from the remote. `tools/preflight.py`
reports that state, and reports the resolved SHA otherwise.

**There is no pin to bump, so there is no fold / catch-up / blocked-by-intervening-commit
question.** A merged corpus PR reaches this repository on its next run. What replaced the pin
bump as the thing to get right is the **merge order**: a runner PR asserting BC behaviour
merges after the corpus PR it cites, and the coordinator merges the pair in one step
(`orchestrating-a-session`).

### Finding the pair from the CORPUS side, where no citation points back

Before merging a corpus PR, ask whether an open runner PR needs to land with it. **"No open
runner PR cites this one" does not answer that question**, and it is the check that feels like
it does.

A runner PR declaring `Corpus-NA:` names its corpus PR nowhere — and `Corpus-NA:` is exactly
the belief a later corpus PR overturns by writing a test.

Read the **corpus PR's own body** for the runner issue it was written for, then check that
issue for an open PR closing it:

<!-- Recipe-unpinned: both halves are live cross-repository GitHub reads -- the corpus PR's body and the runner repo's open-PR list. There is no offline input that reproduces "this corpus PR's paired runner PR is still open"; a fixture would pin the jq, not the question. -->
```bash
gh pr view <corpus-PR> --repo StefanMaron/BusinessCentral.AL.Language.Tests --json body \
  --jq '.body' | command grep -oE "BusinessCentral\.AL\.Runner(#|/issues/)[0-9]+"
gh pr list --repo StefanMaron/BusinessCentral.AL.Runner --state open --limit 100 \
  --json number,closingIssuesReferences \
  --jq '.[]|select(.closingIssuesReferences[]?.number == <N>)|.number'
```

Measured on #4168 (corpus PR #350 merged while its pair sat open) and #4167 (the same mistake
where the tests did depend on the unlanded fix).

## The corpus default branch is `master`, not `main`

Target `master` on a corpus PR. `gh pr create` with no explicit `--base` picks it up correctly;
a hand-written `--base main`, or an API call assuming `main`, fails with a 422 that does not say
why. The same asymmetry applies to every command naming a branch — `git merge-tree --write-tree
origin/master origin/<branch>` for a conflict check in the corpus, `origin/main` for one here.

## What this means in practice

- **Test failures in the corpus are runner gaps**, not corpus bugs. `no-assumption-fixes.md`
  still applies — investigate before patching, but the patch lands in the runner or in
  `tests/expectations/`, never in the corpus.
- **`_fixtures/Assert.al`, table fixtures, helper codeunits — all off-limits.** If
  `Assert.IsNumber` excludes a type and that causes failures, the bug is that the runner
  classifies that type differently from real BC; fix the classification.
- **A corpus commit is measured here on the next run that resolves `master`** — the next push
  to `main`, or the next floor run not debounced away (the floor keys on the *runner* SHA). An
  upstream PR with red runner-side consequences shows up as a red `main`: the corpus PR and the
  runner fix it needs belong to one merge step, in that order.
- **The test count is compared in CI, not committed** (#3675): each leg compares what it ran
  against the last count a `main` run recorded, naming both corpus SHAs on a drop; growth is
  allowed.

## Out-of-scope tests use the expectations manifest

Some corpus tests exercise surfaces the runner cannot support (report rendering, SMTP, HTTP
egress, real task scheduler, …). They pass against real BC and are expected to raise an
out-of-scope signal here (`loud-failures.md`). Declare those in
[`tests/expectations/`](../../tests/expectations/README.md) in one of the four modes —
`expect-oos`, `expect-fail-known-gap`, `expect-divergence`, `skip` — whose exact meaning and
required fields live in [`docs/expectations.md`](../../docs/expectations.md).

Manifest drift is loud in both directions: a test that starts passing despite an `expect-oos`
entry fails the run with "remove the entry"; one that starts throwing OOS without an entry
fails with "add an entry". With a moving corpus that drift can arrive without anyone here
pushing anything, which is a red `main` to fix rather than a mystery.

**A corpus merge that reds `main` is answered on the next coordinator sweep — with a fix, or
with an `expect-fail-known-gap` entry linking an open issue. Never by waiting**: nothing holds a
red corpus commit outside the repository any more. Search the open queue first and file a
runner-gap issue only when none exists (`file-issues-for-gaps.md`). Two things to get right
(#3737, corpus PR #273, #2943): name the **methods**, not `Method: "*"`, unless every test in
the codeunit fails — a wildcard claims the passing ones as failures and drifts the other way;
and read the failing set from a leg that **finished** — a leg that died in its unit tests never
ran the corpus and reports no failures rather than none.

## Which tests belong here at all

`bc-behavior-tests-go-upstream.md` decides that in both directions — what goes upstream, and
what stays in `tests/runner-extras/`.

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
