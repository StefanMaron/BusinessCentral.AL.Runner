---
name: al-runner-tests
description: How AL tests are organised and run — the read-only al-language submodule corpus, the runner-owned expectations manifest, runner-extras for runner-specific positive tests, the proving-test rules, the run command, and how to bump the corpus pin. Use when investigating a corpus failure, adding an expectation entry, writing a runner-specific test, or evaluating whether an existing test "proves" anything.
---

# Running and writing AL tests

## Layout

```
tests/
  al-language/         ← git submodule, READ-ONLY (StefanMaron/BusinessCentral.AL.Language.Tests).
                         The canonical AL-language test corpus validated against a real BC service tier.
                         Never edit. Pin bump folds into the fix PR it enables — see below.
  expectations/        ← runner-owned JSON manifest declaring expected outcomes for corpus tests
                         the runner cannot or does not yet run.
                         - oos-<area>.json         out-of-scope-by-design
                         - known-gaps-<area>.json  in-scope but not yet implemented (links GH issue)
                         - divergence-<area>.json  runner intentionally answers differently from BC
                         - disabled-<area>.json    won't compile or won't run; pure skip
                         - count-baseline/         SEPARATE schema for --count-baseline; a
                                                   subdirectory so the (non-recursive) --expectations
                                                   scan never parses it as a classification array
  runner-extras/       ← runner-specific positive tests (e.g. "surface X throws OOS with reason Y")
  archive/             ← v1 buckets and fixtures, frozen, scheduled for deletion
```

There is no `bucket-1/`, `bucket-2/`, `stubs/`, or per-bucket `idRange`. The corpus already organises tests by area (`record/`, `recordref/`, `codeunit/`, `json/`, `streams/`, `out-of-scope/`, etc.) — see `tests/al-language/README.md`.

## Run the corpus

```bash
dotnet build AlRunner.slnx -c Release
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language
```

**Match CI when you are proving something.** `.github/workflows/bc-tests.yml` runs the corpus as:

```bash
dotnet run --no-build --project AlRunner -c Release --framework net8.0 -- \
    tests/al-language/tests/al-language \
    --package-cache "$HOME/.al-runner/platform-apps" \
    --strict \
    --count-baseline tests/expectations/count-baseline/test-count-baseline.json \
    --out al-language-results.json
```

