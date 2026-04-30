# AL.Runner Known Gaps

Living list of known and suspected gaps in AL.Runner that affect downstream consumers (primarily ALchemist). Each entry has a status: **Confirmed** (reproduced empirically), **Suspected** (architectural inference, not yet reproduced), or **Resolved** (fixed; kept for history).

Add new gaps as they're identified. When fixed, move to a "Resolved" subsection with the commit/plan reference.

---

## Confirmed

_(none — all formerly-confirmed gaps have been resolved. See Resolved section below.)_

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
**Test that catches the regression:** `AlRunner.Tests/IterationTrackerTests.cs::FinalizeIteration_ReadsCapturesAndMessagesFromActiveTestExecutionScope` (Plan E4 A1). **Note:** Plan E5 Group A subsequently replaced the snapshot/delta math entirely with per-loop accumulators (see R7), so the specific code addressed by R5 no longer exists. The behavioral guarantee R5 established remains.

### R6. G2 — Loop-variable per-iteration captures injected by IterationInjector

**Resolved by:** Plan E5 Group B (commit `0e2f842`, with object-name follow-up `67ca347`).
**Replacement:** `IterationInjector.WrapLoop` now extracts the loop variable from `ForStatementSyntax` (via `Declaration.Variables[0]` for inline-declared, or the `Condition`'s `MemberAccessExpression` LHS `this.<name>` for the BC transpiler's pre-loop-init form) and injects a `ValueCapture.Capture` call after `EnterIteration`. The follow-up commit `67ca347` threads the `ScopeToObject` mapping into the injector so captures carry the correct AL ObjectName for `alSourceFile` resolution downstream. The loop variable now flows through the same path as assignment targets and appears in each `step.CapturedValues`. ALchemist's compact-form rendering (`i = 1 ‥ 10 (×10)`) now works for loop variables.
**Test that catches the regression:** `AlRunner.Tests/GapVerificationTests.cs::G2_Fixed_LoopVariableAppearsInPerIterationCaptures`.

### R7. G4 — Per-loop accumulators eliminate nested-loop double-counting

**Resolved by:** Plan E5 Group A (commit `ca19ba7`, with comment fixup `6779e53`).
**Replacement:** `ActiveLoop` now carries `CurrentIterationCaptures` and `CurrentIterationMessages` lists. `ValueCapture.Capture` and `MessageCapture.Capture` push to the INNERMOST active loop's accumulator (one capture, one loop). `EnterIteration` clears the lists; `FinalizeIteration` reads them directly. The snapshot/delta math is gone — nested loops can no longer attribute inner captures to outer steps because each capture only ever lands in one loop's accumulator.
**Test that catches the regression:** `AlRunner.Tests/GapVerificationTests.cs::G4_Fixed_NestedLoopCapturesAttributedToInnermostOnly`.

### R8. G8 — Loop-variable casing mitigated consumer-side

**Status:** Runner-side passthrough is intentional (variable names emitted with declaration case verbatim — no normalization). Consumer-side mitigation: ALchemist's `applyIterationView` and `applyInlineCapturedValues` build a lowercase-keyed shadow Map and lookup with the lowercase source-text spelling. Display still uses the runner's original case for the variable name.
**Resolved (consumer-side) by:** ALchemist Plan E5 Group D (commit `6868811`).
**Test that catches the regression:** `test/suite/decorationManager.perTest.test.ts::case-insensitive variable lookup ...` and `test/integration/iterationStepping.itest.ts::case-insensitive variable lookup during stepping (G8 fix)`. (See `../ALchemist/` for the actual test files.)

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
