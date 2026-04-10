# BusinessCentral.AL.Runner

[![Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml/badge.svg)](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml)
[![NuGet](https://img.shields.io/nuget/v/BusinessCentral.AL.Runner)](https://www.nuget.org/packages/BusinessCentral.AL.Runner)

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

This is a proof of concept. It works well for pure-logic codeunits. For real-world test suites, see [What's Missing](#whats-missing) below.

## Why

Running a full BC CI pipeline (compile, publish, initialize, run tests) takes 45+ minutes. AL Runner makes the pure-logic unit test portion take under a second, giving you a fast inner loop for codeunit logic that doesn't depend on UI, HTTP, or external services.

AL Runner is designed to run **before** the full BC service tier pipeline as a fast pre-check. It does not replace the full pipeline.

## What It Supports

**Supported:**
- Codeunit logic (fields, variables, arithmetic, string ops)
- In-memory record store: Init, Insert, Modify, Get, Delete, DeleteAll, FindFirst, FindLast, FindSet, Next
- SETRANGE and SETFILTER filtering (=, <>, <, <=, >, >=, wildcards, OR separators)
- Cross-codeunit dispatch via MockCodeunitHandle
- `asserterror` keyword (catches expected errors)
- `Error()` / `Message()` — Error throws an exception; Message writes to console
- AL interfaces injected by test code
- AL arrays (MockArray, MockRecordArray)
- AL Variant (MockVariant)
- Input from .al files, directories, or .app packages

**Not supported (by design):**
- Page, Report, XMLPort — inject via AL interface or exclude from runner
- HTTP requests — inject via AL interface or exclude from runner
- Event subscribers — implicit events (OnAfterModify, OnAfterInsert, etc.) do NOT fire
- .app file loading as test input (source directories only; .app supported for symbol references)
- Filter groups (FilterGroup)

## What It Doesn't Support (and Why That's OK)

The runner has a deliberate scope boundary: **if you can't inject a dependency via an AL interface, that code path isn't unit-testable in standalone mode**.

This is a design decision, not a bug. Code that truly depends on the BC service tier (page actions, HTTP calls, events fired by the DB tier) should be tested in the full pipeline. The runner covers the logic layer.

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

1. **Implicit event publishers on DB operations** — `OnAfterModify`, `OnAfterInsert`, etc. do NOT fire. Tests that depend on event subscribers will produce silent false positives (see sample 05).
2. **Page, Report, XMLPort** — not supported. Inject via AL interface or exclude from runner.
3. **HTTP** — not supported. Inject via AL interface.

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
dotnet tool install --global BusinessCentral.AL.Runner
```

That's it. On first build/run, the AL compiler (~57 MB from NuGet) and BC Service Tier DLLs (~11 MB via HTTP range requests) are downloaded automatically and cached. No manual setup, works on Windows, Linux, and macOS.

### Run

```bash
# Run test codeunits (test mode auto-detected when Subtype = Test is present)
al-runner ./src ./test

# Run with coverage report
al-runner --coverage ./src ./test

# Run a single .al file (OnRun mode)
al-runner samples/hello.al

# Load from .app packages with dependency resolution
al-runner --packages ./packages MyApp.app MyApp.Tests.app

# Debug: dump generated C# before and after rewriting
al-runner --dump-csharp samples/hello.al
al-runner --dump-rewritten samples/hello.al
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
al-runner (seconds) — catches pure-logic failures fast
  ↓ (only if al-runner passes)
Full BC pipeline (MsDyn365Bc.On.Linux, 45+ min) — full fidelity test execution
```

The full BC service tier pipeline:
- https://github.com/StefanMaron/MsDyn365Bc.On.Linux

## Samples

Each sample is a self-contained AL project with source, tests, and config:

| Sample | What it demonstrates |
|---|---|
| `samples/hello.al` | Minimal: table + codeunit + Message |
| `samples/calc.al` | Minimal: decimal arithmetic in OnRun |
| `samples/01-pure-function/` | Pure calculation logic, Assert.AreEqual |
| `samples/02-record-operations/` | Record CRUD, SETRANGE filtering, composite PKs |
| `samples/03-interface-injection/` | AL interface for dependency injection |
| `samples/04-asserterror/` | Error validation with asserterror + Assert.ExpectedError |
| `samples/05-known-limitation/` | Silent false positive from missing event subscriber |
| `samples/06-intentional-failure/` | Deliberately broken tests for error output demo |

## CI

Every push runs the test suite against a matrix of BC versions (26.0 through 27.5) to ensure compatibility. The publish workflow pushes to NuGet when all versions pass.

See the [Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml) for current results.

## Naming

Follows the `BusinessCentral.AL.*` convention:
- [BusinessCentral.AL.Mutations](https://github.com/StefanMaron/BusinessCentral.AL.Mutations) — mutation testing
- BusinessCentral.AL.Runner — this repo

## License

MIT