and `tests/runner-extras` with a second `--package-cache "$HOME/.al-runner/test-apps"`. The
`$HOME/.al-runner/platform-apps` cache is what `provision` / `--auto-provision` (on by default
since #2024) and `tools/DownloadArtifacts` populate. A run without it falls back to whatever
artifacts are cached, which is not necessarily the version the corpus declares.

Useful flags (`--guide` and `AlRunner/Program.cs` are the full list):

```bash
# Only FAIL/ERROR lines (PASS lines are on by default in v2; --show-pass is a v1 no-op alias)
dotnet run --project AlRunner -c Release -- --failures-only tests/al-language/tests/al-language

# Verbose internal logs
dotnet run --project AlRunner -c Release -- --verbose tests/al-language/tests/al-language

# One test by name
dotnet run --project AlRunner -c Release -- --test Record_Insert tests/al-language/tests/al-language

# Test isolation modes
dotnet run --project AlRunner -c Release -- --isolation codeunit  tests/al-language/tests/al-language
dotnet run --project AlRunner -c Release -- --isolation test      tests/al-language/tests/al-language
dotnet run --project AlRunner -c Release -- --isolation disabled  tests/al-language/tests/al-language

# Cache compiled AL output between runs
dotnet run --project AlRunner -c Release -- --cache ~/.cache/al-runner/al-out tests/al-language/tests/al-language

# Extra package caches for dep resolution (repeatable)
dotnet run --project AlRunner -c Release -- --package-cache "$HOME/.al-runner/platform-apps" tests/al-language/tests/al-language

# JSON classification output
dotnet run --project AlRunner -c Release -- --out results.json tests/al-language/tests/al-language
```

## Interpreting output

Today the reporter prints raw PASS / FAIL / ERROR per test plus aggregate counts. Exit codes: `0` all passed, `1` at least one test FAILED or ERRORED, `2` a bundle could not execute (process-level error — also a bad invocation: unknown flag or a missing bundle path), `3` a bundle could not compile, `4` a `--count-baseline` count mismatch. `--no-strict-exit` forces `0`.

`AlRunner/Infrastructure/ExpectationManifest.cs` loads the schema described in `docs/expectations.md` and is wired into the run, so results are additionally classified as:

| Classification | Meaning |
|---|---|
| `pass` | Test ran and passed. |
| `pass-oos` | Test raised an out-of-scope signal with the expected reason anchor — typed `RunnerOutOfScopeException` or the `out-of-scope: <api> — <reason>` message convention (declared in `oos-<area>.json`). Counted as success. |
| `pass-known-gap` | Test failed and matches a `known-gaps-<area>.json` entry. Linked GH issue tracks the fix. |
| `pass-divergence` | Test failed and matches a `divergence-<area>.json` entry — the runner intentionally answers differently from BC. Permanent; `Doc` cites the decision. |
| `skipped` | Test matched a `disabled-<area>.json` entry; not executed. |
| `fail` | Real failure — either unexpected, or expectation drift in any direction. |

Drift is loud in every direction: a test passing despite an entry fails with "remove the entry"; a test raising an OOS signal without an entry fails with "add an entry"; a wrong or near-miss `Reason` still fails. See `docs/expectations.md`.

## Proving-test rules

A skeptic must be able to read any test and say: "yes, if this passes, feature X works correctly." Every test satisfies all four:

**1. Positive case with a specific assertion**
```al
Result := MyProc(3, 4);
Assert.AreEqual(7, Result, 'MyProc should return the sum');
```

**2. Negative case with a specific error**
```al
asserterror MyProc(-1);
Assert.ExpectedError('Value must be positive');
```

**3. Would catch a broken implementation.** If the test passes when the implementation always returns the default value (`0`, `''`, `false`), it is not a proving test. Assert a non-default concrete value.

**4. Use `Assert.*` — never `if X then Error(...)`.** Use `Assert.AreEqual`, `Assert.IsTrue`, `Assert.IsFalse`, `Assert.ExpectedError`.

Exception: "no-op stub" tests where the *entire* claim is "this does not crash" — name them `*_NoThrow` / `*_IsNoOp` so the limited claim is explicit.

## Adding an expectation entry

When a corpus test exercises a surface the runner refuses by design (SMTP, real HTTP, report rendering, …):

1. Pick the right file: `tests/expectations/oos-<area>.json` (or `known-gaps-<area>.json` for "in scope, not yet implemented", with a GH issue link).
2. Add one entry following `docs/expectations.md`. One entry per PR if possible; sharding by area keeps diffs small.
3. The reason field must match a reason already used in `docs/scope.md` (`email-smtp`, `http-egress`, `not-yet-implemented`, …).

## Writing a runner-extras test

When the claim is "this runner surface throws `RunnerOutOfScopeException` with the expected reason" or otherwise asserts runner-specific behaviour the upstream corpus cannot, put it in `tests/runner-extras/` as a normal `app.json`-rooted AL project. Apply the proving-test rules above.

**Check the sorting first.** A test asserting plain BC behaviour — what BC does, with nothing runner-specific in the claim — belongs **upstream in the corpus**, not here, even when writing it locally would be quicker. `tests/runner-extras/` is for claims that only make sense *because* this is the runner. See `.claude/rules/bc-behavior-tests-go-upstream.md` for the sorting test and the corpus-PR → pin-bump → runner-fix order.

## Coverage tracking (there isn't any)

v1 tracked AL-language coverage in a hand-curated `docs/coverage.yaml`, and the orchestrator blocked merges that didn't update it. That was retired at the v1→v2 cutover — the file is archived at `docs/archive/coverage.yaml` and nothing reads it. **In v2 the coverage record is the corpus plus `tests/runner-extras/`.** A PR's tests are its coverage entry; do not add, update, or ask anyone to update a coverage file.

## Bumping the corpus pin

The submodule is read-only. To pull in new tests from upstream:

```bash
git -C tests/al-language fetch
git -C tests/al-language log --oneline HEAD..origin/master
git -C tests/al-language diff HEAD..origin/master   # review
git -C tests/al-language checkout origin/master
git add tests/al-language
```

**A pin bump cannot be its own PR — it is red by construction.** The new
upstream tests it pulls in exist because they exercise a gap the runner does
not yet handle; bumping the pin alone fails CI for that reason before anyone
reviews it. Fold the pin bump into the **fix PR** that makes those new tests
pass, together with the `tests/expectations/count-baseline/test-count-baseline.json`
update (`--count-baseline` is an exact match — it fails on growth as well as
shrinkage). Any other tests that newly fail after the bump are separate runner
gaps: patch the runner or add an expectation entry for them; never patch the
corpus.

## Sister docs

- `tests/al-language/README.md` — corpus description, areas, naming convention
- `tests/expectations/README.md` + `docs/expectations.md` — schema
- `.claude/rules/al-language-submodule.md` — read-only contract
- `.claude/rules/tdd.md` — red → green, both directions
- `.claude/rules/loud-failures.md` — surfaces that must throw OOS
