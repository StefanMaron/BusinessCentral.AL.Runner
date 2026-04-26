# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Vision

Run Business Central AL unit tests in milliseconds — no BC service tier, no Docker, no SQL Server, no license.

The goal is **broad AL language compatibility**: any AL codeunit that can run without the BC service tier should compile and execute here. All AL code should compile without modification. The only acceptable reason to restructure AL code is to implement dependency injection or stubs for features that are architecturally out of scope (e.g. actual HTTP network calls, real page rendering).

Hard architectural limits (parallel session contracts, real transaction isolation, service-tier rendering, HTTP network I/O) are the only non-negotiable gaps. Everything else is a gap to close. See `docs/limitations.md` for the full breakdown.

---

## TDD Is Non-Negotiable

**Every feature, fix, or mock addition requires a test. No exceptions.**

The test suite is what makes this project credible to AL developers. Without it, nobody can trust the runner. Coverage is the only proof that the runner works correctly.

Strict red/green workflow:
1. **RED** — Write the failing AL test first. Run it and confirm it fails.
2. **GREEN** — Implement the fix. Run again and confirm it passes.
3. Never write implementation code without a prior failing test.

Every test must cover **both directions**:
- **Positive**: correct input produces the expected result (`Assert.AreEqual`)
- **Negative**: invalid input fails with the right error (`asserterror` + `Assert.ExpectedError`)

A test that only proves the happy path is incomplete — a mock that always returns a default value would also pass.

**Tests must prove, not just pass.** Before marking a test complete, ask: "would this catch a broken or no-op mock?" If the test would pass even if the implementation always returned a default value, it is not a proving test — it is noise. Assert specific expected values, not just absence of errors.

### What a proving test looks like

A skeptic unfamiliar with the codebase must be able to read any test and say: "yes, if this passes, I am confident that feature X works correctly in al-runner." Every test must satisfy all of the following:

**1. Positive case with a specific assertion**
```al
// Weak — proves almost nothing:
MyProc();  // just runs, no assertion

// Strong — proves the output is correct:
Result := MyProc(3, 4);
Assert.AreEqual(7, Result, 'MyProc should return the sum');
```

**2. Negative case with a specific error**
```al
// Weak — proves something fails, but not what or why:
asserterror MyProc(-1);

// Strong — proves the right error fires:
asserterror MyProc(-1);
Assert.ExpectedError('Value must be positive');
```

**3. The test would catch a broken mock**
If the test passes even when the mock always returns a default value (0, `''`, `false`), it is not a proving test. Assert a non-default concrete value whenever possible, so a stub that ignores its input cannot pass.

**4. Use `Assert.*` — not `if X then Error(...)`**
Custom `if/Error` guards are easy to get wrong (wrong condition, missing coverage). Always use `Assert.AreEqual`, `Assert.IsTrue`, `Assert.IsFalse`, or `Assert.ExpectedError` so failures produce clear diagnostics.

Exception: "no-op stub" tests (`Hyperlink_NoThrow`, `Report_Run_IsNoOp`, etc.) are valid when the *entire* claim is "this does not crash." The name must make that explicit.

---

## Filing Issues for Gaps

When you encounter a gap, a missing AL feature, or unexpected behavior during work in this repo:

1. **File a GitHub issue immediately** — do not silently work around it
2. Use the runner-gap issue template (`.github/ISSUE_TEMPLATE/runner-gap.md`)
3. Track every gap — the goal is to wipe them out systematically

If AL code fails to run and the reason is not in `docs/limitations.md`, it is a runner gap, not a problem with the AL code. Report it.

---

## Pipeline Architecture

```
AL Source directories
    ↓  BC Compilation.Emit()           [Microsoft.Dynamics.Nav.CodeAnalysis]
Generated C# (per-object files, BC runtime types)
    ↓  RoslynRewriter                  [CSharpSyntaxRewriter — AST-level rewrites]
Rewritten C# (BC types → mock types)
    ↓  RoslynCompiler                  [Roslyn in-memory compilation]
.NET Assembly (in-memory)
    ↓  Executor                        [test discovery + invocation]
Test Results: N/M PASS
```

