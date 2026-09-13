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
