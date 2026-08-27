---
name: al-runner-architecture
description: Pipeline architecture, the precompiled-DLL contract, the Cecil + JmpHook patch layers, and the key-file map for AlRunner. Use when modifying AlRunner/ source (Program.cs, BcRuntime.cs, BcCompiler.cs, BcAssembler.cs, Patches/, Infrastructure/), debugging compilation/transpilation issues, deciding where to land a new runtime patch, or interpreting non-zero exit codes (1/2/3/4).
---

# AL Runner architecture

## Pipeline

```
AL source (.al files in a bundle dir rooted at app.json)
   |  Program.cs                       — parse CLI, locate bundle, resolve deps
   |  NclCecilRewrite (one-time)       — Cecil-rewrite Microsoft.Dynamics.Nav.Ncl.dll
   |                                      in-place on the bin path BEFORE CoreCLR's TPA
   |                                      probe loads it (cached at
   |                                      ~/.cache/al-runner/ncl-cecil/<key>.dll)
   |  DependencyLoader                 — load the bundle's declared deps as real MS / ISV DLLs
   |  BcRuntime.EnsureApplied()        — idempotent one-time runtime wiring: Win32 stubs,
   |                                      force-load the BC DLLs, register patch call sites
   |                                      (JmpHook layer is OFF by default — Cecil owns them)
   |  BcCompiler.Emit()                — drive BC's Compilation.Emit() to produce IL
   |  BcAssembler                      — Roslyn-compile the small C# polyfill bodies BC asks
   |                                      for (call-site arg-wraps, lambda thunks); IL is
   |                                      byte-equivalent to what BC's pipeline produces
Test assembly (in-memory, optionally cached at --cache <dir>/<key>.dll)
   |  TestExecutor                     — discover [NavTest] methods, run with chosen isolation
Results in milliseconds
```

There is **no type-renaming** layer. The `NavRecordHandle`, `NavSession`, `NavMethodScope` etc. the precompiled BaseApp / SystemApp DLLs reference are the same instances the AL tests touch. v1's `RoslynRewriter`, `MockX.cs` runtime, `AlRunner.Runtime` namespace, and `stubs/` AL stubs are all gone.

## The precompiled-DLL contract

> AL business-logic semantics (as the AL author wrote them) are the contract. Everything else — async wrappers, dispatcher infrastructure, framework plumbing, calling-convention machinery — is implementation detail the runner controls. If a test fails, the answer is **always** "fix runtime/framework code," never "patch the AL business logic."

Full text in `.claude/rules/precompiled-dll-respect.md`. The practical table:

| Layer | Examples | Modify? |
|---|---|---|
| Runtime engine / framework | `Microsoft.Dynamics.Nav.Ncl.dll`, `Microsoft.Dynamics.Nav.Types.dll` | Yes — Cecil rewrite, JmpHook, subclass, field-poke, EventPipe |
| Skeleton state | `NavSession`, `NavMethodScope`, threadlocals | Yes — populate any fields needed |
| AL business-logic DLLs | `*.SystemApplication.dll`, `*.BaseApplication.dll`, ISV `.app` content | **No** — bodies are sacred, signatures are sacred, type names are sacred |
| Our own AL output | DLLs emitted by `BcCompiler` | Modify only inside the compile pipeline (before finalisation). Once cached on disk it is precompiled like any MS DLL. |

When a method NREs on the skeleton runtime:
1. **Inside an AL business-logic DLL?** Stop. The fix is upstream — in the framework method it calls into, or in the skeleton state it reads.
2. **Inside the runtime engine?** Cecil-rewrite it. This is the only live mechanism — the JmpHook layer is off by default, so adding a `Hook(...)` call site with no matching `CecilOwned` entry ships a silent no-op.
3. **Inside our own AL output?** Patch the runtime engine instead; the same fix then helps integration tests against MS / ISV code.

## Loud-failures rule

When AL test code reaches a surface the runner cannot faithfully support, throw `AlRunner.Infrastructure.RunnerOutOfScopeException` with the BC API name and a reason from `docs/scope.md` (e.g. `email-smtp`, `http-egress`, `not-yet-implemented`). Never silently return a default — green tests then lie about what was actually executed. Full text in `.claude/rules/loud-failures.md`.

