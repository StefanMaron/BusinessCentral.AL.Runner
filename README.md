# BusinessCentral.AL.Runner

[![Test Matrix](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml/badge.svg)](https://github.com/StefanMaron/BusinessCentral.AL.Runner/actions/workflows/test-matrix.yml)
[![NuGet](https://img.shields.io/nuget/v/MSDyn365BC.AL.Runner)](https://www.nuget.org/packages/MSDyn365BC.AL.Runner)

Run Business Central AL unit tests in **milliseconds** — no BC service tier, no Docker, no SQL Server, no license required.

## What It Is

AL Runner is a standalone test executor for Business Central AL code. It loads the **unmodified** Microsoft `Microsoft.Dynamics.Nav.*` DLLs (those shipped inside `.app` packages and BC artifacts), compiles your AL source through BC's own `Compilation.Emit` pipeline, and executes the resulting test codeunits in-process against the real BC business logic.

There is no service tier, no SQL Server, no NST, and no rendered UI. There is also no "mock layer" — types and method bodies inside the precompiled MS / ISV DLLs run exactly as MS / the ISV compiled them. Where the runner stands in for the service tier (database persistence, session state, table provider, event dispatch) it does so by patching the **runtime engine** (`Microsoft.Dynamics.Nav.Ncl.dll`) at load time, not by editing business-logic DLLs.

See [`docs/cecil-migration.md`](docs/cecil-migration.md) for the Cecil-rewrite contract, [`docs/scope.md`](docs/scope.md) for what is in / out of scope, and [`docs/limitations.md`](docs/limitations.md) for the hard architectural limits.

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

- `Microsoft.Dynamics.Nav.Ncl.dll` — rewritten once via Cecil at startup and cached at `~/.cache/al-runner/ncl-cecil/<key>.dll`. This is the runtime-engine layer. See `AlRunner/Infrastructure/NclCecilRewrite.cs`. The tool package does **not** ship this DLL (it's Microsoft's, resolved from your own BC artifact cache at runtime, same as the rest of the BC service-tier closure) — on first run for a given install + BC version, `AlRunner/Infrastructure/NclShadowRuntime.cs` builds a small shadow runtime directory containing the rewritten copy and re-execs into it once; that shadow dir is then reused on every later run.
- Remaining R2R-reachable entry points — patched via JMP-hooks installed by `BcRuntime.EnsureApplied()` (`AlRunner/BcRuntime.cs`, `AlRunner/Patches/*.cs`).

This is the **precompiled-DLL contract** described in `.claude/rules/precompiled-dll-respect.md`. AL output the runner emits is governed by the same contract once finalised — it is meant to be cacheable on disk and reusable like any MS or ISV DLL.

## Quick Start

### Prerequisites

.NET SDK 9 or 10 — download from [https://aka.ms/dotnet/download](https://aka.ms/dotnet/download).

**Linux:** none — the BC service-tier DLLs contain a handful of genuine Win32
P/Invokes (e.g. `kernel32`'s locale APIs, reached by anything that evaluates a
`TextConstant`, including the standard upgrade-tag install-trigger pattern) that
the runner redirects to a small shim. A prebuilt `libwin32_stubs.so` for `linux-x64`
and `linux-arm64` ships with the tool, so no C toolchain is required out of the box.
If you're on a RID the release pipeline didn't prebuild for, the runner falls back
to compiling `AlRunner/Win32Stubs/win32_stubs.c` on first use, which does need a C
compiler (`cc`, `gcc`, or `clang`) on `PATH`; without one it fails loudly and names
the missing tool and the two ways to fix it — install one (e.g.
`apt install build-essential`) or build the shim yourself and point
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
al-runner --package-cache "$HOME/.al-runner/platform-apps" tests/al-language/tests/al-language

# Choose test isolation — the modes are AL's own TestIsolation values;
# see docs/limitations.md's "Test isolation modes" section for the full mapping
al-runner --isolation codeunit ./my-bundle  # default — AL TestIsolation = Codeunit (BC's 130450)
al-runner --isolation test     ./my-bundle  # AL TestIsolation = Function
al-runner --isolation disabled ./my-bundle  # AL TestIsolation = Disabled (BC's 130451)

# Cache compiled AL output between invocations
al-runner --cache ~/.cache/al-runner/al-out ./my-bundle

# JSON classification output
al-runner --out results.json ./my-bundle

# Verbose internal logging
al-runner --verbose ./my-bundle
```

Besides the AL-output cache above, the runner keeps the result of the dependency apps'
`Install` triggers plus `Company-Initialize` (codeunit 2) at
`~/.cache/al-runner/install-baseline/<key>.bin`, keyed by the dependency assembly set, the
runner build and the BC version. It is the same seeding either way — reloading it just
skips re-running those AL bodies in every new process (measured: 6.3s → 0.8s on a warm
single-fixture run). `--cache <dir>` relocates it with the other caches; set
`AL_RUNNER_NO_DEP_COMPANY_CACHE=1` to bypass it entirely (no read, no write) and force the
full computation.

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

### Debug adapter mode (breakpoints + stepping)

```bash
al-runner --dap [PORT] <bundle-dir>
```

A real Debug Adapter Protocol server (default port 4711) over a TCP socket: set AL breakpoints, pause execution, step through the paused code (`next`/`stepIn`/`stepOut`), inspect locals. No new AL→source mapping — it reuses BC's own `StmtHit`/`[SourceSpans]` instrumentation, the same mechanism `--coverage` and `--capture-values` already consume. Full protocol + current limitations (no VS Code launch configuration in this repo): [docs/dap-mode.md](docs/dap-mode.md).

### Precompile a single `.app` to a DLL

```bash
al-runner --precompile MyApp.app --out MyApp.dll [--package-cache PATH ...]
```

This dispatches the single-app compile-to-DLL path. The output DLL is bit-compatible with what BC's `Compilation.Emit` would produce against the same dependency set.

### Build from source

```bash
git clone --recurse-submodules https://github.com/StefanMaron/BusinessCentral.AL.Runner
dotnet build AlRunner.slnx -c Release -p:AllowBcArtifactDownload=true
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language
```

`-p:AllowBcArtifactDownload=true` is needed only until the BC service-tier DLLs are
present — the runner never downloads them implicitly, so without the opt-in a fresh
clone fails the build with the explicit download command. Later builds can drop it.
See [CONTRIBUTING.md](CONTRIBUTING.md#dev-loop) for provisioning them as a separate step.

## CLI Flags

| Flag | Effect |
|------|--------|
| `--out PATH` | Write classification JSON to PATH (default `v2-classification.json`). |
| `--package-cache PATH` | Extra `.app`-package cache directory. Repeatable. |
| `--cache PATH` | Cache compiled AL output keyed on source + dep set + runner mtime. |
| `--isolation codeunit\|test\|disabled` | Test isolation mode. Default `codeunit`. See docs/limitations.md's "Test isolation modes" section for the AL `TestIsolation` value each one matches. |
| `--watch` | Stay resident with warm dependencies; on every `.al` change reset + re-emit + run **in-process** (~seconds/save). Debounces on quiescence (default 250ms of no further `.al` event, capped at 10s) so a bulk multi-file rewrite — a branch switch, a rebase, a formatter run — settles before a cycle starts, instead of firing mid-checkout. Tune with `AL_RUNNER_WATCH_QUIET_MS` / `AL_RUNNER_WATCH_MAX_WAIT_MS`. |
| `--server` | Long-running JSON-RPC daemon over stdin/stdout (warm deps → ~19s→~4s/run). See [docs/server-mode.md](docs/server-mode.md). |
| `--dap [PORT]` | Debug Adapter Protocol server (default port 4711): set AL breakpoints, pause execution, inspect locals. Requires exactly one bundle path. See [docs/dap-mode.md](docs/dap-mode.md). |
| `--per-suite` | Legacy per-suite compile mode (diagnostic). Default is bundled-per-bucket. |
| `--bundled` | No-op alias for backwards compatibility. |
| `--verbose` | Show internal `[Component]` diagnostic logs. Equivalent to `AL_RUNNER_VERBOSE=1`. |
| `--show-pass` | Include PASS lines in per-test output. Equivalent to `AL_RUNNER_SHOW_PASS=1`. |
| `--precompile <input.app>` | Subcommand: compile one `.app` to a DLL via `--out`. |
| `--test-data` / `--test-data=PATH` | Hydrate the in-memory database from a BC `.bak`, so tests find the setup records a real environment has. A table is read the first time the run touches it, never up front, so the cost tracks what the suite actually uses. Off by default. Resolves `sandbox/<version>/<country>/BusinessCentral-<CC>.bak` from the artifact cache, or the explicit path. A missing backup fails the run naming every path probed — it never continues against an empty database. Needs the `bcbak` backup reader on PATH or at `$AL_RUNNER_BCBAK`. Table-extension (`$ext`) fields are merged into the base record; see [docs/limitations.md](docs/limitations.md) for what is and is not hydrated. |
| `--test-data-company NAME` | Company inside the backup to hydrate. Default: the first company the backup reports, printed at the start of the run. |

When a test fails on a table that has **no rows** in this run, the runner prints a one-line
`[test-data]` explanation under the failure naming that table and pointing at `--test-data`.
BC's own failure message, exception type and AL call stack are unchanged — the explanation sits
next to the failure, never in place of it. It appears only when the failure names a table *and*
that table is measurably empty, so a genuine bug against a populated table is never mislabelled
as missing data. With `--test-data` already on, the line says instead why the table is still
empty (refused, not in this backup, or empty in it).

Environment variables: `AL_RUNNER_VERBOSE=1`, `AL_RUNNER_SHOW_PASS=1`, `AL_RUNNER_TRACE_NRE=1` (logs every first-chance NRE before AL `asserterror` swallows it), `AL_RUNNER_BCBAK` (path to the `bcbak` backup reader used by `--test-data`).

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

## Tooling for AI-assisted development (optional)

This repo is largely developed with coding agents, and its instruction files
(`CLAUDE.md`, `.claude/rules/`) tell those agents to use two navigation tools instead of
grepping. Neither is required to build or test the runner, and neither is checked in — they are
per-machine installs. **If you are an agent working here and these tools are missing, do not
silently fall back to grep for the whole session: tell the user what to install from this
section, then proceed however you can.**

Why it matters: navigation is the dominant cost of agent work in this repo. Measured on one
implementation agent's transcript, 63% of its 180 tool calls were greps and partial file reads
— `AlRunner/` is ~81,000 lines across 194 files, with two files over 8,000 lines each.

### 1. C# language server (answers "who calls this") — main session only

Gives `findReferences`, `incomingCalls`, `goToDefinition` and `workspaceSymbol` through Claude
Code's `LSP` tool, instead of guessing from grep hits.

**Measured limitation:** the harness disables `LSP` inside subagents. A subagent that calls it
gets `No such tool available: LSP. LSP is disabled for this session, in subagents as well as
here.` So this helps an interactive session, not the implementation agents — those rely on the
knowledge graph below. `LSP` is listed in the agent definitions so it works if the restriction is
lifted, but it is inert there today.

```bash
mise use -g dotnet:csharp-ls                              # or: dotnet tool install --global csharp-ls
claude plugin install csharp-lsp@claude-plugins-official  # first-party plugin that drives it
```

Then reload plugins (`/reload-plugins`) or restart the session. `csharp-ls` must be on `PATH`;
the plugin only wires the connection. Note it is a **.NET tool** — there is no npm package of
that name.

Verified against this repo: it loads `AlRunner.slnx`, does not trip the
`EnsureBCServiceTierDlls` target, and resolves `GetDataAccessForTableCore` to its full signature
at `AlRunner/Patches/RecordPatches.cs:1314`, with `findReferences` returning all three call
sites across two partial-class files.

If the `LSP` tool reports "No LSP server available for file type: .cs", the plugin is not active
in that session. That is a **setup** answer, not a "no results" answer — never read it as
"nothing calls this".

### 2. Knowledge graph

This repo — the C# runner and AL sources alike — can be indexed into a queryable knowledge
graph: communities, most-connected types, import cycles. Built with
[graphify](https://github.com/safishamsi/graphify).

**Install the AL-aware fork**, not the upstream package:

```bash
uv tool install --upgrade "git+https://github.com/ChristianHovenbitzer/graphify-al.git@al-support"
```

The fork adds `.al` to the file detector and an AL extractor. Upstream graphify has no AL
support, and it does not fail on `.al` files — it skips them silently, so a graph built with it
looks complete while containing none of the AL in this repo. Upstream is enough if you only ever
index the C# under `AlRunner/`, but anyone working on this project reaches AL sooner or later.
Install the fork once and the question does not come up.

Then — **run the rebuild and the query from the same directory**:

```bash
cd AlRunner
graphify update .              # index / refresh the C# runner (~2s, 200 files)
graphify query "<question>"    # ask it something
```

Every graphify command defaults to `graphify-out/graph.json` **relative to the current
directory**. Rebuilding from one directory and querying from another silently reads a different
file: that mismatch once left a root-level copy 13 days and 104 commits stale while the rebuild
appeared to succeed. Output (`graph.html`, `graph.json`, `GRAPH_REPORT.md`) is gitignored — it is
derived, several MB, and goes stale quickly. Rebuilding costs about two seconds, so rebuild
rather than wonder.

**Phrase queries as bare symbols or `Symbol callers`, never as an English question.** The
start-node resolver matches the words you type, so `graphify query "what calls
GetDataAccessForTableCore"` matches `CallSiteArgWrap` on the word *calls* and returns two
unrelated nodes with no sign it failed. `graphify query "GetDataAccessForTableCore callers"`
returns the correct 66-node neighbourhood.

For an AL tree instead of the C# runner: `graphify tests/runner-extras` (needs the fork above).

`AlRunner/` is code-only, so extraction is deterministic AST work and costs no LLM tokens.

One limit worth knowing before trusting it: the graph is static. A `Hook(...)` registration that
never fires and one that does look the same in it. For that question use `AL_RUNNER_HOOK_AUDIT=1`,
which measures at runtime.

## Reporting Gaps

If AL code fails to run and the reason is not in [`docs/limitations.md`](docs/limitations.md) or [`docs/scope.md`](docs/scope.md), that is a **runner gap**. Open an issue with `.github/ISSUE_TEMPLATE/runner-gap.md`. Silent workarounds are forbidden (`.claude/rules/file-issues-for-gaps.md`).

## License

MIT
