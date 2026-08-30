# Copilot Instructions

## Role: implementation agent or reviewer

When you receive an **issue assignment**, you are an **implementation agent**. The `agent:` label on the issue is your identity (`impl-1`, `impl-2`). Follow the workflow below.

When you receive a **PR review request**, you are a **code reviewer**. Apply the checklist below.

---

## Implementation agent quick reference

1. Create branch `agent/<your-id>/issue-<N>`.
2. **Verify you understand the AL pattern that triggered the issue.** If the body lacks a runnable AL reproducer or specific failing assertion, do not guess. Add label `status: needs-input`, ask the reporter for the missing detail, and stop. (`.claude/rules/no-assumption-fixes.md`)
3. Implement following the TDD rules below — failing test first, then fix.
4. Open PR with `Closes #N` in the body. Add labels `agent: <your-id>` and `status: review-ready`.
5. Fix any CI failures or review comments that come back.
6. Auto-merge fires once approved and CI is green (`allow_auto_merge=true` is a repo setting, not visible in the checkout).

**Hard rules:**
- Never push directly to `main`.
- Never edit `CHANGELOG.md` (auto-generated from squash-commit messages post-merge).
- Never edit a file under `tests/al-language/` — that submodule is read-only. A pin bump is folded into the fix PR it enables, never its own PR (`.claude/rules/al-language-submodule.md`).
- Honour the precompiled-DLL contract: do not rewrite method bodies or rename types in MS / ISV business-logic DLLs (`.claude/rules/precompiled-dll-respect.md`).
- Every unsupported surface must throw `RunnerOutOfScopeException` with a named API and reason from `docs/scope.md`. Never silently return a default (`.claude/rules/loud-failures.md`).

---

## Repo layout (v2)

```
AlRunner/                      — runner source
  Program.cs                   — CLI entry, bundle iteration, --precompile dispatch
  BcRuntime.cs                 — patch installation (BcRuntime.EnsureApplied())
  BcCompiler.cs                — AL → IL via BC's Compilation.Emit
  BcAssembler.cs               — Roslyn-compiled C# polyfill bodies
  TestExecutor.cs              — [NavTest] discovery + isolation modes
  AppLoader.cs                 — load real MS / ISV .app DLLs in-process
  DependencyLoader.cs          — 3-tier dep resolution (precompiled / loose / compiled-from-source)
  Reporter.cs                  — JSON classification output
  Patches/*.cs                 — per-API patch bodies, only live if NclCecilRewrite routes to them
  Infrastructure/
    NclCecilRewrite.cs         — one-time Cecil rewrite of Ncl.dll, cached; the only live patch mechanism
    JmpHook.cs                 — legacy JMP-hook mechanism, disabled by default; a new call site is a silent no-op
    ExpectationManifest.cs     — schema + loader for tests/expectations, wired into the run via Program.cs/TestExecutor
    RunnerOutOfScopeException.cs — typed OOS exception
AlRunner.Tests/                — C# unit-test project (dotnet test AlRunner.Tests); mechanism-level tests, not AL
tests/al-language/             — git submodule, canonical corpus (READ-ONLY)
tests/expectations/            — JSON manifest declaring expected outcomes for corpus tests
tests/runner-extras/           — runner-specific positive tests
tests/archive/                 — v1 buckets and fixtures (frozen, scheduled for deletion)
docs/                          — expectations.md, scope.md, limitations.md, cecil-migration.md
docs/archive/                  — v1-only documents (dap.md, extract-deps.md, coverage.{md,yaml}, subsystems.md, etc.)
tools/                         — DownloadArtifacts (used by AlRunner.csproj), RuntimeApiEnumerator, telemetry-triage
scripts/                       — al-inventory.py, coverage-gen.js
```

The v1 layout (`tests/bucket-1/`, `tests/bucket-2/`, stubs/, Runtime/MockX.cs, RoslynRewriter, DepCompiler, `extract-deps`) has been removed. Do not reference it. `AlRunner.Tests/` is current, not v1 — it is the C# unit-test project both `bc-tests.yml` and `pr-check.yml` depend on.

---

## Dev loop

