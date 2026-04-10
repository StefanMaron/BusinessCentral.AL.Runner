# BusinessCentral.AL.Runner — Agent Working Document

## Vision

Run Business Central AL unit tests in milliseconds, with no BC service tier, no
Docker, no SQL Server, and no license. The goal is a fast feedback loop for
pure-logic codeunits that don't depend on UI, HTTP, or external services.

This is a proof of concept that works for simple cases. Real-world use requires
the missing pieces described below to be implemented first.

---

## Pipeline: How It Works

```
AL Source directories / .app files
    ↓  BC Compilation.Emit()           [Microsoft.Dynamics.Nav.CodeAnalysis]
Generated C# (per-object files, BC runtime types)
    ↓  RoslynRewriter                  [CSharpSyntaxRewriter — AST-level rewrites]
Rewritten C# (BC types → mock types)
    ↓  RoslynCompiler                  [Roslyn in-memory compilation]
.NET Assembly (in-memory)
    ↓  Executor                        [test discovery + invocation]
Test Results: N/M PASS
```

### Stage 1 — AlTranspiler (`Program.cs`, class `AlTranspiler`)

Uses `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` — the BC compiler's
public API — to transpile AL source to C#. Each AL object (table, codeunit, etc.)
becomes a separate C# class. The transpiler supports:
- Single .al files
- Directories of .al files
- .app packages (ZIP archives containing AL source)
- Symbol references via `--packages <dir>` for cross-app dependencies

### Stage 2 — RoslynRewriter (`RoslynRewriter.cs`)

A `CSharpSyntaxRewriter` that transforms BC-generated C# into standalone code.
Replaces the old regex-based approach (`RegexRewriter` — dead code, kept for
reference). Key transformations:
- Remove BC-specific attributes (`[NavCodeunitOptions]`, etc.)
- Replace BC runtime type references with mock types (see mock surface below)
- Strip ITreeObject arguments from method calls
- Remove `.Target` property chains on record handles
- Strip event subscription calls (`RunEvent`, `ALBindSubscription`, etc.)
- Inject `using AlRunner.Runtime;` into each generated namespace

### Stage 3 — RoslynCompiler (`Program.cs`, class `RoslynCompiler`)

Compiles the rewritten C# in-memory using Roslyn, referencing:
- BC Service Tier DLLs (`Microsoft.Dynamics.Nav.Ncl`, `.Nav.Types`, `.Nav.Common`,
  `.Nav.Language`, `.Nav.Core`, `.Nav.Types.Report.*`)
- `AlRunner.Runtime` (the mock implementations in `Runtime/`)

No files are written to disk. The resulting `Assembly` is held in memory.

### Stage 4 — Executor (`Program.cs`, class `Executor`)

- **Test mode**: Discovers codeunits with `Subtype = Test`, resets all in-memory
  tables between tests, calls each `[NavTest]`-attributed method, reports pass/fail.
- **Run mode**: Finds and calls `OnRun()` on the first codeunit in the assembly.

---

## Mock Surface (Runtime/)

These are the BC runtime types replaced in standalone mode:

| Mock class | Replaces | What it does |
|---|---|---|
| `AlScope` | `NavMethodScope<T>` | Base class for all codeunit scopes. Provides `AssertError()`. |
| `MockRecordHandle` | `NavRecordHandle` | In-memory table store with SETRANGE/SETFILTER filtering. |
| `MockCodeunitHandle` | `NavCodeunitHandle` | Cross-codeunit dispatch via reflection on generated classes. |
| `MockVariant` | `NavVariant` | Boxed variant value (any type). |
| `MockArray<T>` | `NavArray<T>` | Generic array without ITreeObject requirement. |
| `MockRecordArray` | `NavArray<NavRecordHandle>` | Array of MockRecordHandle (Record[] in AL). |
| `MockInterfaceHandle` | `NavInterfaceHandle` | AL interface dispatch stub. |
| `AlScope.AssertError()` | `asserterror` keyword | Catches expected errors, stores message. |
| `AlDialog` | `NavDialog` static methods | Message() prints to console; Error() throws Exception. |
| `MockDialog` | `NavDialog` instance | No-op progress dialog (ALOpen/ALUpdate/ALClose). |
| `AlCompat` | `ALCompiler`, `NavFormatEvaluateHelper` | Type conversion helpers; ALRandomize/ALRandom. |

### MockRecordHandle capabilities

- Init, Insert, Modify, Get, Delete, DeleteAll
- FindFirst, FindLast, FindSet, Next iteration
- SetRange, SetFilter with simple comparisons (=, <>, <, <=, >, >=)
- Filter expressions with wildcards (\*) and OR separators (|)
- Count, IsEmpty
- Field read/write by field ID (GetFieldValueSafe / SetFieldValueSafe)
- Cross-record table reset via `ResetAll()` between tests

