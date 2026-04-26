# Copilot Instructions

## Role: Implementation agent or reviewer

When you receive an **issue assignment**, you are acting as an **implementation agent**. Read the `agent:` label on the assigned issue — that is your identity (`impl-1` or `impl-2`). Follow the agent workflow in `AGENTS.md`.

**Implementation agent quick reference:**
1. Create branch `agent/<your-id>/issue-<N>`
2. Implement following TDD rules below — failing test first, then fix
3. Open PR with `Closes #N` in the body. Add labels `agent: <your-id>` and `status: review-ready` to the PR
4. Fix any CI failures or review comments that come back
5. Auto-merge fires once approved and CI is green — you are done

When you receive a **PR review request**, you are acting as a **code reviewer**. Apply the checklist below.

---

# Code Review Checklist

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

**Red flags that prove nothing:**
- `if X <> expected then Error('...')` — use `Assert.AreEqual` instead
- `asserterror Foo();` with no `Assert.ExpectedError` — proves something fails, not what
- A test with only `Assert.IsTrue(true, ...)` — unconditional, always green
- A test where the assertion would pass for a mock returning a default value (0, `''`, `false`)
- A "no-op stub" test named `DoesNotCrash`/`IsNoOp` where the claim is NOT about crash safety — rename to reflect what is actually proven

---

## Documentation checklist

Flag any PR that changes observable behavior but skips these updates:

| File | What to check |
|---|---|
| `README.md` | Supported/unsupported feature list, CLI flags |
| `PrintGuide()` in `AlRunner/Program.cs` | `--guide` output updated to match new capabilities |
| `CLAUDE.md` | Mock surface table, Known Limitations, Implemented Features, Key File Index |

`CHANGELOG.md` is auto-generated from squash-commit messages by `.github/scripts/generate_changelog.py` during release — do not manually edit it in PRs.

If a change only touches internal implementation with no externally visible effect, README and guide updates may be skipped.

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

Test suites are grouped into thematic categories under three top-level buckets. AL object IDs must be unique within a top-level bucket (suites in the same bucket compile together); IDs may repeat across buckets.

```
tests/
  bucket-1/                 ← backend logic
    record-table/           — record / table / field / filter / database / permissions
    codeunit-runtime/       — codeunit / event / dialog / error / scope / handler / session / library / language features
  bucket-2/                 ← presentation + data
    page-report/            — page / testpage / report / xmlport / query / action / views / fieldgroup
    data-formats/           — text / json / xml / date / numeric / format / stream / http / blob / media
  bucket-feature-niw/       ← suites that need a separate compile unit due to AL feature flags
    feature-niw/            — uses `"features": ["NoImplicitWith"]` in app.json
  stubs/                    ← 39-stubs (run separately with --stubs)
  excluded/                 ← fixtures not in the main loop

tests/<bucket>/<category>/<NN-descriptive-name>/
  src/    — AL source codeunit(s)
  test/   — AL test codeunit (Subtype = Test)
```

When adding a new suite, pick the matching `<bucket>/<category>` folder and reuse the next free `NN-` prefix in that category.