- **Stage 1 (AlTranspiler)** — `Program.cs`: BC compiler's public API transpiles AL objects to C# classes.
- **Stage 2 (RoslynRewriter)** — `RoslynRewriter.cs`: AST-level CSharpSyntaxRewriter replaces BC runtime types with mock types, strips BC attributes, injects `using AlRunner.Runtime;`.
- **Stage 3 (RoslynCompiler)** — `Program.cs`: Roslyn in-memory compilation against BC Service Tier DLLs + `AlRunner.Runtime`. No disk writes.
- **Stage 4 (Executor)** — `Program.cs`: discovers `[NavTest]` methods, resets in-memory tables between tests, reports pass/fail.

The runner uses **real BC types** (`NavText`, `Decimal18`, `NavOption`, etc.) from Microsoft DLLs. Only the I/O boundary is mocked (database, session, UI). See `README.md` for the full architecture explanation.

---

## Test Structure

Test suites are grouped into **thematic categories** under three top-level buckets:

```
tests/
  bucket-1/                 ← backend logic
    record-table/           — record / table / field / filter / database / permissions
    codeunit-runtime/       — codeunit / event / dialog / error / scope / handler / session / library / language features
  bucket-2/                 ← presentation + data
    page-report/            — page / testpage / report / xmlport / query / action / views / fieldgroup
    data-formats/           — text / json / xml / date / numeric / format / stream / http / blob / media
  bucket-feature-niw/       ← suites that need a separate compile unit due to AL feature flags
                            uses `"features": ["NoImplicitWith"]` in app.json; flat layout
                            (no category subfolder) since this bucket holds a single theme
  stubs/                    ← 39-stubs (requires --stubs flag, separate invocation)
  excluded/                 ← fixtures not in the main loop
```

Each suite:
```
tests/<bucket>/<category>/<NN-descriptive-name>/
  src/   — AL source codeunit(s) exercising the feature
  test/  — AL test codeunit (Subtype = Test)
```

**When adding a new suite:** pick the matching `<bucket>/<category>` folder. AL object IDs must be unique within a top-level bucket (suites in the same bucket compile together); IDs may repeat across buckets. Reuse the next free `NN-` prefix in the chosen category.

### Running tests

```bash
# Run all buckets (mirrors .github/workflows/test-matrix.yml)
for bucket in tests/bucket-*/; do
  args=""
  # Suites live two levels deep: bucket/<category>/<suite>/{src,test,app*}
  for suite in "$bucket"*/*/; do
    [ -d "${suite}src"  ] && args="$args ${suite}src"
    for appdir in "${suite}"app*/; do
      [ -d "$appdir" ] && args="$args $appdir"
    done
    [ -d "${suite}test" ] && args="$args ${suite}test"
  done
  dotnet run --project AlRunner --framework net10.0 -- --strict --test-isolation method $args
done

# Stubs test (separate invocation)
dotnet run --project AlRunner --framework net10.0 -- --stubs tests/stubs/39-stubs/stubs tests/stubs/39-stubs/src tests/stubs/39-stubs/test
```

### Build

```bash
dotnet build AlRunner/
dotnet run --project AlRunner -- ./src ./test
```

---

## Agent Working Rules

1. **Strict red/green TDD** — failing test first, then implementation. Always.

2. **Documentation must stay current** — any change affecting behavior, CLI flags, or mock capabilities must be reflected in:
   - `README.md` (supported/unsupported feature list, CLI flags)
   - `--guide` output (`PrintGuide()` in `Program.cs`) — the primary way external agents discover runner capabilities
   - `docs/limitations.md` (if the change affects known gaps)

3. **File issues for gaps** — never silently work around a gap; always report it.

4. **Best solution, not easiest** — avoid shortcuts that create technical debt.

5. **SOLID & DRY** — no duplicate logic; single-purpose helpers; composition over inheritance.

---

## Key Files