### Out-of-scope files (flagged with `// OUT OF SCOPE — see CLAUDE.md`)

These files exist in `Runtime/` but are outside the current scope. Stefan will
decide whether to keep, promote, or delete them:

- `MockFormHandle.cs` — Page mocking (NavFormHandle). Page interactions (PAGE.RUN,
  lookups) are not supported in standalone mode.
- `NavTestPageHandle.cs` — TestPage mocking (NavTestPageHandle). Tests using
  `TestPage` variables cannot run without a UI runtime.
- `MockRecordRef.cs` — RecordRef mocking. Field access via NavFieldRef is not
  functional; the stub satisfies compilation only.

The `RegexRewriter` class at the bottom of `Program.cs` is also marked
OUT OF SCOPE — it is dead code superseded by `RoslynRewriter`.

---

## Developer Contract

**What you can unit-test with al-runner:**
Pure-logic codeunits that operate on records (tables) and call other codeunits.
The codeunit must not depend on Page, Report, XMLPort, HTTP, events, or external
services to execute its core logic.

**What you cannot unit-test with al-runner (by design):**
Anything that requires the BC service tier. If you can't inject the dependency
via an AL interface, the function is not unit-testable in standalone mode. This
is a deliberate design boundary, not a bug to fix.

**The injection pattern:**
Define an AL interface for the dependency and inject it into the codeunit via
a procedure parameter. The test provides a mock implementation; the production
code uses the real one. Example:
```al
interface IMailSender
procedure Send(Subject: Text; Body: Text): Boolean;
end

codeunit 50200 MyProcessor
procedure Process(Sender: Interface IMailSender)
// ... logic that calls Sender.Send(...)
end
```

---

## Known Limitations (by design)

- **Implicit event publishers on DB operations** — OnAfterModify, OnAfterInsert,
  OnAfterDelete, etc. are NOT fired. The DB trigger pipeline is not implemented.
- **Page, Report, XMLPort** — NOT supported. Developer must inject via AL interfaces.
- **HTTP** — NOT supported. Developer must inject via AL interfaces.
- **Events/subscribers** — NOT supported. `RunEvent`, `ALBindSubscription`,
  `ALUnbindSubscription` are no-ops.
- **.app file loading** — NOT supported for test input. Always runs from AL source
  directories. Dependencies can be loaded from .app for symbol references only.
- **Filter groups** (FilterGroup) — not tracked.
- **ALGetFilter** — returns empty string even when filters are active.

---

## Missing Pieces (needed for real-world use, not yet built)

These are gaps that must be addressed before al-runner is useful on real app test
suites:

1. **Assert codeunit mock** — `Assert.AreEqual`, `Assert.IsTrue`, `Assert.IsFalse`,
   `Assert.ExpectedError`, `Assert.AreNotEqual` are not implemented. Most real BC
   tests use this codeunit. Without it, nearly all real test suites will fail.

2. **Composite primary key support in MockRecordHandle** — `ALGet` only supports
   single-field primary keys. Real tables often have multi-field keys.

3. **Sort ordering in GetFilteredRecords** — `ALSetCurrentKey` / `ALSetAscending`
   are stored but not applied during `FindSet`. Results are returned in insertion order.

4. **ALFieldNo(fieldName) returns 0** — Field-by-name lookups are not supported.
   Code that resolves field numbers by name at runtime will silently get 0.

---

## CLI Usage

### Current (dotnet run)
```bash
# Run a single .al file
dotnet run --project AlRunner -- samples/hello.al

# Run all .al files in a directory (test mode auto-detected)
dotnet run --project AlRunner -- ./src ./test

# Run from .app packages with dependency resolution
dotnet run --project AlRunner -- --packages ./packages MyApp.app MyApp.Tests.app

# Dump generated C# (before rewriting)
dotnet run --project AlRunner -- --dump-csharp samples/hello.al

# Dump rewritten C# (after RoslynRewriter)
dotnet run --project AlRunner -- --dump-rewritten samples/hello.al

# Run inline AL code
dotnet run --project AlRunner -- -e 'codeunit 99 X { trigger OnRun() begin Message('"'"'hi'"'"'); end; }'
```

### Config file (al-runner.json)
Place in the project root. Not yet wired into the CLI (future work):
```json
{
  "sourcePath": "./src",
  "testPath": "./test",
  "testCodeunits": [50200, 50201]
}
```

### Future CLI surface (proposed)
```bash
al-runner test --source ./src --tests ./test --codeunits 50200,50201
```

---

## Build & Bootstrap

