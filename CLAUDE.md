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
| `MockAssert` | Codeunit 130 "Library Assert" | Assert.AreEqual, AreNotEqual, IsTrue, IsFalse, ExpectedError. |
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

### Removed out-of-scope files

The following have been removed (they were stubs for unsupported features):

- `MockFormHandle.cs` — Page mocking (not supported in standalone mode)
- `NavTestPageHandle.cs` — TestPage mocking (not supported)
- `MockRecordRef.cs` — RecordRef mocking (not supported)
- `RegexRewriter` class — dead code superseded by `RoslynRewriter`

---

## Agent Guide (`--guide`)

The CLI has a `--guide` flag that prints a comprehensive test-writing reference
for AI coding agents. The guide is emitted by `PrintGuide()` in `Program.cs`.

**When modifying the runner's capabilities (new mock methods, new supported AL
features, new CLI flags), update the guide text in `PrintGuide()` to match.**
The guide is the primary way external agents discover what the runner supports.

The `--help` output references the guide at the bottom so agents discover it
automatically when they run `al-runner -h`.

---

## Testing Requirements

**Every feature, fix, or mock addition MUST have a corresponding test case in
`tests/`. No exceptions.** The test suite is the proof that the runner works.
Without it, nobody can trust the tool.

### Test structure
Each test case is a directory under `tests/NN-name/` with:
- `src/*.al` — AL source code exercising the feature
- `test/*.al` — Test codeunit (`Subtype = Test`) using `Assert: Codeunit Assert`
- `al-runner.json` — config file

### Positive AND negative tests
Every test case must prove BOTH that correct input succeeds AND that incorrect
input fails as expected:

- **Positive**: Call the logic with valid input, `Assert.AreEqual` on the result
- **Negative**: Call with invalid input, use `asserterror` + `Assert.ExpectedError`
  to verify the runner catches errors correctly

If a test only proves the happy path, it doesn't prove the runner works — a mock
that always returns a default value would also pass. Negative tests prove that
the runner actually executes the logic and catches failures.

### When to add tests
- **New mock method** (e.g., `MockRecordHandle.ALRecordId`) → test that calls it
- **New rewriter rule** (e.g., `ALIsolatedStorage → MockIsolatedStorage`) → test
  with AL code that uses the feature
- **Bug fix** → RED test first (fails without fix), then GREEN (passes with fix)
- **New CLI flag** → test that exercises it

### Running tests
```bash
# All tests (auto-discovered, excludes 06-intentional-failure)
for s in $(ls -d tests/*/src | sed 's|tests/||;s|/src||' | grep -v '06-' | sort); do
  al-runner "tests/$s/src" "tests/$s/test"
done
```

### CI
Tests run against a matrix of BC versions (26.0–27.5) on every push. The publish
workflow gates NuGet release on all tests passing across all versions.

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

## Implemented Features (previously listed as missing)

These have been implemented and are tested by the samples:

1. **Assert codeunit mock** (`Runtime/MockAssert.cs`) — `Assert.AreEqual`,
   `Assert.AreNotEqual`, `Assert.IsTrue`, `Assert.IsFalse`, `Assert.ExpectedError`,
   `Assert.ExpectedErrorCode`. Wired into `MockCodeunitHandle` so calls to
   codeunit 130 (Library Assert) route to `MockAssert`. An AL stub
   (`stubs/LibraryAssert.al`) is auto-loaded so test code compiles without
   requiring the real Assert .app.

2. **Composite primary key support** — `ALGet`, `ALModify`, `ALDelete`, `ALRename`
   use all PK fields registered via `MockRecordHandle.RegisterPrimaryKey()`.
   Falls back to field 1 when no PK is registered.

3. **Sort ordering** — `ALSetCurrentKey` / `ALSetAscending` are applied in
   `GetFilteredRecords()`. `ALFind("+")` returns the record with the largest
   key value.

4. **ALFieldNo(fieldName)** — Field-by-name lookups work when registered via
   `MockRecordHandle.RegisterFieldName()`.

## Remaining Gaps

These are gaps that remain for full production use:

---

## CLI Usage

### Current (dotnet run)
```bash
# Run a single .al file
dotnet run --project AlRunner -- tests/01-pure-function/src

# Run all .al files in a directory (test mode auto-detected)
dotnet run --project AlRunner -- ./src ./test

# Run from .app packages with dependency resolution
dotnet run --project AlRunner -- --packages ./packages MyApp.app MyApp.Tests.app

# Dump generated C# (before rewriting)
dotnet run --project AlRunner -- --dump-csharp tests/01-pure-function/src

# Dump rewritten C# (after RoslynRewriter)
dotnet run --project AlRunner -- --dump-rewritten tests/01-pure-function/src

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
| `AlRunner/Runtime/MockAssert.cs` | Assert codeunit mock (AreEqual, ExpectedError, etc.) |
| `AlRunner/stubs/LibraryAssert.al` | AL stub for codeunit 130 (auto-loaded for compilation) |
| `tests/01-pure-function/` | Pure calculation tests with Assert.AreEqual |
| `tests/02-record-operations/` | Record CRUD, filtering, composite PKs |
| `tests/03-interface-injection/` | AL interface dependency injection |
| `tests/04-asserterror/` | asserterror + Assert.ExpectedError |
| `tests/05-known-limitation/` | Silent false positive documentation |
| `tests/06-intentional-failure/` | Deliberately broken tests for error output demo |
| `.github/workflows/samples-pass.yml` | CI: runs samples 01-05 (should pass) |
| `.github/workflows/samples-fail.yml` | CI: runs sample 06 (expected to fail) |
| `al-runner.json` | Sample config file (not yet wired into CLI) |

---

## Continuing Development

When resuming work on this repo as an agent, you do NOT need to re-read the
alDirectCompile repo. Everything needed is in this CLAUDE.md and the source files.

Priority order for next work:
1. Wire al-runner.json config into the CLI
2. Implement filter groups (FilterGroup)
3. Implement ALGetFilter to return actual filter expressions
4. Add more Assert methods (AreNearlyEqual, Fail, etc.)
5. Improve PK auto-detection from generated C# metadata
