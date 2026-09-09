---
name: al-runner-tests
description: How AL tests are organised and run — the read-only al-language corpus and how a run resolves it, the runner-owned expectations manifest, runner-extras for runner-specific positive tests, the proving-test rules, and the run command. Use when investigating a corpus failure, adding an expectation entry, writing a runner-specific test, or evaluating whether an existing test "proves" anything.
---

# Running and writing AL tests

## Layout

```
tests/
  al-language/         ← RESOLVED PER RUN, gitignored, READ-ONLY (#3737).
                         StefanMaron/BusinessCentral.AL.Language.Tests, the canonical AL-language
                         test corpus validated against a real BC service tier. Never edit.
                         `tools/corpus-checkout.py` puts it here — see below.
  expectations/        ← runner-owned JSON manifest declaring expected outcomes for corpus tests
                         the runner cannot or does not yet run.
                         - oos-<area>.json         out-of-scope-by-design
                         - known-gaps-<area>.json  in-scope but not yet implemented (links GH issue)
                         - divergence-<area>.json  runner intentionally answers differently from BC
                         - disabled-<area>.json    won't compile or won't run; pure skip
                         - count-baseline/         SEPARATE schema for --count-baseline; a
                                                   subdirectory so the (non-recursive) --expectations
                                                   scan never parses it as a classification array.
                                                   runner-extras only — the corpus suites left it
                                                   with the pin (#3675).
  runner-extras/       ← runner-specific positive tests (e.g. "surface X throws OOS with reason Y")
  archive/             ← v1 buckets and fixtures, frozen, scheduled for deletion
```

There is no `bucket-1/`, `bucket-2/`, `stubs/`, or per-bucket `idRange`. The corpus already organises tests by area (`record/`, `recordref/`, `codeunit/`, `json/`, `streams/`, `out-of-scope/`, etc.) — see `tests/al-language/README.md`.

## Get the corpus, then run it

`tests/al-language/` is not in git. Check it out first, and note the SHA it prints — that line
is the only record of which corpus a local result is about:

```bash
tools/corpus-checkout.py                   # master
tools/corpus-checkout.py --corpus-pr 293   # a corpus pull request's head
tools/corpus-checkout.py --print           # what this worktree holds now
# corpus: 1a2b3c4d... (master)
```

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
    --out al-language-results.json
```

CI passes no `--count-baseline` on the corpus legs since #3675: the corpus suites are not
declared in `tests/expectations/count-baseline/` at all, because a committed exact count would
go stale on every upstream corpus merge. Each leg counts what it ran and compares against the
last count a `main` run recorded, naming both corpus SHAs on a drop. `runner-extras` keeps its
committed baseline.

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

# Cache compiled AL output between runs; an agent uses its private directory
# (`.claude/agents/impl-agent.md`, "Namespace every path you write to")
dotnet run --project AlRunner -c Release -- --cache ~/.cache/al-runner/<AGENT-ID>-issue-<N>-<SESSION> tests/al-language/tests/al-language

# Extra package caches for dep resolution (repeatable)
dotnet run --project AlRunner -c Release -- --package-cache "$HOME/.al-runner/platform-apps" tests/al-language/tests/al-language

# JSON classification output
dotnet run --project AlRunner -c Release -- --out results.json tests/al-language/tests/al-language
```

## Interpreting output

Today the reporter prints raw PASS / FAIL / ERROR per test plus aggregate counts. Exit codes: `0` all passed, `1` at least one test FAILED or ERRORED, `2` a bundle could not execute (process-level error — also a bad invocation: unknown flag or a missing bundle path), `3` a bundle could not compile, `4` a `--count-baseline` count mismatch (still live for `runner-extras`). `--no-strict-exit` forces `0`.

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

**A wholesale EXEC-FAIL sweep on one identical missing path is another agent, not a regression.** Concurrent agents on one box drive the runner against a shared shadow cache under `~/.cache/al-runner/ncl-shadow/`, and one publishing into it while another loads from it produced **18 bundles failing with the same `Could not load file or assembly '…/ncl-shadow/<hash>/AlRunner.QueryJoin.dll'`** — the directory was gone by the time it was looked at, and it cleared on re-run. The tell is *every* bundle failing on one identical path, rather than a scattered set failing on their own assertions. Check for other runner processes (`pgrep -af 'dotnet.*AlRunner'`) and re-run before concluding anything; an unattended loop that files issues from that sweep files spectacular nonsense. `f461e5bf` addresses the publish side of this race; the consumer side is what you see.

## If a run dies with no output (exit 139 / 134)

An exit of 139 is SIGSEGV and 134 is SIGABRT — the process was killed by a signal, so there
is no managed stack, no test result and usually nothing in the log but the missing output.
#2819 is one such corpus run: it died seconds in, before any test reported, and four further
runs of the same tree finished 2523/2523.

**Do not re-run hoping to see it again.** A rare crash re-run without dump capture produces
another sighting and no evidence, and on a shared box it costs everyone else queue time.

Capture a dump instead. Set these before the run and the next fault leaves something to read:

```bash
export DOTNET_DbgEnableMiniDump=1
export DOTNET_DbgMiniDumpType=2          # heap; see below on why not 4
export DOTNET_DbgMiniDumpName="$PWD/crash-dumps/coredump.%p"
mkdir -p crash-dumps                     # createdump does NOT create this itself
```