## Key files

| File | Role |
|---|---|
| `AlRunner/Program.cs` | CLI entry, bundle iteration, `--precompile` subcommand, Cecil-rewrite-on-startup wiring |
| `AlRunner/BcRuntime.cs` | `EnsureApplied()` — idempotent one-time runtime wiring + patch call-site registration |
| `AlRunner/BcCompiler.cs` | Drives `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` to compile AL bundles |
| `AlRunner/BcAssembler.cs` | Roslyn-compiles C# polyfill bodies BC's emit pipeline requests (arg-wraps, lambda thunks) |
| `AlRunner/AppLoader.cs` | Loads real MS / ISV `.app` DLLs in-process |
| `AlRunner/DependencyLoader.cs` | 3-tier dependency resolution (precompiled / loose / compiled-from-source) |
| `AlRunner/DependencyResolver.cs` | Resolves declared deps from `app.json` to on-disk `.app` paths |
| `AlRunner/TestExecutor.cs` | Discovers `[NavTest]` methods; runs with `codeunit` / `test` / `disabled` isolation |
| `AlRunner/Reporter.cs` | Writes the classification JSON (`--out`) |
| `AlRunner/Log.cs` | `[Component]` output filtering; respects `AL_RUNNER_VERBOSE`, `--verbose` |
| `AlRunner/Infrastructure/NclCecilRewrite.cs` | One-time Cecil rewrite of `Ncl.dll`; result cached at `~/.cache/al-runner/ncl-cecil/<key>.dll` |
| `AlRunner/Infrastructure/JmpHook.cs` | Legacy x86-64 precode JMP-hook mechanism. **Disabled by default** (`ComputeDisabled()` returns `true` unconditionally); `AL_RUNNER_ENABLE_JMPHOOK=1` is a net10-only diagnostic escape hatch that SEGFAULTs on net8. Also the orphaned-hook ledger read by `AL_RUNNER_HOOK_AUDIT=1`. |
| `AlRunner/Infrastructure/ExpectationManifest.cs` | Schema + loader for `tests/expectations/`. Wired into the run — `Program.cs` loads it (`ExpectationManifest.LoadFromDirectory`) and hands it to `TestExecutor` via `Expectations`; `./tests/expectations` is the default when it exists. See `docs/expectations.md`. |
| `AlRunner/Infrastructure/CountBaseline.cs` | Schema + loader for `--count-baseline` (per-suite exact test/app-group counts; a mismatch exits 4). Separate schema from the expectation manifest — lives under `tests/expectations/count-baseline/`. |
| `AlRunner/Infrastructure/AlCoverageTracker.cs`, `AlCoverageReport.cs`, `AlCoverageSourceMap.cs` | `--coverage` / `--coverage-out` — per-statement hit counts + Cobertura output, built on BC's own `StmtHit(N)` + `SourceSpans` line table |
| `AlRunner/Infrastructure/AlDapSession.cs`, `DapTransport.cs`, `DapBreakpointResolver.cs`, `AlDapStackWalker.cs` | `--dap` debug-adapter mode. See `docs/dap-mode.md`. |
| `AlRunner/ServerProtocol.cs`, `WatchSource.cs`, `WatchDashboard.cs` | `--server` (JSON-RPC daemon) and `--watch`. See `docs/server-mode.md`. |
| `AlRunner/Infrastructure/RunnerOutOfScopeException.cs` | Typed OOS exception (named API + reason) |
| `AlRunner/Patches/*.cs` | Per-API patch bodies (CodeunitPatches, RecordPatches, MetadataPatches, NavRecordIdPatches, …). A body only runs if `NclCecilRewrite` routes to it — a `Hook(...)` call site with no Cecil owner is a **silent no-op**. Triage with `AL_RUNNER_HOOK_AUDIT=1`. |

## Exit codes

Authoritative source: the `--strict` block in `PrintGuide()` (`AlRunner/Program.cs`).