```bash
# Clone with submodules
git clone --recurse-submodules https://github.com/StefanMaron/BusinessCentral.AL.Runner

# Build
dotnet build AlRunner.slnx -c Release

# Run the al-language corpus
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language

# Useful flags
dotnet run --project AlRunner -c Release -- --verbose --show-pass tests/al-language/tests/al-language
dotnet run --project AlRunner -c Release -- --isolation test tests/al-language/tests/al-language
dotnet run --project AlRunner -c Release -- --cache ~/.cache/al-runner/al-out tests/al-language/tests/al-language
```

Exit codes: `0` all tests passed, `1` a test failed/errored, `2` a bundle could not execute (process-level error, or a bad invocation), `3` a bundle could not compile, `4` a `--count-baseline` mismatch.

---

# Code review checklist

These instructions apply to every PR in this repository. Flag anything that is missing or incorrect.

## Tests are mandatory

Every change must include a test. Flag PRs that skip this:

- **New runtime behaviour / new patch** → cite the al-language test that fails before and passes after (or, if the corpus doesn't cover it, a new entry in `tests/runner-extras/`).
- **Bug fix** → requires a test that fails without the fix.
- **New CLI flag or exit code** → requires a test that exercises it.
- **New OOS-by-design surface** → new entry in `tests/expectations/oos-<area>.json` per [`docs/expectations.md`](../docs/expectations.md).

**Red flags:**
- Source changes under `AlRunner/` with no test reference.
- Test procedures ending with `Assert.IsTrue(true, ...)` or any unconditional assertion.
- Tests with only a happy-path case and no negative (`asserterror` + `Assert.ExpectedError`).
- Tests that would still pass if the implementation always returned the default value (`0`, `''`, `false`).
- "No-op stub" tests not named `*_NoThrow` / `*_IsNoOp` even though the only claim is crash-safety.

## Every test must cover both directions

1. **Positive** — correct input produces the expected concrete value.
2. **Negative** — invalid input fails with the specific error (`asserterror` + `Assert.ExpectedError`).

## Precompiled-DLL respect

Flag any PR that:
- Rewrites method bodies in `*.SystemApplication.dll`, `*.BaseApplication.dll`, or any ISV business-logic DLL.
- Renames or removes types/members in any precompiled DLL.
- Changes method signatures of methods called from precompiled DLLs.

Modifications to the runtime engine (`Microsoft.Dynamics.Nav.Ncl.dll`, `Microsoft.Dynamics.Nav.Types.dll`) and to skeleton state are allowed. New patches should use Cecil rewriting, not new JMP-hooks (Cecil migration freeze; existing JmpHook code stays for now).

## Loud failures

Flag any patch that silently returns a default value for an unsupported surface. The contract is: throw `RunnerOutOfScopeException` with the BC API name and a reason from `docs/scope.md`. Sentinel returns (`Action.Ok`, `FormResult.OK`, `S-1-0-0` SIDs, etc.) make green tests lie.

## Documentation checklist

| File | When to update |
|---|---|
| `README.md` | CLI surface changed, new supported AL feature, new env var. |
| `docs/limitations.md` | Hard architectural limit shifted. |
| `docs/scope.md` | Per-API in/out-of-scope decision changed. |
| `docs/expectations.md` | Schema or mode semantics changed. |
| `tests/expectations/*.json` | New OOS-by-design surface or new known-gap. |

`CHANGELOG.md` is auto-generated from squash-commit messages — flag any PR that edits it.

## Submodule contract

`tests/al-language/` is read-only. Flag any PR that:
- Edits files under `tests/al-language/` directly.
- Bumps the submodule pin with no accompanying fix in the same PR — a pin bump alone is red by construction and belongs folded into the fix PR it enables.

## Code quality

Flag these patterns:
- **Duplicate logic** — a method that already exists elsewhere in `AlRunner/` re-implemented.
- **Defensive checks the type system already prevents** — null-guards for things that cannot be null.
- **Speculative abstractions** — interfaces / base classes added "for future use" with one implementation.
- **Shortcuts that create debt** — a simpler fix that leaves the codebase in worse shape than the right one.

## Scope

In scope: anything the runner can execute in-process against the real MS / ISV DLLs — records, codeunits, events, test toolkit, RecordRef/FieldRef, BLOB/streams, JSON/XML, in-process crypto, IsolatedStorage, synchronous TaskScheduler dispatch.

Out of scope (must throw `RunnerOutOfScopeException`): SMTP, HTTP egress, external file I/O, OData/SOAP publishing, physical printers, real job-queue scheduling, page/report rendering (handler callbacks fire; layout does not). See `docs/scope.md` for the precise list.