Verified locally on .NET 8: a real `SIGSEGV` (`signal 11`) is caught and written, not only a
managed `AccessViolationException`. `createdump` prints `Writing minidump with heap to file
…` on the dying process's stderr, so its absence tells you the settings did not take.

`DOTNET_DbgMiniDumpType=4` is full memory. Measured: type 2 on a trivial hello-world process
is already ~127 MB, and it scales with committed memory — a runner with BC loaded is measured
in GB. Type 2 carries the faulting native stack, the module list and the managed heap, which
is what the first read of an unexplained crash needs. Reach for 4 only when 2 has been read
and found wanting.

CI sets all three at job level in `.github/workflows/bc-tests.yml` and uploads anything
produced as `crash-dumps-<bc-version>`, so a crash on any leg leaves an artifact rather than
an exit code.

Read one with `dotnet-dump analyze <file>` (`clrstack`, `clrmodules`, `eeversion`).

## `dotnet test` skips the BC-engine tests locally unless you bootstrap first

The ~28 rows in AlRunner.Tests' **`bc-engine-serial`** collection load the BC engine
in-process. On a local box they **skip** unless two things are set up, and at `dotnet test`'s
default verbosity a skip prints no reason at all — measured 2026-09-06: five of five rows
`[SKIP]`, no reason shown, `Skipped! - Failed: 0, Passed: 0, Skipped: 5`, **exit code 0**. A
RED baseline taken in that state is worthless: revert the fix, see "passed", and you have
concluded the opposite of the truth.

```bash
dotnet build AlRunner.Tests/AlRunner.Tests.csproj -c Release
tools/engine-test-bootstrap.sh                       # idempotent; converges in 2 passes
dotnet test AlRunner.Tests/AlRunner.Tests.csproj -c Release --no-build \
    --settings engine.runsettings --filter FullyQualifiedName~YourTestClass
```

**Re-run `tools/engine-test-bootstrap.sh` after EVERY build.** A build restores a pristine
`bin/Microsoft.Dynamics.Nav.Ncl.dll`, and rows that had been running go back to skipping.
`tools/engine-test-bootstrap.sh --verify` answers "would they run right now?" — exit 0 yes,
exit 1 no with the reason printed.

Since #3078 the skip reason itself names the collection, the unwrapped cause and the remedy
(`AlRunner.Tests/BcEngineSkipReason.cs`), but you only see it at
`--logger "console;verbosity=normal"` or higher. **`Skipped: N` in a summary is not a pass** —
if N is non-zero on a suite you are using as a baseline, find out which rows and why before
believing the run.

CI does this bootstrap itself (`.github/workflows/bc-tests.yml`, the "Warm the Ncl Cecil
rewrite cache" and "Generate .runsettings" steps), so the rows execute there. The gap is
purely local, which is exactly why nothing catches it for you.

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

**Check the sorting first.** A test asserting plain BC behaviour — what BC does, with nothing runner-specific in the claim — belongs **upstream in the corpus**, not here, even when writing it locally would be quicker. `tests/runner-extras/` is for claims that only make sense *because* this is the runner. See `.claude/rules/bc-behavior-tests-go-upstream.md` for the sorting test and the corpus-PR → runner-fix merge order.

## Coverage tracking (there isn't any)

v1 tracked AL-language coverage in a hand-curated `docs/coverage.yaml`, and the orchestrator blocked merges that didn't update it. That was retired at the v1→v2 cutover — the file is archived at `docs/archive/coverage.yaml` and nothing reads it. **In v2 the coverage record is the corpus plus `tests/runner-extras/`.** A PR's tests are its coverage entry; do not add, update, or ask anyone to update a coverage file.

## Which corpus a run measures

There is no pin (#3737). `git ls-tree origin/main tests/al-language` answers nothing, the
directory is gitignored, and each run resolves the corpus for itself:

| where | resolves |
|---|---|
| a pull request declaring `Corpus-PR: …/pull/<M>` | that pull request's branch head while it is open, `master` once it has merged |
| any other pull request, a push to `main`, the floor, a release | `master` |
| your worktree | whatever `tools/corpus-checkout.py` last put there |

**Every run prints `corpus: <full sha> (<ref>)`** — the job log, the run summary, and
`tools/corpus-checkout.py` locally. Quote the SHA, never the ref: `master` moves, and so does
a corpus pull request's branch head.

Two traps:

- **A PR body edited after your last push is not what the matrix read.** `pull_request` here
  deliberately does not trigger on `edited` — that would re-run the matrix on every body write
  — so after adding or changing a `Corpus-PR:` line, push an empty commit.
- **A worktree created before #3737** still holds the old submodule checkout, whose `.git` is a
  *file* pointing into `.git/modules/`, shared by every worktree. Nothing removes it for you:
  `rm -rf tests/al-language && tools/corpus-checkout.py`.

To review what a corpus pull request adds before it merges, read it in the corpus clone:

```bash
tools/corpus-checkout.py --corpus-pr 293
git -C tests/al-language log --oneline -5
```

Corpus tests that newly fail are runner gaps: patch the runner or add an expectation entry;
never patch the corpus.

## Sister docs

- `tests/al-language/README.md` — corpus description, areas, naming convention
- `tests/expectations/README.md` + `docs/expectations.md` — schema
- `.claude/rules/al-language-submodule.md` — read-only contract
- `.claude/rules/tdd.md` — red → green, both directions
- `.claude/rules/loud-failures.md` — surfaces that must throw OOS
