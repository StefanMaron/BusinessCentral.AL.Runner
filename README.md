# BusinessCentral.AL.Runner

[![Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml/badge.svg)](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml)
[![NuGet](https://img.shields.io/nuget/v/MSDyn365BC.AL.Runner)](https://www.nuget.org/packages/MSDyn365BC.AL.Runner)

Run Business Central AL unit tests in **milliseconds** -- no BC service tier, no Docker, no SQL Server required.

## What It Is

AL Runner is a standalone test executor for Business Central AL code. It transpiles your AL source to C# using the BC compiler's public API, rewrites the generated C# to replace BC runtime types with in-memory mocks, compiles everything with Roslyn, and executes your test codeunits directly.

**What it runs:** Your AL source code from disk -- the `.al` files you write. Tables, codeunits, enums, interfaces, queries, reports (triggers only), page extensions (business logic only).

**What it does NOT run:** Code inside `.app` packages. If you reference a dependency `.app`, AL Runner uses it only for **symbol resolution** (so your code compiles). It does not execute the code inside that `.app`. This means auto-stubbed dependency codeunits return default values. If your tests depend on real behavior from a dependency, you need to provide stubs or use `--compile-dep` (see [Working with Dependencies](#working-with-dependencies)).

AL Runner is designed to run **before** the full BC service tier pipeline as a fast pre-check -- not as a replacement. Running a full BC CI pipeline takes 45+ minutes; AL Runner makes the unit test portion take under a second.

```
Pull Request
  |
al-runner (seconds) -- catches AL logic failures fast
  | (only if al-runner passes)
Full BC pipeline (BcContainerHelper / MsDyn365Bc.On.Linux, 45+ min) -- full fidelity
```

## Quick Start

### Prerequisites

.NET SDK 8, 9, or 10 — download from [https://aka.ms/dotnet/download](https://aka.ms/dotnet/download).

### Install

```bash
dotnet tool install --global MSDyn365BC.AL.Runner
```

On first run, the AL compiler (~57 MB from NuGet) and BC Service Tier DLLs (~11 MB via HTTP range requests) are downloaded automatically and cached. No manual setup required. Works on Windows, Linux, and macOS.

> **Windows / corporate environments:** If the install fails with "package not found in NuGet feeds", your machine may be missing the public NuGet.org feed. Add it once:
> ```powershell
> dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
> ```
> Then re-run the install command.

### Run

```bash
# Basic usage -- run test codeunits in ./src and ./test
al-runner ./src ./test

# With dependency packages for symbol resolution
al-runner --packages .alpackages ./src ./test

# Run a single test procedure by name
al-runner --run TestMyProcedure ./src ./test

# Machine-readable output for CI
al-runner --output-json ./src ./test
al-runner --output-junit results.xml ./src ./test
```

### Multi-root workspaces

Pass all source and test directories as separate arguments — AL Runner compiles them together as one project:

```bash
al-runner ./app1/src ./app1.test/src ./app2/src ./app2.test/src
```

AL object IDs must be unique across all directories passed in a single invocation.

### Build from source

```bash
dotnet build AlRunner/
dotnet run --project AlRunner -- ./src ./test
```

## Working with Dependencies

Most AL projects reference the Base Application, System Application, or other extensions via `.app` packages. AL Runner uses these packages for **symbol resolution only** -- it does not execute code from inside `.app` files.

When your code calls a codeunit from a dependency, AL Runner **auto-stubs** it: the method exists (so your code compiles) but returns default values (`0`, `''`, `false`). This is usually fine for unit tests that mock their dependencies, but breaks tests that rely on real library behavior.

You have three options, from simplest to most powerful:

### Option 1: Auto-stubs (default, zero config)

AL Runner automatically generates empty stubs for any dependency codeunit your code references. Methods exist but return defaults.

```bash
al-runner --packages .alpackages ./src ./test
```

### Option 2: Hand-written AL stubs (`--stubs`)

Write `.al` stub files that provide controlled return values for specific dependency methods:

```bash
# Generate stub scaffolds from your .app packages
al-runner --generate-stubs .alpackages ./stubs ./src ./test

# Edit the generated stubs, then run with --stubs
al-runner --stubs ./stubs --packages .alpackages ./src ./test
```

The `--generate-stubs` command reads method signatures from .app packages and the BC symbol table, then writes empty AL codeunit files. You fill in the method bodies with test-appropriate return values.

### Option 3: Compiled dependency DLLs (`--compile-dep`)

For dependencies where you have the AL source and want real behavior (not stubs), compile them to a rewritten DLL:

```bash
# Compile a dependency .app to a reusable DLL
al-runner --compile-dep MyDependency.app ./deps --packages .alpackages

# Run tests with the compiled dependency
al-runner --dep-dlls ./deps --packages .alpackages ./src ./test
```

This gives you real method implementations from the dependency, executing in-memory just like your own code.

## Pipeline Architecture

AL Runner has a 4-stage pipeline:

```
AL Source (.al files on disk)
  |  BC Compilation.Emit()        Transpile AL -> C# using BC compiler's public API
  |  RoslynRewriter               Rewrite BC runtime types to in-memory mocks (AST-level)
  |  Roslyn in-memory compile     Compile against BC Service Tier DLLs (no disk writes)
  |  Executor                     Discover [NavTest] methods, run, report results
Results in milliseconds
```

**Stage 1 -- AL Transpiler**: Uses `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` to convert each AL object (table, codeunit, query, report, enum) into a C# class.

**Stage 2 -- RoslynRewriter**: A `CSharpSyntaxRewriter` that replaces BC runtime types with mock implementations (`NavRecordHandle` -> `MockRecordHandle`, etc.) and severs the BC runtime dependency.

**Stage 3 -- RoslynCompiler**: Compiles the rewritten C# in-memory with Roslyn, referencing BC Service Tier DLLs auto-downloaded from the BC artifact CDN (~11 MB instead of the full 1.2 GB artifact).

**Stage 4 -- Executor**: Discovers test codeunits (Subtype = Test), resets the in-memory table store between tests, runs each `[Test]` procedure via reflection, and reports pass/fail/error with optional coverage.

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | All tests passed |
| `1` | Test assertion failures (real bugs) or usage/argument error |
| `2` | Runner limitations only -- use `--strict` to promote to exit 1 |
| `3` | AL compilation error |

## What's Supported

AL Runner targets **broad AL language compatibility**. The goal is that any AL codeunit should compile and execute without modification -- the only restructuring needed is dependency injection for features that are architecturally out of scope (real HTTP calls, page rendering, SQL queries with JOINs).

**Core language**: Variables, procedures, parameters (by-value and by-ref), return values, expressions, control flow, asserterror, error handling, enums, interfaces, arrays, collections (List, Dictionary), TextBuilder, temporary tables.

**Record operations**: Insert, Modify, Delete, Get, Find, FindSet, FindFirst, FindLast, Next, SetRange, SetFilter, SetCurrentKey, CalcFields, CalcSums, Init, Validate, Copy, TransferFields, field triggers, composite primary keys, secondary keys with sorting.

**Cross-codeunit dispatch**: Method calls between codeunits, interface dispatch, event subscribers (both manual and integration events with `--init-events`). With `--init-events`, BC lifecycle events (OnCompanyInitialize, OnInstallAppPerCompany) fire **once** at startup and the resulting DB state is snapshotted as the baseline for every test.

**Mock subsystems**: RecordRef/FieldRef, TestPage, Notification, BigText, JSON types, HttpClient (mock responses), BLOB/InStream/OutStream, Media (ImportStream/ExportStream), File, IsolatedStorage, TaskScheduler, DataTransfer.

**Query objects**: Single-dataitem queries work in-memory (Open/Read/Close, SetFilter, SetRange, TopNumberOfRows).

**Report triggers**: OnPreReport, OnPreDataItem, OnAfterGetRecord, OnPostDataItem, OnPostReport.

For the full coverage map, see [`docs/coverage.yaml`](docs/coverage.yaml).

**Built-in test toolkit codeunits** (auto-loaded, no stubs needed):

| Codeunit | ID | Purpose |
|---|---|---|
| Library Assert | 130 / 130002 | AreEqual, IsTrue, IsFalse, ExpectedError |
| Library - Variable Storage | 131004 | Enqueue/Dequeue for handler communication |
| Any | 130500 | Random test data (IntegerInRange, AlphanumericText, GuidValue) |
| Library - Random | 130440 | Pseudo-random numbers, dates, text (RandInt, RandDec, RandText) |
| Library - Utility | 131003 | GenerateGUID, GenerateRandomCode, GenerateRandomText |
| Library - Test Initialize | 132250 | Integration events for test setup hooks |

For AI agents writing tests: `al-runner --guide` prints a comprehensive test-writing reference.

## Known Limitations

Some features require the BC service tier and cannot be emulated in standalone mode:

- **Code inside .app packages is not executed** -- only your `.al` source files run; dependencies are auto-stubbed or must be provided via `--stubs` / `--compile-dep`
- **No transaction semantics** -- `Commit()` and `Rollback()` are no-ops
- **No parallel sessions** -- `StartSession` runs inline synchronously
- **No UI rendering** -- page layout, field visibility, and report rendering are not evaluated
- **No multi-dataitem queries** -- single-dataitem queries work; JOINs and aggregation do not
- **HTTP I/O** -- `HttpClient.Send()` throws; inject via AL interface instead
- **XmlPort I/O** -- `Import()`/`Export()` throw; XmlPort variables compile and surrounding logic runs

For the complete breakdown with workarounds, see [`docs/limitations.md`](docs/limitations.md).

If AL code fails to run and the reason is not in `docs/limitations.md`, that is a **runner gap** -- please report it at https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

## Pipeline Outcomes

When al-runner executes a test, exactly one of three things happens:

- **PASS** -- the codeunit's direct logic is correct
- **FAIL** -- an assertion failed or the test threw an exception; this is a real failure
- **ERROR** -- the codeunit depends on an unsupported feature; this is a configuration issue, not a test failure

**The guarantee:** if al-runner says FAIL, it is a real failure. Silent passes due to missing event subscriber side-effects are an accepted known limitation -- always run the full pipeline after al-runner.

## Testability Pattern

Design codeunits for testability by injecting dependencies via AL interfaces:

```al
interface IInventoryCheck
    procedure HasStock(ItemNo: Code[20]): Boolean;
end

codeunit 50100 OrderProcessor
    procedure Process(ItemNo: Code[20]; Checker: Interface IInventoryCheck)
    begin
        if not Checker.HasStock(ItemNo) then
            Error('Item %1 is out of stock', ItemNo);
    end;
end
```

Implement the interface in your test codeunit with a stub that returns controlled values. Anything you can't inject cannot be unit-tested by this runner -- and that is the right boundary.

## DAP Debugger (experimental)

AL Runner includes a DAP (Debug Adapter Protocol) server that lets you set breakpoints in AL source files and inspect variable values during test execution -- no BC service tier needed.

### How it works

The BC compiler emits a `StmtHit(N)` call at every AL statement. AL Runner's `BreakpointManager` intercepts these calls and pauses execution when a breakpoint is registered at that statement. Variable values are captured after each assignment via `ValueCapture`.

### Usage

Start al-runner in DAP mode -- it listens on a TCP port and waits for a debugger client to connect before running tests:

```bash
# Start DAP server on port 4711
al-runner --dap 4711 ./src ./test

# With packages and a specific test
al-runner --dap 4711 --run TestMyProcedure --packages .alpackages ./src ./test
```

The server prints `al-runner DAP server listening on 127.0.0.1:4711` and blocks until a client connects and sends `configurationDone`.

### Connecting from VS Code

Add to your `launch.json`:

```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "type": "al",
            "request": "attach",
            "name": "Attach to al-runner",
            "port": 4711,
            "hostname": "127.0.0.1"
        }
    ]
}
```

> **Note:** The standard Microsoft AL Language extension's debugger expects a BC server, not a custom DAP server. A dedicated `vscode-al-runner` extension is planned but not yet available. In the meantime, a generic DAP client extension may work for basic breakpoint/continue/inspect workflows.

### Current limitations

- **Step-over/step-into** -- not yet implemented; behave as `continue`
- **No conditional breakpoints** -- all breakpoints are unconditional
- **No expression evaluation** -- the `evaluate` command is not supported
- **Variable values are post-assignment** -- you see the most recent assigned value

For the full architecture and protocol details, see [`docs/dap.md`](docs/dap.md).

## Test Suite

The `tests/` directory contains 100+ self-contained AL projects (`src/` + `test/`), each exercising a specific runner capability. Every push runs all of them against a [matrix of BC versions](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml) (BC 26.0 through 28.0).

## CI

The [Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml) runs on every push. The [Publish](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/publish.yml) workflow pushes to NuGet only when all versions pass (triggered by `git tag v*`).

## Naming

Follows the `BusinessCentral.AL.*` convention:
- [BusinessCentral.AL.Mutations](https://github.com/StefanMaron/BusinessCentral.AL.Mutations) -- mutation testing
- BusinessCentral.AL.Runner -- this repo

## License

MIT
