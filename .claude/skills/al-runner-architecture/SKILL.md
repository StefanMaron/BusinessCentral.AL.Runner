---
name: al-runner-architecture
description: Pipeline architecture, the precompiled-DLL contract, the Cecil + JmpHook patch layers, and the key-file map for AlRunner. Use when modifying AlRunner/ source (Program.cs, BcRuntime.cs, BcCompiler.cs, BcAssembler.cs, Patches/, Infrastructure/), debugging compilation/transpilation issues, deciding where to land a new runtime patch, or interpreting non-zero exit codes (1/2/3).
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
   |  BcRuntime.EnsureApplied()        — idempotently install all JMP-hook patches
   |  BcCompiler.CompileBundle()       — drive BC's Compilation.Emit() to produce IL
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
2. **Inside the runtime engine?** Cecil-rewrite it (preferred for new patches) or JMP-hook it (legacy).
3. **Inside our own AL output?** Patch the runtime engine instead; the same fix then helps integration tests against MS / ISV code.

## Loud-failures rule

When AL test code reaches a surface the runner cannot faithfully support, throw `AlRunner.Infrastructure.RunnerOutOfScopeException` with the BC API name and a reason from `docs/scope.md` (e.g. `email-smtp`, `http-egress`, `not-yet-implemented`). Never silently return a default — green tests then lie about what was actually executed. Full text in `.claude/rules/loud-failures.md`.

## Key files

| File | Role |
|---|---|
| `AlRunner/Program.cs` | CLI entry, bundle iteration, `--precompile` subcommand, Cecil-rewrite-on-startup wiring |
| `AlRunner/BcRuntime.cs` | `EnsureApplied()` — idempotent installer for every JMP-hook patch |
| `AlRunner/BcCompiler.cs` | Drives `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` to compile AL bundles |
| `AlRunner/BcAssembler.cs` | Roslyn-compiles C# polyfill bodies BC's emit pipeline requests (arg-wraps, lambda thunks) |
| `AlRunner/AppLoader.cs` | Loads real MS / ISV `.app` DLLs in-process |
| `AlRunner/DependencyLoader.cs` | 3-tier dependency resolution (precompiled / loose / compiled-from-source) |
| `AlRunner/DependencyResolver.cs` | Resolves declared deps from `app.json` to on-disk `.app` paths |
| `AlRunner/TestExecutor.cs` | Discovers `[NavTest]` methods; runs with `codeunit` / `test` / `disabled` isolation |
| `AlRunner/Reporter.cs` | Writes the classification JSON (`--out`) |
| `AlRunner/Log.cs` | `[Component]` output filtering; respects `AL_RUNNER_VERBOSE`, `--verbose` |
| `AlRunner/Infrastructure/NclCecilRewrite.cs` | One-time Cecil rewrite of `Ncl.dll`; result cached at `~/.cache/al-runner/ncl-cecil/<key>.dll` |
| `AlRunner/Infrastructure/JmpHook.cs` | Legacy JMP-hook mechanism; Cecil-freeze rule applies to new patches |
| `AlRunner/Infrastructure/ExpectationManifest.cs` | Schema + loader for `tests/expectations/`. Library is loaded but **not yet wired into `Reporter`** — wiring is a separate PR. See `docs/expectations.md`. |
| `AlRunner/Infrastructure/RunnerOutOfScopeException.cs` | Typed OOS exception (named API + reason) |
| `AlRunner/Patches/*.cs` | Per-API JMP-hook patches (CodeunitPatches, RecordPatches, MetadataPatches, NavRecordIdPatches, etc.) |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | All tests pass |
| 1 | Test assertion failures, runner errors, or argument error |
| 2 | Runner limitations only |
| 3 | AL compilation error |

## CLI flags

Defined in `AlRunner/Program.cs`. Current set: `--out`, `--package-cache` (repeatable), `--cache`, `--isolation {codeunit|test|disabled}`, `--per-suite`, `--bundled` (no-op alias), `--verbose`, `--show-pass`, `--precompile <input.app>` (subcommand). Environment: `AL_RUNNER_VERBOSE`, `AL_RUNNER_SHOW_PASS`, `AL_RUNNER_TRACE_NRE`.

No `--guide`, `--stubs`, `--coverage`, `--dap`, `extract-deps` — those were v1.

## Cecil migration freeze

As of 2026-05-20, new runtime patches go through Cecil IL rewriting (`NclCecilRewrite`). Do not add new `JmpHook` patches. Existing JmpHook code migrates to Cecil opportunistically in hotspot order. See `docs/cecil-migration.md`.

## Sister docs

- `docs/subsystems.md` — BC subsystem boundary analysis
- `docs/scope.md` — per-API in/out-of-scope decisions
- `docs/limitations.md` — hard architectural limits
- `docs/expectations.md` — expectation-manifest schema
- `docs/cecil-migration.md` — Cecil-rewrite contract and roadmap
- `.claude/rules/precompiled-dll-respect.md` — the load-chain contract
- `.claude/rules/loud-failures.md` — runtime-side OOS-throw contract
