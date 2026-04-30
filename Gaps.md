# AL.Runner Known Gaps

Living list of known and suspected gaps in AL.Runner that affect downstream consumers (primarily ALchemist). Each entry has a status: **Confirmed** (reproduced empirically), **Suspected** (architectural inference, not yet reproduced), or **Resolved** (fixed; kept for history).

Add new gaps as they're identified. When fixed, move to a "Resolved" subsection with the commit/plan reference.

---

## Confirmed

### G1. `IterationTracker.FinalizeIteration` reads from global aggregates instead of per-test scope

**Surface:** `Server.cs` v2 streaming path emits `iterations[].steps[].capturedValues = []` and `iterations[].steps[].messages = []` regardless of how many captures/messages actually fired during the iteration.

**Why:** `AlRunner/Runtime/IterationTracker.cs:90` and `:131` snapshot from `ValueCapture.GetCaptures()` / `MessageCapture.GetMessages()`. Both are GLOBAL aggregates only populated when their respective `Enable()` was called — the legacy v1 `--output-json` path does this; the v2 streaming `Executor.RunTests` path does NOT. The v2 path writes to `TestExecutionScope.Current.CapturedValues` / `.Messages` only, so the global stays empty and the iteration delta is always `0..0`.

**Evidence:** `docs/protocol-v2-samples/runtests-iterations.ndjson` after Plan E3 Group A — every step has `"capturedValues":[]`. Test event carries the captures at test scope; iteration steps are empty.

**Downstream impact:** ALchemist's iteration-stepping flow updates the stepper indicator correctly but the inline captured-value text disappears (the data per-iteration is empty). User-visible blank lines in the editor when stepping.

**Fix:** Read from `TestExecutionScope.Current.CapturedValues` / `.Messages` in `EnterIteration`'s snapshot and `FinalizeIteration`'s delta loop. Plan E4 Group A. Both captures and messages share the same architectural fix (parallel one-line changes plus a unit test that exercises both).

**Test that catches the regression:** `AlRunner.Tests/IterationTrackerTests.cs::FinalizeIteration_ReadsCapturesFromActiveTestExecutionScope` (Plan E4 A1).

---

## Suspected (need empirical verification)

### G2. Loop-variable captures emit only at test scope, not per iteration

**Surface:** For `for i := 1 to 10 do begin ... end`, the runner emits ONE capture for `i` at test scope (statementId 0, value `10` — last). Per-iteration values for `i` are not in `iterations[].steps[].capturedValues` (those only contain assignment targets like `j := i*2` → `j`).

**Why suspected:** Confirmed by the v0.5.6 NDJSON sample for `Loop.Codeunit.al` — `i` appears once at scope `RunsLoop` (test scope) with value `3` (final value). No per-iteration `i` capture.

**Downstream impact:** ALchemist's compact-form rendering (`i = 1 ‥ 10 (×10)`) requires multiple captures per `(statementId, variable)`. Loop variables get only one capture → render plain (`i = 10`). Less informative.

**Fix candidates:** Either inject a `ValueCapture.Capture` call for the loop variable at iteration boundary in `IterationInjector.Inject`, OR augment `step.CapturedValues` post-hoc with the loop-variable value sampled at `EnterIteration` time. Both runner-side.

**Confidence:** Confirmed via NDJSON inspection. Reproducer is the existing iterations fixture.

### G3. Cancellation mid-iteration may leave the IterationTracker in an inconsistent state

**Surface:** When a v2 `cancel` request arrives mid-loop, the executor stops the test. If injected `ExitLoop` doesn't fire (e.g. unhandled exception bypasses the finally), `_loopStack` retains an active loop and the next test inherits it.

**Why suspected:** `IterationTracker.cs` line 8-12 comments claim "ExitLoop is called from a finally block, so always runs." Need to verify the IterationInjector emits `try { ... } finally { ExitLoop(loopId); }` and that the AL→C# rewriter preserves it through cancellation (`OperationCanceledException`).

**Downstream impact:** Subsequent v2 requests may see stale loop entries, double-count iterations, or crash on `Peek()` of the wrong loop.

**Fix candidates:** Add `IterationTracker.Reset()` at the top of every v2 `runtests` request. (Server.cs already does this at line 442 — verify.) Also verify `IterationInjector.Inject` wraps the loop body in try/finally.

**Confidence:** Architectural inference. Need a test that cancels mid-loop and verifies tracker state for the NEXT request.

### G4. Nested loop capture attribution may double-count

**Surface:** When a loop B runs inside loop A's iteration N, captures inside B go into `TestExecutionScope.Current.CapturedValues`. After B's `ExitLoop` finalizes B's iterations. Then A's `EnterIteration(N+1)` snapshots — and FinalizeIteration's delta-loop captures EVERYTHING between A's `ValueSnapshotBefore` and the current scope count, including all of B's iteration captures.

**Why suspected:** The snapshot+delta math assumes a flat single-level loop. Nested loops produce overlapping snapshot ranges. Loop A's iteration N's `CapturedValues` may include captures that already appeared in loop B's iteration steps, double-counting them.

**Downstream impact:** ALchemist's compact-form display would show inflated counts for outer-loop iterations of nested-loop variables.