**Prerequisites:**
- .NET 8 SDK
- AL compiler (installed as dotnet tool):
  `dotnet tool install microsoft.dynamics.businesscentral.development.tools.linux`
- BC Service Tier artifacts at `artifacts/onprem/27.5.46862.0/` (relative to
  `AlRunner/` project file). See alDirectCompile CLAUDE.md for download instructions.

**Build:**
```bash
dotnet build AlRunner/
```

**Run tests:**
```bash
dotnet run --project AlRunner -- ./src ./test
```

---

## BC-Linux Integration and Pipeline Outcomes

al-runner runs as a fast pre-check step before the full BC service tier pipeline
(MsDyn365Bc.On.Linux). The recommended workflow:

1. **al-runner** — run in CI in seconds for pure-logic unit tests (fast feedback)
2. **Full pipeline** — run the BC service tier (Docker + SQL) for integration/UI tests

The full pipeline lives at:
- https://github.com/StefanMaron/MsDyn365Bc.On.Linux

The alDirectCompile repo (`bc-linux` / `alDirectCompile`) contains the service tier
startup hook patches and the CI pipeline that orchestrates both approaches.

### Three possible outcomes when al-runner executes a test codeunit

**Outcome 1 — FAIL: test failure caught**
An assertion failed or an unhandled exception was thrown in test logic. This is a
real failure. Pipeline stops immediately. The runner correctly identified a bug.

**Outcome 2 — ERROR: runner cannot execute the codeunit**
The codeunit depends on an unsupported feature (Page, HTTP, event subscriber,
unmocked codeunit) and crashes during execution with a `NotSupportedException`
or similar. This is a configuration error, not a test failure. Pipeline stops.
The user must either:
- Remove that codeunit from the runner config (run it only in the full pipeline), or
- Inject the missing dependency via an AL interface so the runner can execute it.

**Outcome 3 — PASS (potential silent false positive)**
The runner reports PASS. However, if the test logic implicitly depends on an event
subscriber (e.g., OnAfterModify, OnAfterInsert) that modifies state and the test
asserts on that modified state — the runner will PASS because it does not fire
implicit events. The full BC service tier pipeline will catch this failure in the
subsequent step.

**The guarantee the runner provides:**
> If al-runner says FAIL, it is a real failure.
> If al-runner says PASS, the codeunit's direct logic is correct. Silent passes
> due to missing event subscribers are an accepted known limitation — the full
> BC pipeline always runs after the runner and catches them.

---

## Naming Convention

Follows the `BusinessCentral.AL.*` pattern:
- BusinessCentral.AL.Mutations — mutation testing
- BusinessCentral.AL.Runner — this repo (unit test runner)

---

## Key File Index

| File | Role |
|---|---|
| `AlRunner/Program.cs` | Main CLI + AlTranspiler + RoslynCompiler + Executor + AppPackageReader + Kernel32Shim |
| `AlRunner/RoslynRewriter.cs` | BC→mock type transformations (AST-level, replaces RegexRewriter) |
| `AlRunner/Runtime/AlScope.cs` | Base scope, AlDialog, AlCompat, MockDialog |
| `AlRunner/Runtime/MockRecordHandle.cs` | In-memory record store with filtering |
| `AlRunner/Runtime/MockCodeunitHandle.cs` | Cross-codeunit dispatch via reflection |
| `AlRunner/Runtime/MockVariant.cs` | AL Variant type replacement |
| `AlRunner/Runtime/MockArray.cs` | AL Array type replacement |
| `AlRunner/Runtime/MockRecordArray.cs` | AL Record array replacement |
| `AlRunner/Runtime/MockInterfaceHandle.cs` | AL Interface dispatch stub |
| `AlRunner/Runtime/MockFormHandle.cs` | OUT OF SCOPE: Page runtime mock |
| `AlRunner/Runtime/NavTestPageHandle.cs` | OUT OF SCOPE: TestPage mock |
| `AlRunner/Runtime/MockRecordRef.cs` | OUT OF SCOPE: RecordRef mock |
| `samples/hello.al` | Minimal sample: table + codeunit + Message |
| `samples/calc.al` | Minimal sample: arithmetic in OnRun |
| `al-runner.json` | Sample config file (not yet wired into CLI) |

---

## Continuing Development

When resuming work on this repo as an agent, you do NOT need to re-read the
alDirectCompile repo. Everything needed is in this CLAUDE.md and the source files.

Priority order for next work:
1. Implement the Assert codeunit mock (highest impact for real-world use)
2. Add composite primary key support to MockRecordHandle
3. Wire al-runner.json config into the CLI
4. Apply sort ordering in GetFilteredRecords
5. Implement ALFieldNo(fieldName) lookup
