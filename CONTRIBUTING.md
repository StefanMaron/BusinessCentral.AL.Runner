# Contributing to BusinessCentral.AL.Runner

Thanks for your interest in contributing. This guide covers the requirements every pull request must meet before it can be merged.

---

## Before you start

Read `CLAUDE.md`. It describes the architecture, mock surface, pipeline stages, and known limitations. Understanding it will save you from building something that conflicts with the project's design.

The goal is broad AL language compatibility — targeting the full functional AL surface. A small number of hard architectural limits exist (parallel sessions, transaction isolation, service-tier rendering, HTTP), documented in `docs/limitations.md`. Everything else is a gap to close. If AL code compiles but fails to run in the runner, that is a missing feature, not a design boundary.

---

## Tests are mandatory — no exceptions

Every feature, fix, and mock addition must have tests. No change merges without them.

### AL end-to-end tests (`tests/`)

Tests live in numbered buckets inside `tests/`:

```
tests/
  bucket-1/   — suites 01–32, 71, 77, 79-gui-fieldclass
  bucket-2/   — suites 33–95
  stubs/      — 39-stubs (run separately with --stubs)
  excluded/   — fixtures not in the main loop
```

Each suite inside a bucket:

```
tests/bucket-N/NN-descriptive-name/
  src/   — AL source codeunit(s) exercising the feature
  test/  — AL test codeunit (Subtype = Test) using Assert
```

Put new suites in the bucket with fewer entries. Check that the AL object IDs (codeunit, table numbers) in your new suite don't clash with other suites already in that bucket — suites in the same bucket compile together, so IDs must be unique within a bucket. IDs may repeat across buckets (they run as separate invocations).

**Every test case must cover both directions:**

- **Positive**: call the logic with valid input, assert on the result with `Assert.AreEqual` or similar.
- **Negative**: call with invalid input, use `asserterror` + `Assert.ExpectedError` to verify the error is caught.

A test that only proves the happy path proves nothing — a mock that returns a constant default value would pass it too.

**`Assert.IsTrue(true, ...)` is not a test.** If your test procedure ends with an unconditional assertion, it is testing compilation, not behavior. The assertion must depend on the output of the code under test.

### C# unit tests (`AlRunner.Tests/`)

New cache classes, rewriter rules, server protocol handling, and other infrastructure components should also have xUnit tests in `AlRunner.Tests/`. Look at the existing tests for structure:

- `IncrementalTests.cs` — server-mode caching and warm-start behavior
- `SingleProcedureTests.cs` — `--run` flag test isolation
- `StubGeneratorTests.cs` — `--generate-stubs` output

When your change is primarily about internal C# behavior (thread-safety, cache correctness, protocol parsing), a C# test is the right vehicle. When the change is about what AL code can run, an AL end-to-end test in `tests/` is the right vehicle. Often both are needed.

### Red/Green TDD

Write the failing test first, verify it fails for the right reason, then implement the fix. This sequence matters: it proves your test actually exercises the code you wrote. Never write implementation code without a failing test to drive it.

---

## The full CI pipeline must pass

All pull requests run against a matrix of BC versions (currently 26.0–27.5) on both net8.0 and net9.0. A PR cannot merge unless every job in the matrix is green.

To run the same checks locally before pushing:

```bash
# Build (downloads BC DLLs automatically on first run)
dotnet build AlRunner.slnx

# C# unit tests
dotnet test AlRunner.Tests/

# AL end-to-end tests (net8.0)
for bucket in tests/bucket-*/; do
  args=""
  for suite in "$bucket"*/; do
    [ -d "${suite}src"  ] && args="$args ${suite}src"
    [ -d "${suite}test" ] && args="$args ${suite}test"
  done
  dotnet run --project AlRunner --framework net8.0 -- $args || {
    rc=$?; [ $rc -eq 2 ] || exit $rc
  }
done

# Stubs suite (needs --stubs flag)
dotnet run --project AlRunner --framework net8.0 -- \
  --stubs tests/stubs/39-stubs/stubs tests/stubs/39-stubs/src tests/stubs/39-stubs/test
```

Exit code 2 means the runner hit a known limitation (not a test failure). Exit codes 1 and 3 are real failures and block CI.

---

## CHANGELOG.md

`CHANGELOG.md` is **auto-generated** during release by `.github/scripts/generate_changelog.py`, which reads squash-commit messages and injects categorized release notes. The publish pipeline runs this script and commits the result — manual edits will be overwritten.

**Do not manually edit `CHANGELOG.md` in PRs.**

To make sure your change appears correctly in the generated release notes, use a [Conventional Commit](https://www.conventionalcommits.org/) prefix in your PR title and squash-commit message:

- `feat:` — new feature or capability visible to users
- `fix:` — bug fix
- `docs:` — documentation-only change
- `chore:` — maintenance, dependency bumps, CI changes

The generator uses these prefixes to categorize entries automatically.

---

## Documentation checklist

When your change affects observable behavior, update all of the following before marking the PR ready:

| Location | What to update |
|---|---|
| `README.md` | Supported/unsupported feature list, CLI flags |
| `PrintGuide()` in `Program.cs` | The `--guide` output (primary discovery mechanism for AI coding agents) |
| `CLAUDE.md` | Mock surface table, Known Limitations, Implemented Features, Key File Index |

If a change only touches internal implementation with no externally visible effect, you can skip the README and guide updates.

---

## Code quality

**Best solution, not easiest.** Choose the highest-quality approach regardless of how much refactoring it requires. A simpler fix that creates technical debt or leaves the codebase in a worse shape than before is not acceptable. If the right solution requires touching more files than a shortcut would, do it properly.

**DRY.** Do not duplicate logic. If you are writing a private method that already exists somewhere else in the Runtime namespace, extract it and share it. Two copies of the same code will diverge.

**SOLID.** Keep classes focused on a single responsibility. Prefer composition. Do not add parameters to methods just to thread state through — if a class needs that state, it should own it.

**No speculative abstractions.** Three similar lines of code is better than a premature abstraction built for a hypothetical future case. Only extract when the duplication is real and the abstraction is obvious.

**Only validate at system boundaries.** Do not add defensive checks for conditions that the internal contract already prevents. Trust the type system and framework guarantees.

---

## What belongs in this repo

Any AL language feature that can be run without a real BC service tier is in scope. This includes expanding existing mocks, adding new mock classes, new rewriter rules, and new CLI capabilities.

The hard limits are architectural, not policy: parallel session contracts, real transaction isolation, page/report rendering, and HTTP cannot work without the service tier. Everything else is either already supported or a gap worth filling.

If you are unsure whether something is in scope, open an issue and describe what AL construct you want to support. The bar is: can it be meaningfully emulated in a single .NET process with in-memory state?
