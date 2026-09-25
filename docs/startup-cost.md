# Fixed per-invocation startup cost

What every `al-runner` invocation pays before its first test, and the rules that keep it down.
The measurement programme is #2366; the levers it selected are grouped under the
`area: startup-cost` label.

## Main JIT tier

**Rule: no loop inside a `catch`, exception filter or `finally` block in the runner assembly.**
Put the loop in a helper method and call the helper from the handler.
`AlRunner.Tests/HandlerLoopJitTierGuardTests.cs` fails on any backward branch inside a handler
in `al-runner.dll`, and on `--version` it reads the JIT's own summary line for `<Main>$`.

### Why

The .NET 8 JIT compiles a method with loops at Tier0 and uses on-stack replacement (OSR) to move
hot loops to optimized code. OSR cannot place a patchpoint inside an exception handler. When a
method has a backward branch inside a handler, the JIT gives up on Tier0 for the **whole
method** and compiles it fully optimized on first call. `DOTNET_JitDisasmSummary=1` reports
this as `Tier-0 switched to FullOpts`.

`<Main>$` holds the top-level statements of `Program.cs`, about 33 KB of IL. Two small loops
(one in a catch filter, one in a `finally`) made the JIT compile all of it FullOpts, 78,929
bytes of native code. The runner JITs `Main` **in every process generation**: the shadow
re-exec parent (`docs/ncl-shadow-runtime.md`) and the child it starts both paid it.
`BcRuntime.ApplyAllPatches` (10 KB of IL) and `TestExecutor.Run` (3.7 KB) had the same shape.

A `leave` from a catch back to a loop head **outside** every handler does not trigger it
(measured: `Instrumented Tier0`), so the guard ignores that `leave`. A `leave` whose target is
inside a handler is that handler's own loop (a retry loop with a try/catch body, sitting in a
catch) and does trigger it, so the guard counts it.

### Measured (#2375)

Trivial bundle (`"platform": "28.0.0.0"`, one empty `[Test]`), BC 28.1.49838.53910, warm private
`--cache`, before and after interleaved. `perf stat -e instructions:u`; "whole invocation"
counts the process tree, "parent only" adds `--no-inherit`. Load average was 9-11 from other
work, so instruction counts are the claim and wall times are not.

| shape | measure | before (`8b85cf45`) | after |
|---|---|---|---|
| CI (`--package-cache ~/.al-runner/platform-apps`) | whole invocation | 12.793 / 12.798 / 12.794G | 12.149 / 12.149 / 12.152G |
| CI | parent only | 1.194 / 1.194 / 1.194G | 0.931 / 0.930 / 0.931G |
| no `--package-cache` | whole invocation | 17.71 / 15.80 / 16.16G | 15.86 / 16.32 / 15.37G |
| no `--package-cache` | parent only | 1.196 / 1.196 / 1.196G | 1.004 / 0.933 / 0.932G |
| — | `al-runner --version` | 0.752G | 0.471G |

