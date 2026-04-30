# AL.Runner Known Gaps

Living list of known and suspected gaps in AL.Runner that affect downstream consumers (primarily ALchemist). Each entry has a status: **Confirmed** (reproduced empirically), **Suspected** (architectural inference, not yet reproduced), or **Resolved** (fixed; kept for history).

Add new gaps as they're identified. When fixed, move to a "Resolved" subsection with the commit/plan reference.

---

## Confirmed

### G2. Loop-variable captures emit only at test scope, not per iteration

**Status:** Confirmed empirically (Plan E4 review + GapVerificationTests G2).

**Surface:** For `for i := 1 to 10 do begin ... end`, the runner emits ONE capture for `i` at test scope (statementId 0, value `10` — last). Per-iteration values for `i` are not in `iterations[].steps[].capturedValues` (those only contain assignment targets like `j := i*2` → `j`).

**Evidence:** Plan E4 Group A's `RunTests_V2Summary_IncludesIterations_WhenIterationTrackingRequested` test was tightened during review to assert only `sum`, not `i`, after the implementer reproduced this empirically: the AL→C# rewriter instruments assignment targets (e.g., `sum` from `sum += i`) but NOT the loop counter `i` (managed by the runtime, not by a rewritten statement). The v0.5.6 NDJSON sample confirms: `i` appears once at scope `RunsLoop` (test scope) with value `3` (final value). No per-iteration `i` capture.

**Empirical result (GapVerificationTests G2):** Test `G2_LoopVariableCapturedOnceAtTestScopeNotPerIteration` PASSES. Loop variable `i` is absent from all per-iteration steps; assignment target `sum` is present in each step; `i` appears exactly once in `TestExecutionScope.Current.CapturedValues` with final value "3". Current behavior pinned.

**Downstream impact:** ALchemist's compact-form rendering (`i = 1 ‥ 10 (×10)`) requires multiple captures per `(statementId, variable)`. Loop variables get only one capture → render plain (`i = 10`). Less informative for the user.

**Fix candidates:** Either inject a `ValueCapture.Capture` call for the loop variable at iteration boundary in `IterationInjector.Inject`, OR augment `step.CapturedValues` post-hoc with the loop-variable value sampled at `EnterIteration` time. Both runner-side.

**Verification test:** `AlRunner.Tests/GapVerificationTests.cs::G2_LoopVariableCapturedOnceAtTestScopeNotPerIteration`

### G4. Nested loop capture attribution double-counts inner captures in outer steps

**Status:** Confirmed empirically (GapVerificationTests G4).

**Surface:** When a loop B runs inside loop A's iteration N, `FinalizeIteration` for outer iteration N reads all `TestExecutionScope.Current.CapturedValues` since outer iteration N started. That span includes every capture that occurred inside inner loop B, so those inner captures appear in BOTH the inner loop's step AND the outer loop's step N — double-counting.

**Root cause:** The snapshot+delta math in `IterationTracker.FinalizeIteration` uses a flat index range over `TestExecutionScope.Current.CapturedValues`. Nested loops produce overlapping index ranges: outer iteration N's range subsumes inner loop B's entire execution, so inner captures land in both the inner step (correct) and the outer step (wrong).

**Empirical result (GapVerificationTests G4):** Test `G4_NestedLoopCapturesAttributedToInnermost` PASSES by asserting the double-count: `"inner-j"` appears in both `innerLoop.Steps[0].CapturedValues` AND `outerLoop.Steps[0].CapturedValues`. Current behavior pinned; any future fix that removes the double-count will break this test (update test at that point).

**Downstream impact:** ALchemist's compact-form display would show inflated capture counts for outer-loop iterations when the test contains nested loops. Each outer iteration step includes every inner-loop variable capture from that outer iteration.

**Fix candidates:** Stack-aware finalization: when finalizing outer iteration N, skip any captures that fall within a nested loop's finalized steps (track inner-loop captured ranges and exclude them from the outer delta). Alternatively, during `EnterIteration(inner)`, advance the outer loop's `ValueSnapshotBefore` so its next finalization starts after the inner loop's captures.