**Fix candidates:** When EnterIteration of a nested loop fires, mark any captures that occur during that nested loop with the nested loopId so the outer-loop's delta loop can skip them. OR use a stack-aware finalization: only attribute captures to the innermost active loop's current iteration.

**Confidence:** Architectural inference. No nested-loop fixture exists yet to reproduce.

### G5. Zero-iteration loop (`for i := 1 to 0`) wire shape may be wrong

**Surface:** AL allows `for i := 1 to 0 do` which never executes the body. EnterLoop fires; EnterIteration never; ExitLoop fires; `IterationCount = 0`; `Steps = []`.

**Why suspected:** Simple boundary case — likely correct, but needs a regression test. ALchemist's `iterationStore.load` has `if (loop.iterationCount === 0)` guard so consumer side is safe. Open question: does AL.Runner emit such loops AT ALL in the v2 summary, or skip them?

**Confidence:** Need a fixture with a never-executed loop and a test asserting either non-emission or emission with `iterationCount: 0`.

### G6. Coverage emission on cache-hit may have stale `linesExecuted` per iteration

**Surface:** v2 server cache-hit path (`Server.cs:286-303`) restores `SourceFileMapper`/`SourceLineMapper` snapshots but doesn't reset `IterationTracker._loops`. Server.cs:442 does `IterationTracker.Reset()` before iteration tracking is enabled — but is this sufficient? Need to verify cache-hit doesn't leak iteration data from the previous request.

**Why suspected:** Coverage state is tracked separately from iteration state. The Reset at 442 should be enough; document it.

**Confidence:** Need a test that runs requests A, B, A and asserts iteration data on the third (cache-hit) request matches the first.

### G7. Schema vs. emission drift on edge cases

**Surface:** Plan E3 Group C found `["string","null"]` schema types where the runtime omits the field entirely (WhenWritingNull). Tightened in v0.5.6. Other places with similar drift may exist.

**Suspect candidates** (not yet audited):
- `Summary.changedFiles` — null-when-cached emission, `["array","null"]` schema?
- `Summary.compilationErrors` — empty-when-no-errors emission, `["array","null"]`?
- `TestEvent.alSourceFile` — required-or-omitted? Documented as absolute fwd-slash, but is the field required when the test passes (no stack)?

**Fix candidate:** Capture an exhaustive sample (multiple test outcomes: pass, fail, error, cancel, cached, fresh, with/without coverage, with/without iteration tracking) and AJV-validate against the schema. Tighten any branch that doesn't fit reality.

**Confidence:** Architectural pattern that bit us once in v0.5.5 → v0.5.6. Worth a one-time exhaustive audit.

### G8. Loop-variable casing in v2 wire format

**Surface:** AL identifiers are case-insensitive in source. The runner emits captures with the variable's DECLARATION case (e.g., `j: Integer; j := i * 2;` → `variableName: "j"`). But AL.Runner may not normalize across uses — e.g., `myInt` declared lowercase in `var` block, used as `myInt` in source — what gets emitted? `myint` or `myInt`?

**Why suspected:** Earlier in Plan E2.1's debugging, we observed `"variableName":"myint"` (lowercase) for a CU1.al var declared `myint: Integer;` even though source code referenced `myInt`. ALchemist's `applyIterationView` does case-sensitive `Map.get(varName)` against the source-text spelling — could miss when cases differ.

**Confidence:** Prior empirical observation but not pinned with a regression test. Risk of consumer-side missed lookups.

**Fix candidate:** Either (a) emit normalized casing (lowercase) from the runner, OR (b) ALchemist does case-insensitive lookup. Both are consumer-visible breaking changes if not coordinated.

---

## Resolved

### R1. AL.Runner emitted source paths via `Path.GetRelativePath(cwd, ...)` — wire format depended on spawner cwd

**Resolved by:** Plan E3 Group A (commit `35e8f8a`).
**Replacement:** `Path.GetFullPath(file).Replace('\\', '/')` — absolute, fwd-slash, cwd-independent.

### R2. v2 summary dropped `iterations` field even though Pipeline collected the data for v1

**Resolved by:** Plan E3 Group B (commit `45aeb4a`).
**Replacement:** Always-inject `IterationInjector` calls (cached assemblies serve both flag values) + thread iterationLoops through `Server.cs:SerializeSummary`.

### R3. Schema declared nullable types but runtime uses `WhenWritingNull` (omits)

**Resolved by:** Plan E3 Group C fixup (commit `ed6e2ca`).
**Affected fields:** `parentLoopId`, `parentIteration`, `steps[].messages`, `Summary.iterations`. Schema tightened to single types; descriptions updated to "omitted entirely" rather than "null".

### R4. `IterationTracker.Reset()` left `_enabled` unchanged — misleading "ground state" semantics

**Resolved by:** Plan E3 Group B fixup (commit `7dfb2d2`).
**Replacement:** `Reset()` now also clears `_enabled` so the contract is "return to known disabled ground state".

---

## How to use this file

- When you find a new gap (architectural pattern, edge case, or schema/runtime drift), add an entry under **Suspected** with: surface, why, evidence/confidence, downstream impact, fix candidates.
- When you reproduce empirically, promote to **Confirmed**.
- When you fix, move to **Resolved** with the commit reference and a one-line note about the fix shape.
- Cross-reference Plan documents in `../ALchemist/docs/superpowers/plans/` when relevant — this file is the runner-side companion to those plans.
