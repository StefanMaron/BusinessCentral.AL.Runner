# BusinessCentral.AL.Runner

[![Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml/badge.svg)](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml)
[![NuGet](https://img.shields.io/nuget/v/MSDyn365BC.AL.Runner)](https://www.nuget.org/packages/MSDyn365BC.AL.Runner)

Run Business Central AL unit tests in **milliseconds** — no BC service tier, no Docker, no SQL Server, no license required.

## What It Is

AL Runner is a standalone test executor for Business Central codeunits. It transpiles AL source to C# using the BC compiler's public API, rewrites the generated C# to replace BC runtime types with in-memory mocks, compiles everything with Roslyn, and executes your test codeunits directly.

AL Runner is designed to run **before** the full BC service tier pipeline as a fast pre-check — not as a replacement. Running a full BC CI pipeline takes 45+ minutes; AL Runner makes the unit test portion take under a second.

```
Pull Request
  ↓
al-runner (seconds) — catches AL logic failures fast
  ↓ (only if al-runner passes)
Full BC pipeline (MsDyn365Bc.On.Linux, 45+ min) — full fidelity
```

## Quick Start

### Install

```bash
dotnet tool install --global MSDyn365BC.AL.Runner
```

On first run, the AL compiler (~57 MB from NuGet) and BC Service Tier DLLs (~11 MB via HTTP range requests) are downloaded automatically and cached. No manual setup required. Works on Windows, Linux, and macOS.

### Run

```bash
# Basic usage — run test codeunits in ./src and ./test
al-runner ./src ./test

# Load from .app packages
al-runner --packages ./packages MyApp.app MyApp.Tests.app

# Provide stub AL files for unsupported dependencies
al-runner --stubs ./stubs ./src ./test

# Run with coverage report (outputs cobertura.xml)
al-runner --coverage ./src ./test

# Machine-readable JSON output
al-runner --output-json ./src ./test

# Run a single test procedure by name
al-runner --run TestMyProcedure ./src ./test

# Generate stub AL files from .app symbol packages
al-runner --generate-stubs .alpackages ./stubs

# Print test-writing guide for AI agents
al-runner --guide
```

### Build from source

```bash
dotnet build AlRunner/
dotnet run --project AlRunner -- ./src ./test
```

## Pipeline Architecture

AL Runner has a 4-stage pipeline:

```
AL Source
  ↓  BC Compilation.Emit()        Transpile AL → C# using BC compiler's public API
  ↓  RoslynRewriter               Rewrite BC runtime types to in-memory mocks (AST-level)
  ↓  Roslyn in-memory compile     Compile against BC Service Tier DLLs (no disk writes)
  ↓  Executor                     Discover [NavTest] methods, run, report results
Results in milliseconds
```

**Stage 1 — AL Transpiler**: Uses `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` to convert each AL object (table, codeunit) into a C# class.

**Stage 2 — RoslynRewriter**: A `CSharpSyntaxRewriter` that replaces BC runtime types with mock implementations (`NavRecordHandle` → `MockRecordHandle`, etc.) and severs the BC runtime dependency.

**Stage 3 — RoslynCompiler**: Compiles the rewritten C# in-memory with Roslyn, referencing BC Service Tier DLLs auto-downloaded from the BC artifact CDN (~11 MB instead of the full 1.2 GB artifact).

**Stage 4 — Executor**: Discovers test codeunits, resets the in-memory table store between tests, runs each test via reflection, and reports pass/fail/error with optional coverage.

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | All tests passed |
| `1` | Test assertion failures (real bugs) or usage/argument error |
| `2` | Runner limitations only — use `--strict` to promote these to exit 1 |
| `3` | AL compilation error |

## What's Supported

AL Runner targets **broad AL language compatibility**. Core AL constructs (records, codeunits, interfaces, JSON, BLOB, TextBuilder, RecordRef/FieldRef, TestPage, report variables, HTTP mocks, and more) are supported. For the full coverage map, see [`docs/coverage.yaml`](docs/coverage.yaml).

**Built-in test toolkit codeunits** (auto-loaded, no stubs needed):

| Codeunit | ID | Purpose |
|---|---|---|
| Library Assert | 130 / 130002 | AreEqual, IsTrue, IsFalse, ExpectedError |
| Library - Variable Storage | 131004 | Enqueue/Dequeue for handler communication |
| Any | 130500 | Random test data (IntegerInRange, AlphanumericText, GuidValue) |
| Library - Random | 130440 | Pseudo-random numbers, dates, text (RandInt, RandDec, RandText) |
| Library - Test Initialize | 132250 | Integration events for test setup hooks |

For AI agents writing tests against the runner, use:

```bash
al-runner --guide
```

This prints a comprehensive test-writing reference including all supported/unsupported features and workarounds.

## Known Limitations

Some features require the BC service tier and cannot be emulated in a single .NET process. The key architectural limits are:

- **No transaction semantics** — `Commit()` and `Rollback()` are no-ops
- **No parallel sessions** — `StartSession` runs inline synchronously
- **Event subscribers with parameters** — subscribers receiving `var Rec` or `Sender` see null values
- **No UI rendering** — page layout, field visibility, and report datasets are not evaluated
- **HTTP I/O** — `HttpClient.Send()` and similar throw; inject via AL interface instead
- **XmlPort I/O** — `Import()`/`Export()` throw; XmlPort variables compile and surrounding logic runs

For the complete breakdown with workarounds, see [`docs/limitations.md`](docs/limitations.md).

If AL code fails to run and the reason is not in `docs/limitations.md`, that is a **runner gap** — please report it at https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues.

## Pipeline Outcomes

When al-runner executes a test, exactly one of three things happens:

- **PASS** — the codeunit's direct logic is correct
- **FAIL** — an assertion failed or the test threw an exception; this is a real failure
- **ERROR** — the codeunit depends on an unsupported feature; this is a configuration issue, not a test failure

**The guarantee:** if al-runner says FAIL, it is a real failure. Silent passes due to missing event subscriber side-effects are an accepted known limitation — always run the full pipeline after al-runner.

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

Implement the interface in your test codeunit with a stub that returns controlled values. Anything you can't inject cannot be unit-tested by this runner — and that is the right boundary.

## Test Suite

The `tests/` directory contains 81+ self-contained AL projects (`src/` + `test/`), each exercising a specific runner capability. Every push runs all of them against a [matrix of BC versions](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml).

## CI

The [Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml) runs on every push. The [Publish](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/publish.yml) workflow pushes to NuGet only when all versions pass (triggered by `git tag v*`).

## Naming

Follows the `BusinessCentral.AL.*` convention:
- [BusinessCentral.AL.Mutations](https://github.com/StefanMaron/BusinessCentral.AL.Mutations) — mutation testing
- BusinessCentral.AL.Runner — this repo

## License

MIT
