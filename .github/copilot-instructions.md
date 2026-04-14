# Copilot Code Review Instructions

These instructions apply to every pull request in this repository. Review each PR against the requirements below and flag anything that is missing or incorrect.

---

## Tests are mandatory — flag any PR that lacks them

Every change that touches behavior, mocks, or rewriter rules **must** include tests. Flag PRs that skip this:

- **New mock method or mock class** → requires a new test suite in `tests/bucket-N/NN-descriptive-name/` (or additions to an existing relevant suite)
- **New rewriter rule** → requires a test with AL code that exercises the rewritten construct
- **Bug fix** → requires a test that fails without the fix (even if added alongside the fix)
- **New CLI flag or exit code** → requires a test that exercises it

**Red flags to call out:**
- Source or runtime changes in `AlRunner/` with no corresponding change under `tests/`
- Test procedures that end with `Assert.IsTrue(true, ...)` or any unconditional assertion — those test compilation, not behavior
- Tests with only a happy-path case and no negative test (`asserterror` + `Assert.ExpectedError`)
- A new C# infrastructure class (cache, server protocol, rewriter helper) with no xUnit test in `AlRunner.Tests/`

---

## Every test suite must cover both directions

Every test procedure must prove:
1. **Positive**: correct input produces the expected result (`Assert.AreEqual`, `Assert.IsTrue`, etc.)
2. **Negative**: invalid input fails with the right error (`asserterror` + `Assert.ExpectedError`)

A suite that only proves the happy path is incomplete. Flag it.

---

## Documentation checklist

Flag any PR that changes observable behavior but skips these updates:

| File | What to check |
|---|---|
| `CHANGELOG.md` | Entry under `[Unreleased]` — always required for behavior changes |
| `README.md` | Supported/unsupported feature list, CLI flags |
| `PrintGuide()` in `AlRunner/Program.cs` | `--guide` output updated to match new capabilities |
| `CLAUDE.md` | Mock surface table, Known Limitations, Implemented Features, Key File Index |

If a change only touches internal implementation with no externally visible effect, README and guide updates may be skipped — but the `CHANGELOG.md` entry is always required.

---

## Code quality

Flag these patterns:

- **Duplicate logic**: a method that already exists elsewhere in the `Runtime/` namespace being re-implemented instead of reused
- **Defensive checks that the type system already prevents**: trust the contract, do not add null-guards for things that cannot be null
- **Speculative abstractions**: a new interface or base class added "for future use" with only one implementation
- **Shortcuts that create technical debt**: a simpler fix that leaves the codebase in worse shape than before — the right fix is always preferred, even if it touches more files

---

## What is in scope

Any AL language feature that can run without a real BC service tier is in scope: expanding mocks, new mock classes, new rewriter rules, new CLI capabilities.

Hard limits (cannot be fixed without BC service tier): parallel session semantics, real transaction isolation, page/report rendering, HTTP. These belong behind AL interfaces, not in the runner.

---

## Test suite structure (for reference)

```
tests/
  bucket-1/   — suites 01–32, 71, 77, 79-gui-fieldclass
  bucket-2/   — suites 33–95
  stubs/      — 39-stubs (run separately with --stubs)
  excluded/   — fixtures not in the main loop

tests/bucket-N/NN-descriptive-name/
  src/    — AL source codeunit(s)
  test/   — AL test codeunit (Subtype = Test)
```

New suites go in the bucket with fewer entries. AL object IDs must be unique within a bucket (suites in the same bucket compile together). IDs may repeat across buckets.
