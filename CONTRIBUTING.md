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

Each test case is a self-contained directory:

```
tests/NN-descriptive-name/
  src/   — AL source codeunit(s) exercising the feature
  test/  — AL test codeunit (Subtype = Test) using Assert
```

Number the directory sequentially after the last existing one. The CI pipeline discovers and runs all of them automatically.

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
for s in $(ls -d tests/*/src | sed 's|tests/||;s|/src||' | grep -v '06-' | sort); do
  dotnet run --project AlRunner --framework net8.0 -- "tests/$s/src" "tests/$s/test" || {
    rc=$?; [ $rc -eq 2 ] || exit $rc
  }
done
```

Exit code 2 means the runner hit a known limitation (not a test failure). Exit codes 1 and 3 are real failures and block CI.

---

## CHANGELOG.md

Every change that affects behavior, CLI flags, mock capabilities, or exit codes must have an entry under `[Unreleased]` in `CHANGELOG.md`. The publish pipeline reads this file directly — it does not auto-generate release notes.

Use the existing entries as a template for tone and granularity. A one-line entry per logical change is enough; a paragraph is not necessary.

---

## Documentation checklist

When your change affects observable behavior, update all of the following before marking the PR ready:

| Location | What to update |
|---|---|
| `README.md` | Supported/unsupported feature list, CLI flags |
| `PrintGuide()` in `Program.cs` | The `--guide` output (primary discovery mechanism for AI coding agents) |
| `CLAUDE.md` | Mock surface table, Known Limitations, Implemented Features, Key File Index |
| `CHANGELOG.md` | Entry under `[Unreleased]` |

If a change only touches internal implementation with no externally visible effect, you can skip the README and guide updates — but the CHANGELOG entry is always required.

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
