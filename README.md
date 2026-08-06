# BusinessCentral.AL.Runner

[![Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml/badge.svg)](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml)
[![NuGet](https://img.shields.io/nuget/v/MSDyn365BC.AL.Runner)](https://www.nuget.org/packages/MSDyn365BC.AL.Runner)

Run Business Central AL unit tests in **milliseconds** — no BC service tier, no Docker, no SQL Server, no license required.

## What It Is

AL Runner is a standalone test executor for Business Central AL code. It loads the **unmodified** Microsoft `Microsoft.Dynamics.Nav.*` DLLs (those shipped inside `.app` packages and BC artifacts), compiles your AL source through BC's own `Compilation.Emit` pipeline, and executes the resulting test codeunits in-process against the real BC business logic.

There is no service tier, no SQL Server, no NST, and no rendered UI. There is also no "mock layer" — types and method bodies inside the precompiled MS / ISV DLLs run exactly as MS / the ISV compiled them. Where the runner stands in for the service tier (database persistence, session state, table provider, event dispatch) it does so by patching the **runtime engine** (`Microsoft.Dynamics.Nav.Ncl.dll`) at load time, not by editing business-logic DLLs.

See [`docs/subsystems.md`](docs/subsystems.md) for the subsystem map, [`docs/cecil-migration.md`](docs/cecil-migration.md) for the Cecil-rewrite contract, [`docs/scope.md`](docs/scope.md) for what is in / out of scope, and [`docs/limitations.md`](docs/limitations.md) for the hard architectural limits.

## Architecture

```
AL source (.al files)
   |  BcCompiler           — BC's own Compilation.Emit() drives AL → IL
   |  BcAssembler          — Roslyn compiles the small C# polyfill bodies BC asks for
   |                         (call-site arg-wraps, lambda thunks); IL is byte-equivalent
   |                         to what BC's pipeline would produce
Test assembly (in-memory, cacheable)
   |  TestExecutor         — discovers [NavTest] methods, runs them with the chosen
   |                         isolation mode against real BC dispatch
Results in milliseconds
```

What the runner does **not** do:

- It does not rename BC types. No `NavRecordHandle → MockRecordHandle` substitution. The same `NavRecordHandle` that the precompiled BaseApp / SystemApp DLLs reference is the one your tests execute against.
- It does not rewrite method bodies in `*.SystemApplication.dll`, `*.BaseApplication.dll`, or any ISV business-logic DLL. Those bodies are the contract the runner exists to validate.
- It does not stub dependencies. MS apps (System App, Base App, test toolkit) load as their real DLLs; ISV dependencies load the same way.

What the runner **does** modify:

- `Microsoft.Dynamics.Nav.Ncl.dll` — rewritten once via Cecil at startup and cached at `~/.cache/al-runner/ncl-cecil/<key>.dll`. This is the runtime-engine layer. See `AlRunner/Infrastructure/NclCecilRewrite.cs`.
- Remaining R2R-reachable entry points — patched via JMP-hooks installed by `BcRuntime.EnsureApplied()` (`AlRunner/BcRuntime.cs`, `AlRunner/Patches/*.cs`).

This is the **precompiled-DLL contract** described in `.claude/rules/precompiled-dll-respect.md`. AL output the runner emits is governed by the same contract once finalised — it is meant to be cacheable on disk and reusable like any MS or ISV DLL.

## Quick Start

### Prerequisites

.NET SDK 9 or 10 — download from [https://aka.ms/dotnet/download](https://aka.ms/dotnet/download).

**Linux only:** a C compiler (`cc`, `gcc`, or `clang`) on `PATH`. The BC service-tier
DLLs contain a handful of genuine Win32 P/Invokes (e.g. `kernel32`'s locale APIs,
reached by anything that evaluates a `TextConstant`, including the standard
upgrade-tag install-trigger pattern) that the runner redirects to a small shim
compiled on first use from `AlRunner/Win32Stubs/win32_stubs.c`. Without a compiler
this fails loudly and names the missing tool and the two ways to fix it — install one
(e.g. `apt install build-essential`) or build the shim yourself and point
`AL_RUNNER_WIN32_STUBS_SO` at the resulting `.so`. Not needed on Windows or macOS.

### Install

```bash
dotnet tool install --global MSDyn365BC.AL.Runner
```

On first run, the AL compiler and the BC service-tier DLLs (around 11 MB via HTTP range requests) are downloaded and cached. Works on Windows, Linux, and macOS.

On Windows, exclude the runner's cache/output directories from real-time antivirus scanning if you hit slow cold-start times — Windows Defender locking a just-written DLL for scanning is a known source of first-run delay (worked around automatically with a bounded retry, but excluding the folder avoids the wait entirely).

### Run

The runner takes one or more **bundle directories**. A bundle is an `app.json`-rooted AL project (the same shape every BC extension has). The `tests/al-language/` submodule is a canonical example.

```bash
# Run a single bundle
al-runner tests/al-language/tests/al-language

# Multiple bundles
al-runner ./app1 ./app2

# Specify package caches for dependency resolution (repeatable)
al-runner --package-cache ~/.local/share/al-runner/packages tests/al-language/tests/al-language

# Choose test isolation (matches BC's "Test Runner - Isol. Codeunit" by default)
al-runner --isolation codeunit ./my-bundle
al-runner --isolation test     ./my-bundle
al-runner --isolation disabled ./my-bundle

# Cache compiled AL output between invocations
al-runner --cache ~/.cache/al-runner/al-out ./my-bundle

# JSON classification output
al-runner --out results.json ./my-bundle

# Verbose internal logging
al-runner --verbose ./my-bundle
```

### Watch mode (live dashboard)

```bash
al-runner <bundle-dir> --watch [--package-cache PATH ...] [--cache DIR]
```

Stays resident with dependencies + BC patches loaded once, and re-runs the bundle **in-process** on every `.al` save (~seconds/save after a one-time cold first cycle).

On an interactive terminal `--watch` renders a **live, non-scrolling dashboard** that repaints in place on each cycle (like vitest / cargo-watch):

```text
╭──────────────────────────────────────────────────────────────────────────────╮
│ al-runner my-app  ·  ● watching  ·  last run 11.45.05 · 0,9s                  │
╰──────────────────────────────────────────────────────────────────────────────╯

╭────────────────────────────────┬────────┬────┬───────────────────────────────╮
│ Test                           │ Status │ ms │ Message                       │
├────────────────────────────────┼────────┼────┼───────────────────────────────┤
│ Codeunit60110.Insert_OnInsert… │ FAIL   │ 38 │ Assert.AreEqual failed.       │
│                                │        │    │ Expected:<1>. Actual:<9>.     │
╰────────────────────────────────┴────────┴────┴───────────────────────────────╯

0P / 1F / 0E  ·  1 total    Ctrl+C to quit
```

The header status flips to `⟳ running…` while a cycle compiles+runs (so the cold first run never looks frozen) and back to `● watching` when idle. Rendered cross-platform (Windows/macOS/Linux) via [Spectre.Console](https://spectreconsole.net/).

When stdout is **not** an interactive terminal (CI, a pipe, VS Code, a test harness), `--watch` automatically falls back to plain line output (`PASS`/`FAIL` per test + a `[watch] waiting for AL source changes…` marker) and emits no ANSI/cursor control. There is no separate UI flag — `--watch` itself is the dashboard.

### Server mode (warm daemon for editor integrations)

```bash
al-runner --server [--package-cache PATH ...] [--cache DIR]
```

A long-running JSON-RPC daemon over stdin/stdout. Dependencies and BC patches load once; each `runTests` request re-emits the bundle warm and runs it in-process (~19s→~4s). stdout carries only the newline-delimited JSON protocol; logs go to stderr. The VS Code extension uses this. Full protocol + the same-bundle reload contract: [docs/server-mode.md](docs/server-mode.md).

### Precompile a single `.app` to a DLL

```bash
al-runner --precompile MyApp.app --out MyApp.dll [--package-cache PATH ...]
```

This dispatches the single-app compile-to-DLL path. The output DLL is bit-compatible with what BC's `Compilation.Emit` would produce against the same dependency set.

### Build from source

```bash
git clone --recurse-submodules https://github.com/StefanMaron/BusinessCentral.AL.Runner
dotnet build AlRunner.slnx -c Release
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language
```

## CLI Flags

| Flag | Effect |
|------|--------|
| `--out PATH` | Write classification JSON to PATH (default `v2-classification.json`). |
| `--package-cache PATH` | Extra `.app`-package cache directory. Repeatable. |
| `--cache PATH` | Cache compiled AL output keyed on source + dep set + runner mtime. |
| `--isolation codeunit\|test\|disabled` | Test isolation mode. Default `codeunit`. |
| `--watch` | Stay resident with warm dependencies; on every `.al` change reset + re-emit + run **in-process** (~seconds/save). |
| `--server` | Long-running JSON-RPC daemon over stdin/stdout (warm deps → ~19s→~4s/run). See [docs/server-mode.md](docs/server-mode.md). |
| `--per-suite` | Legacy per-suite compile mode (diagnostic). Default is bundled-per-bucket. |
| `--bundled` | No-op alias for backwards compatibility. |
| `--verbose` | Show internal `[Component]` diagnostic logs. Equivalent to `AL_RUNNER_VERBOSE=1`. |
| `--show-pass` | Include PASS lines in per-test output. Equivalent to `AL_RUNNER_SHOW_PASS=1`. |
| `--precompile <input.app>` | Subcommand: compile one `.app` to a DLL via `--out`. |

Environment variables: `AL_RUNNER_VERBOSE=1`, `AL_RUNNER_SHOW_PASS=1`, `AL_RUNNER_TRACE_NRE=1` (logs every first-chance NRE before AL `asserterror` swallows it).

## Test Corpus

The canonical AL test corpus lives in [`tests/al-language/`](tests/al-language/) — a read-only git submodule pinned at [`StefanMaron/BusinessCentral.AL.Language.Tests`](https://github.com/StefanMaron/BusinessCentral.AL.Language.Tests). Each test is a behavioural contract validated against a real BC service tier. The runner runs that corpus unmodified; tests it cannot execute by design (SMTP, real HTTP, report rendering, etc.) are declared in [`tests/expectations/`](tests/expectations/) using the schema in [`docs/expectations.md`](docs/expectations.md).

Runner-specific positive tests (e.g. "this surface must throw `RunnerOutOfScopeException` with reason X") live in [`tests/runner-extras/`](tests/runner-extras/).

See `.claude/rules/al-language-submodule.md` for the read-only contract.

## What's Supported

The goal is broad AL-language compatibility: any AL code that can run without the BC service tier should compile and execute here. The runner targets the whole AL surface — records (CRUD, filters, keys, CalcFields, CalcSums, triggers), codeunits (interface dispatch, event subscribers, BC lifecycle events), test toolkit codeunits (`LibraryAssert` 130, `Any` 130500, etc.), test handlers (Confirm, Message, ModalPage, Request, Report, Notification), TestPage, RecordRef / FieldRef, BLOB / streams, JSON / XML, regex, in-process crypto, IsolatedStorage, TaskScheduler (synchronous dispatch).

Out of scope by design: SMTP, HTTP egress to external services, file I/O against external filesystems, OData / SOAP publishing endpoints, physical printers, background-job scheduling against a real scheduler, page / report **rendering** (handler callbacks fire; layout is not evaluated). These surfaces throw `RunnerOutOfScopeException` with a named API and reason — they never silently return defaults. See `.claude/rules/loud-failures.md` and [`docs/scope.md`](docs/scope.md).

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | All tests passed |
| `1` | Test assertion failures, runner errors, or argument error |
| `2` | Runner limitations only |
| `3` | AL compilation error |

## Reporting Gaps

If AL code fails to run and the reason is not in [`docs/limitations.md`](docs/limitations.md) or [`docs/scope.md`](docs/scope.md), that is a **runner gap**. Open an issue with `.github/ISSUE_TEMPLATE/runner-gap.md`. Silent workarounds are forbidden (`.claude/rules/file-issues-for-gaps.md`).

## License

MIT
