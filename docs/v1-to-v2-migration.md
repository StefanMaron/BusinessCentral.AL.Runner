# AL Runner v1 → v2 migration

This page documents what changed between AL Runner v1 and v2, flag by flag, so
users upgrading the `MSDyn365BC.AL.Runner` global tool know what works, what
moved, and what is gone.

The v2 line starts at version **2.0.0-preview.1**. v1 releases were 0.x.

## Why a major version bump

v1 produced a managed test DLL by **renaming BC's types** in its Roslyn
rewriter (`NavCodeunitHandle` → `MockCodeunitHandle`, `NavRecord` →
`MockRecord`, …) and emitting stub `.al` files for missing BC objects. That
meant every BC dependency had to be renameable end-to-end, every test had to
live in a stubbed world, and integration tests against the real System / Base
Application R2R DLLs could not link.

v2 inverts the contract: **MS- and ISV-compiled DLLs link in unchanged**. AL
sources are emitted through BC's own `Compilation.Emit` pipeline so the
resulting IL is byte-equivalent to BC's own service-tier output. Patches at
runtime target the runtime engine (`Microsoft.Dynamics.Nav.Ncl.dll`) via
Cecil rewrites and JMP hooks; the precompiled business-logic DLLs are never
touched.

See `docs/cecil-migration.md` for the patch contract and
`.claude/rules/precompiled-dll-respect.md` for the full table of what's
allowed and forbidden.

## CLI flag matrix

| Flag | v1 | v2 | Notes |
|---|---|---|---|
| `<bundle-dir>` (positional) | ✓ | ✓ | Same shape. Multiple bundles aggregate. |
| `--out PATH` | ✓ | ✓ | Failure-classification JSON. |
| `--package-cache PATH` (repeatable) | ✓ | ✓ | Same scan order. |
| `--cache DIR` | ✓ | ✓ | AL-output cache. |
| `--isolation MODE` / `--test-isolation MODE` | `--test-isolation` only | both work | v2 accepts the v1 name. `method` accepted as alias for `codeunit`. |
| `--verbose` | ✓ | ✓ | Same. |
| `--show-pass` | ✓ | accepted (no-op) | v2 prints PASS lines by default. |
| `--failures-only` / `--quiet` | (no flag — was default) | ✓ | New v2 opt-out to suppress PASS lines. |
| `--strict` | ✓ | accepted (no-op) | Same exit-code convention (0 / 1 / 2 / 3), but strict exit is now v2's default — matching v1. `--no-strict-exit` opts back into always-exit-0 for tooling that only wants to parse the JSON output. |
| `--test PATTERN` / `--filter PATTERN` | ✓ | ✓ | Substring match on `Codeunit.Method`. |
| `--output-json` | ✓ | ✓ | v1-shaped per-test `status: pass/fail/error` JSON to stdout, distinct from `--out`'s failure-classification JSON. `capturedValues`/`iterations` fields omitted — those need a shared Cecil-instrumentation prerequisite, tracked separately. |
| `--output-junit PATH` | ✓ | ✓ | JUnit XML report, grouped by codeunit as `<testsuite>`. |
| `--dump-csharp DIR` | ✓ | ✓ | v2 dumps BC `Compilation.Emit`'s intermediate C# per AL object. |
| `--precompile <in> --out <out>` | ✓ | ✓ | Same. |
| `--bundled` / `--per-suite` | (n/a) | ✓ | v2's pipeline mode toggle. Default `--bundled`. |
| `--server` (JSON-RPC daemon) | (n/a) | ✓ | Long-running warm-state daemon over stdin/stdout, used by the VS Code extension. See `docs/server-mode.md`. Not to be confused with the deferred DAP debug adapter below. |
| `--help`, `-h`, `help` | ✓ | ✓ | Same. |
| `--version` | ✗ (rejected as an unknown path — v1 treats it as a positional bundle argument, so it fails with `Error: file or directory not found: --version`) | ✓ | Because v1 rejects it and v2 accepts it, `--version` doubles as a v1-vs-v2 discriminator: a non-zero exit with that message is itself the v1 signal. |

## Deferred — accepted as v2 followups

These v1 surfaces are not in v2 yet. Each has a tracking issue. None block the
typical "compile and run AL tests" workflow.