| File | Role |
|---|---|
| `AlRunner/Program.cs` | CLI entry point, AlTranspiler, RoslynCompiler, Executor, PrintGuide |
| `AlRunner/RoslynRewriter.cs` | BC→mock type transformations (AST-level CSharpSyntaxRewriter) |
| `AlRunner/DiagnosticClassifier.cs` | AL diagnostic message parser |
| `AlRunner/PackageScanner.cs` | .app file scanning and deduplication |
| `AlRunner/StubGenerator.cs` | `--generate-stubs` command |
| `AlRunner/Runtime/AlScope.cs` | Base scope, AlDialog (Error/Message + ErrorInfo), AlCompat (Format, RoundDateTime, utility stubs), MockDialog, collectible errors state |
| `AlRunner/Runtime/MockRecordHandle.cs` | In-memory record store (filtering, composite PKs, sort, triggers, temp records, CalcFields) |
| `AlRunner/Runtime/MockCodeunitHandle.cs` | Cross-codeunit dispatch via reflection |
| `AlRunner/Runtime/EventSubscriberRegistry.cs` | Event subscriber discovery + dispatch |
| `AlRunner/Runtime/HandlerRegistry.cs` | Test handler dispatch (ConfirmHandler, MessageHandler, ModalPageHandler, RequestPageHandler, ReportHandler, SendNotificationHandler) |
| `AlRunner/Runtime/MockTestPageHandle.cs` | TestPage mock with full lifecycle, field access, navigation, Editable, ValidationErrorCount, Last, Previous, Expand, GetRecord |
| `AlRunner/Runtime/MockRecordRef.cs` | RecordRef backed by MockRecordHandle; Mark/MarkedOnly/ClearMarks (functional), Rename, FieldExists, HasFilter, GetPosition, Ascending (get/set), ModifyAll, KeyCount/KeyIndex/CurrentKeyIndex, system-field number accessors |
| `AlRunner/Runtime/MockFieldRef.cs` | FieldRef with value get/set, range/filter, GetFilter, GetRangeMin/Max, Record(), Name/Caption/Type/Length from metadata, enum introspection (IsEnum, EnumValueCount, GetEnumValueName/Caption/Ordinal), CalcSum, ALSetTable (no-op stub) |
| `AlRunner/Runtime/MockKeyRef.cs` | KeyRef mock: FieldCount, FieldIndex(n), Record, Active, ALAssign |
| `AlRunner/Runtime/TableFieldRegistry.cs` | Transpile-time AL field metadata registry (field name/caption/type/length, table name/caption, enum field names, PK extraction) |
| `AlRunner/Runtime/MockNotification.cs` | In-memory Notification mock: Message, Send (dispatches to SendNotificationHandler), Recall, SetData/GetData/HasData, AddAction, Id, Scope |
| `AlRunner/Runtime/MockTaskScheduler.cs` | TaskScheduler stubs: CreateTask (sync dispatch), TaskExists, CancelTask, SetTaskReady |
| `AlRunner/Runtime/MockBigText.cs` | BigText mock: ALAddText, ALGetSubText, ALTextPos, ALLength, ALWrite, ALRead (StringBuilder-backed) |
| `AlRunner/Runtime/MockDataTransfer.cs` | DataTransfer stubs: SetTables, AddFieldValue, AddConstantValue, CopyFields, CopyRows (no-ops) |
| `AlRunner/stubs/LibraryAssert.al` | AL stub for codeunit 130 (auto-loaded) |
| `AlRunner/stubs/LibraryVariableStorage.al` | AL stub for codeunit 131004 (auto-loaded) |
| `docs/limitations.md` | Full breakdown of architectural limits and behavioral differences |
| `.github/copilot-instructions.md` | PR review checklist (mirrors these rules) |

---

## `--guide` Flag

`al-runner --guide` prints a comprehensive test-writing reference for AI agents. It is the primary discovery mechanism for external agents. **Always update `PrintGuide()` in `Program.cs` when runner capabilities change.**

---

## Agent workflow

This repository uses a multi-agent workflow. See `AGENTS.md` for the full contract. The short version:

- **Implementation agents** (`agent: impl-1`, `agent: impl-2`): pick up issues labeled for them, implement with TDD, open a PR labeled `status: review-ready`, wait for orchestrator review and auto-merge.
- **Orchestrator** (`agent: orchestrator`): assigns issues to idle impl agents, reviews PRs, enables auto-merge, replenishes the issue queue.
- **Branch naming**: `agent/<agent-id>/issue-<N>` — always
- **Never push directly to `main`** — always via PR
- **Never self-assign** — impl agents only work on issues the orchestrator has labeled for them
