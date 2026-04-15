# BusinessCentral.AL.Runner

[![Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml/badge.svg)](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml)
[![NuGet](https://img.shields.io/nuget/v/MSDyn365BC.AL.Runner)](https://www.nuget.org/packages/MSDyn365BC.AL.Runner)

Run Business Central AL unit tests in **milliseconds** — no BC service tier, no Docker, no SQL Server, no license required.

## What It Is

AL Runner is a standalone test executor for Business Central codeunits. It transpiles AL source to C# using the BC compiler's public API, rewrites the generated C# to replace BC runtime types with in-memory mocks, compiles everything with Roslyn, and executes your test codeunits directly.

```
AL Source
  ↓  BC Compilation.Emit()
Generated C#
  ↓  RoslynRewriter (BC types → mocks)
Rewritten C#
  ↓  Roslyn in-memory compile
.NET Assembly
  ↓  Test discovery + execution
Results in milliseconds
```

It targets broad AL language compatibility. For known gaps, see [What's Missing](#whats-missing) below.

## Why

Running a full BC CI pipeline (compile, publish, initialize, run tests) takes 45+ minutes. AL Runner makes the unit test portion take under a second, giving you a fast inner loop for codeunit logic that can run without the BC service tier.

AL Runner is designed to run **before** the full BC service tier pipeline as a fast pre-check. It does not replace the full pipeline.

## What It Supports

**Supported:**
- Codeunit logic (fields, variables, arithmetic, string ops, enums/options)
- In-memory record store: Init, Insert, Modify, Get, Delete, DeleteAll, FindFirst, FindLast, FindSet, Next
- Composite primary keys, sort ordering (SetCurrentKey/SetAscending)
- SETRANGE and SETFILTER filtering (=, <>, <, <=, >, >=, wildcards, OR separators)
- Cross-codeunit dispatch via MockCodeunitHandle
- Assert codeunit (ID 130): AreEqual, AreNotEqual, IsTrue, IsFalse, ExpectedError, RecordIsEmpty, etc.
- `asserterror` keyword (catches expected errors) + `GetLastErrorText()`
- `Error()` / `Message()` — Error throws an exception; Message writes to console
- ErrorInfo type with collectible errors: `Error(ErrorInfo)`, `HasCollectedErrors()`, `GetCollectedErrors()`,
  `ClearCollectedErrors()`, `IsCollectingErrors()`, `[ErrorBehavior(ErrorBehavior::Collect)]`
- OnValidate triggers on table fields
- Table procedures (custom procedures on table objects)
- IsolatedStorage (in-memory key-value store)
- TextBuilder (in-memory string builder)
- Format/Evaluate type conversions
- AL interfaces injected by test code
- AL arrays (MockArray, MockRecordArray)
- AL Variant (MockVariant)
- RecordRef / FieldRef (Open, Close, Field(n).Value get/set, Insert, Modify, Delete, DeleteAll, FindSet, Next, GetTable, SetTable, SetRange, SetFilter, RecordId, SetLoadFields)
- JSON types (JsonObject, JsonArray, JsonToken, JsonValue): Add, Get, Contains, Remove, Replace, Count, WriteTo, ReadFrom, SelectToken, AsValue, AsText, AsInteger, etc.
- BLOB / InStream / OutStream — CreateInStream/CreateOutStream, HasValue, ReadText/WriteText (in-memory)
- Library - Variable Storage (codeunit 131004) — Enqueue, Dequeue*, AssertEmpty, Clear, IsEmpty
- TestPage navigation — Caption, First(), GoToKey(), GoToRecord(), Next(), New(), GetPart(), Filter.SetFilter()/GetFilter()
- Request page handler dispatch (`[RequestPageHandler]`)
- Limited report-handle support — `SetTableView()`, helper-procedure dispatch, `Run()`, `RunRequestPage()`
- Built-in session functions: CompanyName, UserId, TenantId, SerialNumber (return empty string)
- Input from .al files, directories, or .app packages
- Partial compilation (skips unsupported object types like XMLport)
- Stub files (`--stubs <dir>`) for replacing unsupported dependencies
- Stub generation (`--generate-stubs`) from .app symbol packages
- Statement-level coverage reporting (`--coverage`, outputs cobertura.xml)
- Per-iteration loop tracking (`--iteration-tracking`)
- Machine-readable JSON output (`--output-json`)

**Not supported (by design):**
- Page and report rendering fidelity — inject via AL interface or exclude from runner when correctness depends on real BC UI/runtime behavior
- XmlPort — variables compile and surrounding logic runs; `Import()`/`Export()` throw at runtime (XmlPort I/O requires the BC service tier)
- HTTP requests — inject via AL interface or exclude from runner
- Event subscribers — implicit events (OnAfterModify, OnAfterInsert, etc.) do NOT fire
- .app file loading as test input (source directories only; .app supported for symbol references)
- Filter groups (FilterGroup)

## What It Doesn't Support (and Why)

The items listed above are **architectural limits** — they require the BC service tier and cannot be emulated in a single .NET process. Everything else is either already supported or a gap being actively closed.

If AL code fails to run and the reason isn't in the architectural list above, that is likely a runner gap rather than a problem with your code. Report it at https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

## Pipeline Outcomes

When al-runner executes a test codeunit, exactly one of three things happens:

**1. FAIL — test failure caught**
An assertion failed or the test threw an exception. This is a real failure. Pipeline stops immediately. If al-runner says FAIL, it is a real failure.

**2. ERROR — runner cannot execute the codeunit**
The codeunit depends on an unsupported feature and crashes. This is a configuration error, not a test failure. Fix it by either removing that codeunit from the runner config, or injecting the missing dependency via an AL interface.

**3. PASS**
The codeunit's direct logic is correct. Note: if the test implicitly depends on an event subscriber (e.g., `OnAfterModify` fires a trigger that modifies state the test then asserts), the runner will PASS silently because implicit events don't fire. The full BC service tier pipeline runs after the runner and catches these cases.

**The guarantee:** if al-runner says FAIL, it is a real failure. Silent passes due to missing event subscribers are an accepted known limitation — always run the full pipeline after al-runner.

## What's Missing

Known gaps for real-world use:

1. **Implicit event publishers on DB operations** — `OnAfterModify`, `OnAfterInsert`, etc. do NOT fire. Tests that depend on event subscribers will produce silent false positives (see test 05).
2. **TestPage / request page / report (partial)** — field access, lifecycle (`OpenEdit`/`Close`/`New`), actions (`OK`/`Cancel`), navigation (`First()`, `GoToKey()`, `GoToRecord()`, `Next()`, `GetPart()`, `Filter.SetFilter()/GetFilter()`), and `[ConfirmHandler]`/`[MessageHandler]`/`[ModalPageHandler]`/`[RequestPageHandler]` work. Report variables support limited standalone operations such as `SetTableView()`, helper dispatch, `Run()`, and `RunRequestPage()`. Real page/report rendering and full UI metadata evaluation are still not supported.
3. **XmlPort (partial)** — `XmlPort "X"` variables compile via `MockXmlPortHandle`. Accessing `XmlPortId` and `GetStatus()` style logic works. `Import()`/`Export()` (both instance and static `XmlPort.Import(portId, stream)`) throw `NotSupportedException` at runtime — XmlPort I/O requires the BC service tier. Inject via AL interface to make it testable.
4. **HTTP** — not supported. Inject via AL interface.
5. **Filter groups** (FilterGroup) — not tracked.
6. **ALGetFilter** — returns empty string even when filters are active.

## Developer Contract

Design your codeunits for testability by injecting dependencies via AL interfaces:

```al
// Define the interface
interface IInventoryCheck
    procedure HasStock(ItemNo: Code[20]): Boolean;
end

// Inject it into the codeunit
codeunit 50100 OrderProcessor
    procedure Process(ItemNo: Code[20]; Checker: Interface IInventoryCheck)
    begin
        if not Checker.HasStock(ItemNo) then
            Error('Item %1 is out of stock', ItemNo);
        // ... rest of logic
    end;
end

// In your test codeunit:
// Implement IInventoryCheck with a stub that always returns true/false
```

Anything you can't inject cannot be unit-tested by this runner — and that's the right boundary.

## Quick Start

### Install

```bash
dotnet tool install --global MSDyn365BC.AL.Runner
```

That's it. On first build/run, the AL compiler (~57 MB from NuGet) and BC Service Tier DLLs (~11 MB via HTTP range requests) are downloaded automatically and cached. No manual setup, works on Windows, Linux, and macOS.

### Run

```bash
# Run test codeunits (test mode auto-detected when Subtype = Test is present)
al-runner ./src ./test

# Run with coverage report
al-runner --coverage ./src ./test

# Load from .app packages with dependency resolution
al-runner --packages ./packages MyApp.app MyApp.Tests.app

# Provide stub AL files for unsupported dependencies
al-runner --stubs ./stubs ./src ./test

# Verbose output (show transpilation/compilation details)
al-runner -v ./src ./test

# Machine-readable JSON output
al-runner --output-json ./src ./test

# Run a single test procedure by name
al-runner --run TestMyProcedure ./src ./test

# Track per-iteration loop data (requires --output-json)
al-runner --iteration-tracking --output-json ./src ./test

# Capture variable values after each test for inline display
al-runner --capture-values ./src ./test

# Generate stub AL files from .app symbol packages
al-runner --generate-stubs .alpackages ./stubs
al-runner --generate-stubs .alpackages ./stubs ./src ./test  # only referenced codeunits

# Run inline AL code
al-runner -e 'codeunit 99 X { trigger OnRun() begin Message('"'"'hi'"'"'); end; }'

# Print test-writing guide for AI agents
al-runner --guide

# Debug: dump generated C# before and after rewriting
al-runner --dump-csharp ./src
al-runner --dump-rewritten ./src
```

### Build from source

```bash
dotnet build AlRunner/
dotnet run --project AlRunner -- ./src ./test
```

### Config file (al-runner.json)

```json
{
  "sourcePath": "./src",
  "testPath": "./test",
  "testCodeunits": [50200, 50201]
}
```

Config-based invocation is not yet wired into the CLI (future work).

## How It Fits in the Full Pipeline

AL Runner is designed to sit before the full BC service tier in CI:

```
Pull Request
  ↓
al-runner (seconds) — catches AL logic failures fast
  ↓ (only if al-runner passes)
Full BC pipeline (MsDyn365Bc.On.Linux, 45+ min) — full fidelity test execution
```

The full BC service tier pipeline:
- https://github.com/StefanMaron/MsDyn365Bc.On.Linux

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | All tests passed |
| `1` | Test assertion failures (real bugs in code) or usage/argument error |
| `2` | Runner limitations only (no assertion failures; all blocked tests are due to Roslyn compilation gaps or missing mock support) |
| `3` | AL compilation error (the AL source itself does not compile) |

Use exit codes in CI to distinguish failure modes:

```bash
al-runner --packages .alpackages ./src ./test
# Exit 0 = all pass, non-zero = failure
# Exit 2 specifically indicates runner limitations (blocked tests)
```

## How It Works

AL Runner has a 4-stage pipeline:

```
AL source (.al files)
  ↓  BC Compilation.Emit()        Transpiles AL to C# using the BC compiler's public API
  ↓  RoslynRewriter               Rewrites BC runtime types to in-memory mocks (AST-level)
  ↓  Roslyn in-memory compile     Compiles the rewritten C# against BC Service Tier DLLs
  ↓  Executor                     Discovers [NavTest] methods, runs them, reports results
```

**Stage 1 — AL Transpiler**: Uses `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` to convert each AL object (table, codeunit) into a C# class. The AL compiler is downloaded from NuGet automatically on first build.

**Stage 2 — RoslynRewriter**: A `CSharpSyntaxRewriter` that transforms the generated C# for standalone execution. Replaces `NavRecordHandle` → `MockRecordHandle`, `NavCodeunitHandle` → `MockCodeunitHandle`, strips BC attributes, rewrites `NavDialog.ALMessage` → `AlDialog.Message`, etc. This is where the BC runtime dependency is severed.

**Stage 3 — RoslynCompiler**: Compiles the rewritten C# in-memory with Roslyn. References the BC Service Tier DLLs (auto-downloaded from the BC artifact CDN via HTTP range requests — ~11 MB instead of the full 1.2 GB artifact). No files written to disk.

**Stage 4 — Executor**: Discovers test codeunits (classes with `[NavTest]` methods), resets the in-memory table store between tests, invokes each test via reflection, and reports pass/fail/error with coverage.

All dependencies are auto-downloaded and cached. The only prerequisite is .NET 8 SDK.

## Test Cases

The `tests/` directory contains 81 self-contained AL projects (`src/` + `test/`), each exercising a specific runner capability. Every push runs all of them against a [matrix of BC versions](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml) (26.0 through 27.5).

When a new scenario is encountered that should work but doesn't, it gets triaged:
- **In scope** → add a test case, fix the runner, verify it passes across all BC versions
- **Out of scope** → document as a known limitation (like test 05)

To add a test case: create `tests/NN-name/src/*.al` and `tests/NN-name/test/*.al`. The CI workflow auto-discovers all test directories (except `06-intentional-failure`).

## CI

The [Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml) runs on every push — resolving the latest patch version for each BC major.minor, building and testing in parallel. The [Publish](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/publish.yml) workflow pushes to NuGet only when all versions pass (triggered by `git tag v*`).

## Naming

Follows the `BusinessCentral.AL.*` convention:
- [BusinessCentral.AL.Mutations](https://github.com/StefanMaron/BusinessCentral.AL.Mutations) — mutation testing
- BusinessCentral.AL.Runner — this repo

## License

MIT