The CI shape drops 0.645G (5.0%); the parent alone drops 0.264G (22%). Without
`--package-cache` the whole-invocation spread (about 2G, from hashing VS Code's symbol tree,
#4050) is larger than the change, so that row shows only that the parent moved.

A hello-world `net8.0` console app measures 0.149G on the same box, so the parent's remaining
0.93G is still mostly JIT and type loading for the pre-dispatch code path.

The whole al-language corpus (corpus `0d9d246b`, 3,348 tests) passed 3,348 of 3,348 with both
binaries. Its instruction count varies by about 60G between identical warm runs, so it can show
that nothing regressed, not the size of the gain.

## Startup housekeeping

**Rule: the stale-scratch sweep (#2706) and the package-dedup prune (#2990) run once per
invocation, in its outermost process.** Every re-exec — the shadow hop in `TryShadowReexec` and
the fresh-rewrite hop in `Program.cs` — calls `ProgramSupport.HandOffStartupHousekeeping` on the
child's `ProcessStartInfo`, and the child consumes `AL_RUNNER_STARTUP_HOUSEKEEPING_DONE=1` and
skips both. The `startup_housekeeping` field of the `AL_RUNNER_PHASE_LOG` process row says which
process ran it: `PhaseLogIntegrationTests` pins `[true, false]` for a two-generation run and
`StartupOutputReexecDedupTests` pins `[true, false, false]` for the stacked three-generation one.

The child **clears** the variable as it reads it, so the hand-off covers one generation only. A
process the terminal generation spawns later — a `--jobs` worker, an abort-resume child — sweeps
again, because the processes that leave stale scratch behind are the ones that die during an
invocation. **Trap:** it is not `AL_RUNNER_NCL_SHADOW_DONE`. That variable is also set by hand to
run a shadow directory directly, and such a run has no parent that swept for it.

### Measured (#2375)

The sweep's cost is one enumeration of the temp directory's top level, so it scales with how full
that directory is, and before this change it was paid in every generation. Trivial bundle, BC
28.1.49838.53910, warm `--cache`, `--package-cache ~/.al-runner/platform-apps`, seven interleaved
runs each, load average about 2 on 12 cores, wall time of the whole invocation:

| `TMPDIR` | before (median) | after (median) |
|---|---|---|
| 122,194 entries (this agent box) | 1.76 s | 1.68 s |
| 3 entries (the CI shape) | 1.60 s | 1.59 s |

On the busy directory each sweep took about 85 ms. On a clean one the sweep is a few
milliseconds, so the change is below the noise there.

## Package-directory discovery memo

**Rule: a recursive search for `.alpackages` or `.deps-bin` walks each root once per run.**
`SafeDirectoryScan.Directories` answers those two names from a memo while a
`SafeDirectoryScan.BeginRunMemo()` scope is open, and walks on every call otherwise.
`AlRunner.Tests/SafeDirectoryScanRunMemoTests.cs` pins the scope behaviour, and the
`package_dir_repeat_walks` field of the `AL_RUNNER_PHASE_LOG` process row — asserted `0` in
`PhaseLogIntegrationTests.RealRun_EmitsOrderedPerBundleRecordsAndOneProcessRecord` — pins that a
one-shot run actually opens the scope. `AlRunner.Tests/PackageDirMemoRenewalTests.cs` pins the
renewals: a `--server` request finds an `.alpackages`/`.deps-bin` added after the previous
request, and a second `--watch` cycle walks again.

### Where the scope opens

| mode | scope |
|---|---|
| one-shot CLI, `--dap` | the whole run, opened before the first `.alpackages` scan in `Program.cs` |
| `--watch` | renewed at the top of every cycle after the first |
| `--server` | one per request, inside `RunAllBundlesForServer`; the startup scope is closed before the server goes resident |

A disposed memo is dead: a task that captured it inside a run and outlives the run walks
instead of answering from it.

Never the process: both directories are user-owned, and a developer can add an `.alpackages`
(a symbol download) between two watch cycles or two server requests. The runner itself never
creates either name — provisioning writes to its own artifact directories (#1653) — so within
one run the memo cannot go stale. **Trap:** a name the runner *does* create during a run must not
be added to `MemoizedPatterns`; a later search in the same run would miss the new directory.

The memo keys on the root **as spelled**, because the returned paths are spelled from it. Two
spellings of one directory (a relative and an absolute path) are two entries and two walks.

### Measured (#2218)

Warm page cache, NVMe, BC 28.1.49838.53910 build, the runner pointed at a repository root with
`--package-cache ~/.al-runner/platform-apps`, walks traced per `(pattern, root)`:

| tree | directories | before: walks / repeats / ms in walks | after |
|---|---|---|---|
| fresh worktree of this repository, full run | 2,380 | 358 / 66 / 736 ms | 293 / 1 / 57 ms |
| main checkout with 31 worktrees, first 150 s | 78,203 | 280 / 206 / 83.6 s | 547 / 1 / 1.3 s |

In the second row the root itself was walked 73 times for `.alpackages` (477 ms each) and 135
times for `.deps-bin` (360 ms each) before the change; after it, twice and once. The run did not
finish inside the window either way, so the row compares work done in the same 150 s — after the
change the run had reached 546 distinct roots instead of 74.