| Flag / feature | Why deferred | Estimated lift |
|---|---|---|
| DAP debug adapter | v1's `DapServer.cs`. Needs an AL→C# source map BC's `Compilation.Emit` does not currently expose — without that, breakpoints would land on C# lines, not AL. Distinct from `--server`, the JSON-RPC daemon, which IS implemented (see below). | 1-2 wk (research-heavy) |
| `--coverage` (cobertura XML) | v1 hooked the Roslyn rewriter to inject hit-counters. v2 has no rewrite pass on AL output. A Cecil post-pass over the emitted DLL is feasible. | 2-4 d |
| `--stubs DIR` | v1's stub-merge path. v2 loads real MS DLLs in-process so the original use case mostly evaporates, but the "extra source roots" capability still has value for partial extensions. | <1 d |
| Telemetry / crash reporter | v1's `TelemetryReporter.cs` phoned home to App Insights. `tools/telemetry-triage/` still exists. Needs a secret-handling decision before re-enabling. | 1 d |

## Dropped — architectural mismatch with v2

These v1 features have been removed and are not planned for reinstatement
unless a concrete user need surfaces.

| Feature | Why dropped |
|---|---|
| `--extract-deps` subcommand | v1's 121 KB dep-slicer existed because v1 needed the **full minimal AL surface** of each dependency to compile against. v2 just loads the dependency `.app` directly via `DependencyLoader` and links against its embedded DLL. Memory cost is a few hundred MB more per run — acceptable for the workflow. |
| In-tree stubs (`AlRunner/stubs/*.al`, `AlRunner/Runtime/MockX.cs`) | v1 needed stubs because the rewriter couldn't satisfy MS DLL signatures otherwise. v2 satisfies them by loading the MS DLLs as-is. |
| Roslyn rewriter / type-rename pass | The premise — that BC types could be safely renamed — was incompatible with linking against MS R2R DLLs. v2's only rewriter is `Rewriters/CallSiteArgWrap.cs` (121 LOC, IL-byte-equivalent to BC's own emit). |
| `docs/coverage.yaml` / `docs/coverage.md` | v1 tracked AL-language coverage in a hand-curated YAML; the orchestrator blocked merges if the YAML wasn't updated. v2's spec is the `tests/al-language/` corpus itself — every AL surface that needs coverage has a test there. Both files are archived under `docs/archive/`. |
| `coverage-demo.yml` GitHub workflow | Demonstrated v1's `--coverage` flag. Re-enable when v2 implements coverage. |
| .NET 9 / .NET 10 target frameworks | v1 multi-targeted; v2 targets `net8.0` only, to run on BC's own real .NET 8 runtime rather than reimplementing its runtime-dependent behavior on a newer BCL. net10's BCL drift breaks BC's `UnsafeAccessor`/precode assumptions; net9 can't satisfy BC's `System.Text.Json` 10 bind. Breaking change for any consumer targeting net9/net10 exclusively. |

## Layout change

```
v1                             v2
──                             ──
AlRunner/                      AlRunner/                  (renamed; v2 source lives here now)
AlRunner.Tests/                AlRunner.Tests/            (kept and grown; C# integration tests for
                                                            v2's own runtime/CLI behavior — distinct
                                                            from AL-language coverage, which lives in
                                                            tests/al-language/ + tests/runner-extras/)
SymbolProbe/                   SymbolProbe/               (kept; diagnostic tool, framework-agnostic)
tools/                         tools/                     (kept; DownloadArtifacts used by csproj target)
scripts/                       scripts/                   (kept)
tests/bucket-1/                tests/archive/bucket-1/    (frozen; will be deleted once corpus covers it)
tests/bucket-2/                tests/archive/bucket-2/    (frozen)
tests/excluded/                tests/archive/excluded/    (frozen)
tests/stubs/                   tests/archive/stubs/       (frozen — v2 has no stubs concept)
…                              tests/al-language/         (NEW — git submodule, canonical corpus)
…                              tests/expectations/        (NEW — runner-owned manifest for OOS tests)
…                              tests/runner-extras/       (NEW — runner-specific positive tests)
spike/v2/Runner/               (deleted; promoted to AlRunner/)
```

The full ruleset for the new layout lives in
`.claude/rules/al-language-submodule.md`.

## Updating an existing checkout

```bash
git fetch
git checkout main
git submodule update --init --recursive
dotnet build AlRunner.slnx -c Release
dotnet run --project AlRunner -c Release -- tests/al-language/tests/al-language
```

If you install via NuGet:

```bash
dotnet tool update --global MSDyn365BC.AL.Runner --version 2.0.0
al-runner --help
```

## I want my flag back

If a flag listed under "deferred" or "dropped" blocks your workflow, open a
GitHub issue describing the use case. The deferred list is prioritised by
demand; dropped items can be re-evaluated with concrete reproducers.