**Verification test:** `AlRunner.Tests/GapVerificationTests.cs::G4_NestedLoopCapturesAttributedToInnermost`

### G8. Loop-variable casing in v2 wire format passes through declaration case verbatim

**Status:** Confirmed empirically (GapVerificationTests G8 + prior Plan E2.1 observation).

**Surface:** AL identifiers are case-insensitive in source. The runner emits captures with the variable's DECLARATION case as written in the AL `var` block. If declaration and usage case differ (e.g., `myint: Integer` declared but `myInt` used), the emitted `variableName` matches the declaration spelling, not the usage spelling. ALchemist's `applyIterationView` does case-sensitive `Map.get(varName)` against the source-text spelling — this can cause missed lookups when cases differ.

**Evidence (prior):** Plan E2.1 debugging observed `"variableName":"myint"` for a var declared `myint: Integer` even though source code spelled it `myInt`.

**Empirical result (GapVerificationTests G8):** Test `G8_VariableNameCasingMatchesAlDeclaration` PASSES, confirming the runner emits declaration casing verbatim. For the iterations fixture (all-lowercase declarations), emitted names are all-lowercase. No normalisation is performed. Current behavior pinned.

**Downstream impact:** ALchemist consumers that do case-sensitive lookup on `variableName` may miss variables when AL source mixes case. Risk is low for all-lowercase or all-uppercase declarations (consistent with most AL style guides) but real for mixed-case identifiers.

**Fix candidate:** Either (a) emit normalised lowercase from the runner, OR (b) ALchemist does case-insensitive lookup. Both are consumer-visible breaking changes if not coordinated. Recommend (b) as the less invasive change.

**Verification test:** `AlRunner.Tests/GapVerificationTests.cs::G8_VariableNameCasingMatchesAlDeclaration`

---

## Verified Non-Issue

### G3. Cancellation mid-iteration and IterationTracker state

**Status:** Verified non-issue (GapVerificationTests G3).

**Original suspicion:** When a v2 `cancel` request arrives mid-loop, if `ExitLoop` doesn't fire (e.g. an unhandled exception bypasses the `finally`), `_loopStack` would retain a stale loop and the next test would inherit it.

**Empirical result (GapVerificationTests G3):** Test `G3_CancelMidIterationDoesNotLeaveStaleLoopStack` PASSES. Two defences are in place and both verified:
1. `IterationInjector.Inject` wraps every loop in `try { ... } finally { ExitLoop(loopId); }` — `OperationCanceledException` propagates through `finally` in C#, so ExitLoop always fires.
2. `Server.cs:442` calls `IterationTracker.Reset()` unconditionally before each runtests execution. Even in a hypothetical path that bypassed the finally, the next request starts clean.

Even the worst-case simulation (no ExitLoop, stale `_loops` from request 1) produces a clean tracker after the next `Reset()`. No stale entries survive.

**Verification test:** `AlRunner.Tests/GapVerificationTests.cs::G3_CancelMidIterationDoesNotLeaveStaleLoopStack`

### G5. Zero-iteration loop (`for i := 1 to 0`) wire shape

**Status:** Verified non-issue (GapVerificationTests G5).

**Original suspicion:** A loop that fires `EnterLoop`/`ExitLoop` but never `EnterIteration` might produce a malformed or missing `LoopRecord`.

**Empirical result (GapVerificationTests G5):** Test `G5_ZeroIterationLoopProducesEmptySteps` PASSES. The runner emits a `LoopRecord` with `IterationCount=0` and `Steps=[]` — clean, schema-valid, and consistent with the ALchemist consumer guard `if (loop.iterationCount === 0)`. No malformed output. Boundary case is handled correctly.

**Verification test:** `AlRunner.Tests/GapVerificationTests.cs::G5_ZeroIterationLoopProducesEmptySteps`

### G6. Cache-hit iteration data leakage across request sequence A → B → A

**Status:** Verified non-issue (GapVerificationTests G6).

**Original suspicion:** The v2 server cache-hit path restores `SourceFileMapper`/`SourceLineMapper` snapshots but might not reset `IterationTracker._loops`, causing stale data to leak from one request's iteration capture into the next.