| Code | Meaning |
|---|---|
| 0 | All tests passed |
| 1 | At least one test FAILED or ERRORED |
| 2 | A bundle could not execute (process-level error) — also a bad invocation: unknown flag, or a bundle path that does not exist |
| 3 | A bundle could not compile |
| 4 | `--count-baseline`: a suite's test or app-group count did not exactly match its declared baseline |

`--no-strict-exit` forces exit 0 regardless, so a caller can parse the JSON output without failing the step.

## CLI flags

**Do not take this list as authoritative — `AlRunner/Program.cs` is.** Run `al-runner --guide`
(the operating manual written for automated callers) or `--help`, or grep Program.cs for
`args[i] == "--`. The set as of 2026-08-27:

`--artifact-path`, `--auto-provision`, `--bc-version`, `--bundled` (no-op alias for the default
bundled mode), `--cache`, `--classify`, `--count-baseline`, `--coverage`, `--coverage-out`,
`--dap [PORT]`, `--define`, `--dump-csharp`, `--emit-app`, `--expectations`, `--failures-only`,
`--filter`, `--guide`, `--help`, `--isolation {codeunit|test|disabled}` (alias `--test-isolation`),
`--no-auto-provision`, `--no-cache`, `--no-strict-exit`, `--out`, `--output-json`,
`--output-junit`, `--package-cache` (repeatable), `--per-suite`, `--preprocessor-symbols`,
`--print-cache-key`, `--quiet`, `--server`, `--show-pass` (v1 back-compat; PASS lines are on by
default in v2), `--strict` (back-compat; the default since the v2 cut), `--tdd`, `--test`,
`--test-timeout`, `--verbose`, `--version`, `--watch`. Subcommands: `provision`,
`--precompile <input.app>`.

Auto-provisioning is **on by default** (#2024) — `--no-auto-provision` is the opt-out for
offline/air-gapped runs.

Environment: ~39 `AL_RUNNER_*` variables; the ones worth knowing are `AL_RUNNER_VERBOSE`,
`AL_RUNNER_TRACE_NRE`, `AL_RUNNER_HOOK_AUDIT` (live-vs-orphaned hook triage),
`AL_RUNNER_HOOK_TRACE`, `AL_RUNNER_PHASE_LOG`, `AL_RUNNER_PERF`, `AL_RUNNER_NCL_CACHE`,
`AL_RUNNER_TEST_TIMEOUT_SEC`. Full list: `grep -ohE 'AL_RUNNER_[A-Z0-9_]+' AlRunner -r --include=*.cs | sort -u`.

`--stubs` and `extract-deps` were v1 and are gone (`docs/archive/extract-deps.md`).
`--guide`, `--coverage` and `--dap` are **not** v1 leftovers — they are live v2 surfaces
(`docs/dap-mode.md`, `.github/workflows/coverage-demo.yml`).

## Cecil migration freeze

As of 2026-05-20, new runtime patches go through Cecil IL rewriting (`NclCecilRewrite`). Do not add new `JmpHook` patches — since the Cecil-only cutover a JmpHook call site does nothing at all. Existing JmpHook code migrates to Cecil opportunistically in hotspot order. See `docs/cecil-migration.md`.

**Measured twice, both negative: re-enabling orphaned JmpHooks is a net loss** (−7 Pageworks passes; −42 corpus passes on 2026-08-21). The remedy for an orphaned hook is to migrate it to Cecil or delete it — never `AL_RUNNER_ENABLE_JMPHOOK=1`. Roughly half the remainder are silent-fake stubs that `.claude/rules/loud-failures.md` forbids reviving at all.

## Sister docs

- `docs/subsystems.md` — BC subsystem boundary analysis
- `docs/scope.md` — per-API in/out-of-scope decisions
- `docs/limitations.md` — hard architectural limits
- `docs/expectations.md` — expectation-manifest schema
- `docs/cecil-migration.md` — Cecil-rewrite contract and roadmap
- `.claude/rules/precompiled-dll-respect.md` — the load-chain contract
- `.claude/rules/loud-failures.md` — runtime-side OOS-throw contract
