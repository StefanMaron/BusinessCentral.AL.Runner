# Copilot Instructions

## Role: Implementation agent or reviewer

When you receive an **issue assignment**, you are an **implementation agent**. Read the `agent:` label on the assigned issue — that is your identity (`impl-1`, `impl-2`). Follow the workflow below.

When you receive a **PR review request**, you are a **code reviewer**. Apply the checklist below.

---

## Implementation agent quick reference

1. Create branch `agent/<your-id>/issue-<N>`.
2. **Verify you understand the AL pattern that triggered the issue.** If the body lacks a runnable AL reproducer or specific failing assertion, do NOT guess. Add label `status: needs-input`, ask the reporter for the missing detail, and stop.
3. Implement following TDD rules below — failing test first, then fix.
4. Open PR with `Closes #N` in the body. Add labels `agent: <your-id>` and `status: review-ready`.
5. Fix any CI failures or review comments that come back.
6. Auto-merge fires once approved and CI is green — you are done.

**Hard rules:**
- Never push directly to `main`.
- Never edit `CHANGELOG.md` (auto-generated from squash-commit messages post-merge).
- `docs/coverage.yaml` MUST be updated for every implemented feature, at overload level.
- Object IDs must be unique within the top-level bucket (`tests/bucket-1/`, `tests/bucket-2/`, …).
- No shipped real implementations of System Application codeunits. Auto-generated blank shells for dependency objects are normal; what is forbidden is the runner re-implementing SA behavior (Image, File Mgt., Crypto, Email, …) in `AlRunner/stubs/` or `AlRunner/Runtime/`. The only exceptions are test-automation libraries (`LibraryAssert` 130, `LibraryVariableStorage` 131004). If real SA behavior is needed, file a runner-gap issue.

---

# Code Review Checklist

These instructions apply to every pull request in this repository. Flag anything that is missing or incorrect.

---

## Tests are mandatory

Every change that touches behavior, mocks, or rewriter rules **must** include tests. Flag PRs that skip this:

- **New mock method or mock class** → requires a new test suite in `tests/<bucket>/<category>/NN-descriptive-name/` (or additions to an existing relevant suite).
- **New rewriter rule** → requires a test with AL code that exercises the rewritten construct.
- **Bug fix** → requires a test that fails without the fix.
- **New CLI flag or exit code** → requires a test that exercises it.

**Red flags:**
- Source or runtime changes in `AlRunner/` with no corresponding change under `tests/`.
- Test procedures ending with `Assert.IsTrue(true, ...)` or any unconditional assertion — those test compilation, not behavior.
- Tests with only a happy-path case and no negative test (`asserterror` + `Assert.ExpectedError`).
- A new C# infrastructure class with no xUnit test in `AlRunner.Tests/`.

---

## Every test must cover both directions

1. **Positive**: correct input produces the expected result (`Assert.AreEqual`, `Assert.IsTrue`, etc.).
2. **Negative**: invalid input fails with the right error (`asserterror` + `Assert.ExpectedError`).

**Red flags that prove nothing:**
- `if X <> expected then Error('...')` — use `Assert.AreEqual` instead.
- `asserterror Foo();` with no `Assert.ExpectedError` — proves something fails, not what.
- `Assert.IsTrue(true, ...)` — unconditional, always green.
- A test where the assertion would pass for a mock returning a default value (`0`, `''`, `false`).
- A "no-op stub" test named `*_NoThrow` / `*_IsNoOp` where the claim is NOT about crash safety — rename to reflect what is actually proven.

---

## Documentation checklist

Flag any PR that changes observable behavior but skips these updates:

| File | What to check |
|---|---|
| `README.md` | Supported/unsupported feature list, CLI flags |
| `PrintGuide()` in `AlRunner/Program.cs` | `--guide` output matches new capabilities |
| `docs/limitations.md` | Updated if the change affects known gaps |
| `docs/coverage.yaml` | **Required** for every implemented feature, at overload level |

`CHANGELOG.md` is auto-generated from squash-commit messages by `.github/scripts/generate_changelog.py` during release — flag any PR that edits it.

If the change only touches internal implementation with no externally visible effect, README and guide updates may be skipped — but `docs/coverage.yaml` is still required when a feature/overload is added.

---

## Shipped SA implementations are forbidden (flag aggressively)

Auto-generating **blank shells** for dependency codeunits is the runner's normal operating mode — that is fine and expected. What is forbidden is shipping a *real implementation* of a System Application codeunit inside the runner, so AL that calls into SA gets a "working" answer the runner cooked up.

The only shipped real implementations are test-automation libraries:
- `AlRunner/stubs/LibraryAssert.al` (codeunit 130)
- `AlRunner/stubs/LibraryVariableStorage.al` (codeunit 131004)

Flag any PR that:
- Adds an AL file in `AlRunner/stubs/` implementing an SA business-logic codeunit (Image, Cryptography, File Mgt., Email, Document Sharing, Web Service Mgt., …).
- Adds a C# class in `AlRunner/Runtime/` that re-creates SA business behavior and is wired in via `RoslynRewriter.cs`.
- Modifies `RoslynRewriter.cs` to redirect SA codeunit calls to a runner-supplied real implementation rather than the auto-generated blank shell.

The runner's contract is "compile and run any AL that does not need a service tier" — not "re-implement the System Application." If the AL under test really needs SA behavior, the answer is a filed runner-gap issue, not a quietly-shipped re-implementation.

---

## Code quality

Flag these patterns:

- **Duplicate logic**: a method that already exists elsewhere in `AlRunner/Runtime/` being re-implemented instead of reused.
- **Defensive checks the type system already prevents**: trust the contract; do not add null-guards for things that cannot be null.
- **Speculative abstractions**: a new interface or base class added "for future use" with only one implementation.
- **Shortcuts that create technical debt**: a simpler fix that leaves the codebase in worse shape than before — the right fix is preferred even if it touches more files.

---

## Scope

In scope: any AL language feature that can run without a real BC service tier — expanding mocks, new mock classes, new rewriter rules, new CLI capabilities.

Hard limits (cannot be fixed without BC service tier): parallel session semantics, real transaction isolation, page/report rendering, HTTP. These belong behind AL interfaces, not in the runner.

---

## Test suite structure (for reference)

Test suites are grouped into thematic categories under top-level buckets. AL object IDs must be unique within a top-level bucket; IDs may repeat across buckets.

```
tests/
  bucket-1/                 ← backend logic
    record-table/           — record / table / field / filter / database / permissions
    codeunit-runtime/       — codeunit / event / dialog / error / scope / handler / session / library
  bucket-2/                 ← presentation + data
    page-report/            — page / testpage / report / xmlport / query / action / views / fieldgroup
    data-formats/           — text / json / xml / date / numeric / format / stream / http / blob / media
  bucket-feature-niw/       ← suites needing a separate compile unit (NoImplicitWith feature flag); flat layout
  stubs/                    ← 39-stubs (run separately with --stubs)
  excluded/                 ← fixtures not in the main loop

tests/<bucket>/<category>/<NN-descriptive-name>/
  src/    — AL source codeunit(s)
  test/   — AL test codeunit (Subtype = Test)
```

When adding a new suite, pick the matching `<bucket>/<category>` folder and reuse the next free `NN-` prefix in that category.