**Empirical result (GapVerificationTests G6):** Test `G6_RequestSequenceDoesNotLeakIterationData` PASSES. `IterationTracker.Reset()` at `Server.cs:442` is called before each runtests iteration-tracking pass, clearing `_loops`, `_loopStack`, `_nextLoopId`, and `_enabled`. The A → B → A sequence produces exactly 1 loop per request, each scoped to only that request's data. No cross-request contamination.

**Verification test:** `AlRunner.Tests/GapVerificationTests.cs::G6_RequestSequenceDoesNotLeakIterationData`

### G7. Schema vs. emission drift on edge fields

**Status:** Verified non-issue (GapVerificationTests G7a/G7b/G7c).

**Original suspicion:** Three edge-field emission patterns might drift from schema: `compilationErrors` on clean runs, `changedFiles` on cache hits, and `alSourceFile` on passing tests.

**Empirical results:**
- **G7a** (`compilationErrors` on clean run): PASSES. Field is absent (or null/empty) on a clean-compile run. Server.cs emits `compilationErrors` only when the map is non-empty. WhenWritingNull drops it. No drift.
- **G7b** (`changedFiles` on cache hit): PASSES. Second identical request returns `cached:true` and `changedFiles` is absent. Server.cs line 715 sets `changedFiles = null` when `cached == true`. No drift.
- **G7c** (`alSourceFile` on ALL test events): PASSES. Every test event (both passing and failing) carries a non-empty `alSourceFile` ending in `.al`. Server.cs line 655 falls back to `SourceFileMapper.GetFile(t.CodeunitName)` for passing tests that lack a stack walk. The v0.5.6 fix is confirmed in place and regression-pinned.

**Verification tests:** `AlRunner.Tests/GapVerificationTests.cs::G7a_CleanRun_SummaryOmitsCompilationErrors`, `G7b_CacheHit_OmitsChangedFiles`, `G7c_AllTestEvents_HaveAlSourceFile`

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

### R5. `IterationTracker.FinalizeIteration` read from global aggregates instead of per-test scope

**Resolved by:** Plan E4 Group A (commit `94253ea`, with comment fixup `988c575`).
**Replacement:** `EnterIteration` snapshot and `FinalizeIteration` delta loop now read from `TestExecutionScope.Current.CapturedValues` and `.Messages` instead of `ValueCapture.GetCaptures()` / `MessageCapture.GetMessages()`. Both v1 (`--output-json`) and v2 (`--server`) paths share this code, so both are fixed.
**Symptom that drove the discovery:** ALchemist's iteration stepper updated the `⟳ N/M` indicator but inline captured-value text disappeared — `step.capturedValues` was always `[]` so there was nothing to render.
**Test that catches the regression:** `AlRunner.Tests/IterationTrackerTests.cs::FinalizeIteration_ReadsCapturesAndMessagesFromActiveTestExecutionScope` (Plan E4 A1).

---

## Verification Tests

When a Suspected gap is investigated, the test that verifies its current state lives at `AlRunner.Tests/GapVerificationTests.cs`. Each test is named `G<N>_<short-description>` and either:

- **PASSES** by asserting the suspected behavior (the gap is real and we accept it for now — the test pins current behavior so silent changes break the test), OR
- **FAILS** if the gap turns out to NOT actually be present (the assertion was wrong; downgrade the gap to non-issue), OR
- **PASSES** by asserting the GOOD behavior the runner already produces (the suspicion was wrong; downgrade the gap).

Each test's assertion message references the Gap ID. The test file is the empirical companion to this document.

---

## How to use this file

- When you find a new gap (architectural pattern, edge case, or schema/runtime drift), add an entry under **Suspected** with: surface, why, evidence/confidence, downstream impact, fix candidates.
- When you reproduce empirically, promote to **Confirmed** AND add a verification test at `AlRunner.Tests/GapVerificationTests.cs::G<N>_*` that pins the current behavior.
- When you fix, move to **Resolved** with the commit reference and a one-line note about the fix shape. Update or replace the verification test with the regression test.
- Cross-reference Plan documents in `../ALchemist/docs/superpowers/plans/` when relevant — this file is the runner-side companion to those plans.
