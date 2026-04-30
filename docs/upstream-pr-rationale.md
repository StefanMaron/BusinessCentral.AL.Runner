# AL.Runner Protocol v2 — Upstream PR Rationale

**Branch:** `feat/alchemist-protocol-v1` (fork at SShadowS/BusinessCentral.AL.Runner)
**Scope:** 40 commits, ~7600 lines, 270 tests pass, 6 pre-existing failures unchanged.
**Audience:** upstream maintainers reviewing this PR.

**Reading order:** the doc is in four layers. **Plan E1** (the bulk below) covers the foundational design — `#line` directives, NDJSON streaming, per-test isolation, the original 21 commits. **Plan E3 / E4 / E5** at the bottom are correctness work discovered during downstream consumer integration with ALchemist. Each later section addresses a specific bug class that the Plan E1 design exposed but did not break the protocol contract — they are refinements, not redirections. Companion `Gaps.md` (in the repo root) tracks the audit history of all confirmed/resolved/non-issue gaps with the verification tests that pin each.

---

## The Problem That Drove It

ALchemist v0.4.0 surfaced two correctness gaps + several UX gaps that all rooted in the AL.Runner `--server` runtests response shape:

1. **Inline error decoration at wrong line.** AL.Runner stripped `alSourceLine`/`alSourceColumn` from `runtests` responses even though it had them. For runtime errors (not assertions) the deepest user frame was buried in a text `stackTrace` blob — no structured way to surface "the .al line where the error actually happened."
2. **Coverage gutter icons missing.** `runtests` had no coverage data. CLI mode's `--coverage` writes `cobertura.xml` to disk; server mode skipped coverage entirely.
3. **`runtests` sparse vs `execute`.** Per-test `Message()` output, `capturedValues`, iteration data — all silently dropped during `runtests` serialization but emitted by `execute`.
4. **Server runs everything.** No way to narrow a run to a single test or codeunit.
5. **No live updates.** Test Explorer waited for the entire run; stale UI for slow suites.
6. **No mid-run cancel.** Stop button wired to nothing useful.

---

## The Foundational Primitive: `#line` Directives

Single most impactful change. Spec argument: once Roslyn writes `.al` filenames into IL pdb sequence points, every other feature falls into place using **standard .NET machinery** — no custom runtime walking, no custom attribute scrapers, future debugger work uses the same data.

**Why this is the right primitive:**
- `StackFrame.GetFileName()` returns `.al` paths natively.
- Coverage tools read pdb sequence points without modification.
- One source of truth — eliminates the parallel `AlScope.LastStatementHit` shadow-tracker drift.
- A future Debug Adapter Protocol implementation drops in cleanly.

**Why a fork branch first:** the BC transpiler is upstream/external. We can't change the AL→C# emitter. The cleanest insertion point is **post-rewrite syntax-tree manipulation**: walk the rewritten C# tree, find `StmtHit(N)` / `CStmtHit(N)` markers (which already encode `(scope, stmtIdx)`), look up `(scope, stmtIdx) → AL line` from `SourceLineMapper._sourceSpans`, prepend `#line N "src/Foo.al"` as leading trivia. No transpiler change. Plus: gate Roslyn at `OptimizationLevel.Debug` and emit portable PDB so `Assembly.Load(asmBytes, pdbBytes)` makes stack traces honor the directives.

---

## The 12-Task Implementation Defense

| # | Change | Defense |
|---|---|---|
| 1 | `protocol-v2.schema.json` | Single source of truth across two repos. ALchemist's tests validate emitted lines against it; drift caught immediately. |
| 2 | `AlStackFrame`, `AlErrorKind`, `FramePresentationHint`, `TestFilter` types | Pure value types; foundation for everything that follows. |
| 3 | `StackFrameMapper` | Reads standard managed `Exception.StackTrace` text. Classifies frames: `.al` filename → user code (Normal); `AlRunner.Runtime.*` / `Microsoft.Dynamics.*` → Subtle. Returns DAP-shaped frames. **Why suffix-prefixed predicates and not bare `Mock*`:** initial review caught false positives on user codeunits named `MockAccountTest`. The narrow predicate set (`AlRunner.Runtime.` + `Microsoft.Dynamics.`) is the canonical truth. |
| 4 | `ErrorClassifier` | Heuristics on exception type-name suffix — assertion / runtime / compile / setup / timeout / unknown. Drives IDE UI variation. **Setup is real:** T8 wires `insideTestProc` flag flipped just before `OnRun.Invoke`, so exceptions during `InitializeComponent` correctly classify as setup. |
| 5 | `CoverageReport.ToJson` | Mirrors existing `WriteCobertura` resolution loop, producing structured per-file `FileCoverage[]` for inline emission. **Sums hits, not max-1:** matches the `Plan A → Plan B+D` IDE rendering goal where multi-statement lines deserve detail. Cobertura keeps clamp-to-1 for external-tool compatibility. Aggregator extracted, both consumers share the resolution logic. |
| 6 | `LineDirectiveInjector` | **The foundational change.** Trivia-injected `#line N "path.al"` before every `StmtHit`/`CStmtHit`-anchored statement. Plus `OptimizationLevel.Debug` (PDB sequence points need it) and portable-PDB emit. Gated by `EmitLineDirectives` flag — CLI users opt in. Reviewer caught: re-entry guard skips injection if statement already has a `LineDirectiveTriviaSyntax`; tests cover spaces in paths, `CStmtHit` if-statement branch, line-number correctness against fixture. |
| 7 | `Executor.RunTests` revised | Adds `TestFilter`, `onTestComplete`, `CancellationToken`. **Per-test isolation via `AsyncLocal<TestExecutionState>`:** `MessageCapture`/`ValueCapture` dual-write — always to the per-test scope (so `TestEvent.messages`/`capturedValues` work) AND to the global aggregate when `Enable()` is set (so existing pipeline-level capture keeps working). `BuildErrorResult` extracted across the 5 catch branches — single source of truth for failure-result construction. |
| 8 | `Server.cs cancel` command | New JSON-RPC command. Sets `_activeRequestCts`. Acks `{type:"ack",command:"cancel",noop:bool}`. **Concurrent dispatch** so cancel arrives mid-run: dispatch loop reads stdin while `runtests` streams; only `cancel` is permitted as side-channel. `_outputLock` semaphore serializes writes between the dispatch loop and the runtests worker. Wins: true cooperative mid-run cancel without breaking stdin protocol simplicity. |
| 9 | `Server.cs` NDJSON streaming + protocol v2 | One `{type:"test"}` line per test as it completes; terminal `{type:"summary",protocolVersion:2}`. Field parity with execute response (`alSourceLine`, `errorKind`, `stackFrames` DAP-shaped, `messages`, `capturedValues`, structured `coverage`). Forward-compat: schema permits unknown fields; v1 clients fall back via summary `protocolVersion` absence. |
| 10 | Cache extension | `CompilationCache` carries `SourceSpans`, `ScopeToObject`, `TotalStatements`, `SourceFileMapper`/`SourceLineMapper` snapshots. **Why all of them:** cache hits previously bypassed the rewrite/compile pipeline, so coverage couldn't be emitted on second-run-of-same-project. The static singletons (`SourceFileMapper`, `SourceLineMapper`) are written by Pipeline only on cache-miss; on cache-hit, multi-slot LRU rotation A→B→A would leave them stale. Snapshot+restore makes correctness independent of hit/miss. |
| 11 | Schema validation test | Real-binary smoke output validates against `protocol-v2.schema.json`. Catches drift between emitter and contract immediately. |
| 12 | E2E smoke against built Release binary | Recorded in `docs/protocol-v2-samples/runtests-coverage-success.ndjson`. ALchemist consumes the same shape in unit tests without spawning a runner. |

---

## The Late Fix (`f2d2bb3`)

Discovered after Plan E2 ALchemist consumer shipped: **passing tests didn't get `alSourceFile`**. The original spec defined `alSourceFile` as "deepest user frame from stack walk" — only populated on failures (where there's an exception to walk). Passing tests had `alSourceFile: undefined`, breaking ALchemist's inline-decoration file-filter.

Two fixes in one commit:

1. **Per-test `alSourceFile` fallback.** When stack-walk doesn't fire (passing test), populate `t.AlSourceFile` from `SourceFileMapper.GetFile(t.CodeunitName)` so the test event always carries a file context. Cheap because the runner already has this mapping for cobertura emission.
2. **Per-capture `alSourceFile` (the load-bearing one).** Each captured-value record now carries `alSourceFile` resolved from its own `objectName`. Captures from a codeunit invoked indirectly by the test (e.g. `TestCU` calls into `CU1`) correctly attribute to `CU1.al`, not the test's file — matching where the assignment actually happened in the user's editor.

This is genuinely a runner concern, not consumer-side: only the runner has the authoritative `objectName → file` mapping (`SourceFileMapper`). ALchemist would otherwise need to duplicate that map.

---

## Defensible Architectural Invariants

1. **`#line` directives are the primitive.** Every other feature builds on standard .NET machinery (`StackFrame.GetFileName`, pdb sequence points, native debugger attach). No custom runtime walking, no parallel attribute scraping.
2. **NDJSON streaming.** One request → multiple response lines (`type` discriminator). Forward-compat to future event types.
3. **Protocol versioning.** Summary's `protocolVersion: 2` is the single discriminator. v1 clients silently fall back without breakage.
4. **Backward compat preserved.** Existing CLI users (cobertura.xml writers, `--dump-csharp`, `--run-procedure`) unchanged. The 6 pre-existing test failures are summary-line-format-string-related, predate this branch, untouched.

---

## Suggested Upstream PR Split (9 PRs)

From the Plan E1 final review:

1. **Protocol surface** — `protocol-v2.schema.json` + types (`AlStackFrame`, `AlErrorKind`, `FramePresentationHint`, `TestFilter`). Pure additions.
2. **`StackFrameMapper`** — exception → structured DAP-aligned frames.
3. **`ErrorClassifier`** — exception → error-kind enum (assertion/runtime/compile/setup/timeout/unknown).
4. **`CoverageReport.ToJson`** + Cobertura aggregator extraction. Both consumers share resolution logic.
5. **`#line` directive injector + portable PDB** (the perf-hazard one — gated by `EmitLineDirectives` flag, CLI opt-in).
6. **`Executor.RunTests` revised** — `TestFilter` + `onTestComplete` + `CancellationToken` + AsyncLocal per-test isolation.
7. **Server `cancel` command** — new JSON-RPC command + `_activeRequestCts` lifecycle.
8. **Server NDJSON streaming + protocol v2** (the capstone) — concurrent dispatch, field-parity test event, terminal summary with `protocolVersion: 2`. Cache extension lands here.
9. **Per-capture `alSourceFile`** (`f2d2bb3`) — depends on PR 8.

---

## Bottom-Line Defense (Plan E1)

Every change addresses a concrete shipped-product gap in ALchemist v0.4.0 that the AL.Runner protocol shape made unfixable downstream. The architectural choice (post-rewrite trivia injection rather than transpiler change) keeps us out of the BC compiler. The cache-singleton snapshot work and the AsyncLocal per-test isolation are correctness fixes that benefit any future server consumer, not just ALchemist.

The fork-first development model meant we could iterate end-to-end against a real workload (Sentinel + ALchemist) before splitting into reviewable upstream PRs — surfacing real bugs (e.g. the per-capture `alSourceFile` gap) that a paper-only spec review would have missed.

---

## Plan E3 — Wire Format Hardening (commits `35e8f8a` → `f3c1c9e`, 7 commits)

The Plan E1 design shipped functionally complete. End-to-end use against ALchemist + ALProject4 (a user's real BC project) surfaced three pre-Plan-E1-design wire-format issues:

### 1. Source paths depended on the spawner's cwd

**Symptom.** ALchemist's inline-capture filter dropped every capture even though the runner emitted captures with `alSourceFile` populated.

**Root cause.** `Pipeline.cs:457,473` registered file paths via `Path.GetRelativePath(Directory.GetCurrentDirectory(), file)`. When ALchemist spawned the runner from VS Code's extension host, cwd was `C:\Users\<user>\AppData\Local\Programs\Microsoft VS Code\` — emitted paths walked up four levels: `../../../../Documents/AL/<project>/CU1.al`. ALchemist's `path.resolve(workspacePath, sourceFile)` then resolved to a non-existent location.

**Fix.** `Path.GetFullPath(file).Replace('\\', '/')` — absolute, forward-slash, cwd-independent. Wire format becomes platform-stable.

**Commit:** `35e8f8a fix(server): emit absolute paths in SourceFileMapper`. Test: `SourceFilePathEmissionTests` (4 tests, deliberately spawned from a foreign cwd).

### 2. v2 summary dropped iteration data even though Pipeline collected it

**Symptom.** ALchemist's iteration table panel and CodeLens stepper silently degraded to no-op. The CodeLens showed `⟳ N/M` correctly but stepping had no per-iteration data to render.

**Root cause.** Pipeline.cs collected per-loop iteration data (Plan E1 v1 `--output-json` path emits it) but `Server.cs:SerializeSummary` never plumbed it through. The v2 summary's `iterations` field was missing entirely.

**Fix.** Three coordinated changes in `45aeb4a feat(server): emit iterations in v2 summary`:
1. Add `IterationTracking` to `ServerRequest` DTO.
2. Always-inject `IterationInjector` calls (no longer gated on the request flag) — cached assemblies serve both `iterationTracking=true` and `=false` requests without recompilation. Mirrors the established `ValueCaptureInjector` always-inject pattern.
3. Wrap the streaming `Executor.RunTests` with `Reset+Enable` / `Disable` of `IterationTracker` when `iterationTracking` requested. Collect `Runtime.IterationTracker.GetLoops()` after the run and emit on the v2 summary.

Wire shape mirrors v1 `--output-json` `iterations[]` exactly so existing consumers don't need a translator. Field omitted (null) when not requested via `WhenWritingNull` serialization.

**`Reset()` semantics tightened.** `7dfb2d2 refactor(server): tighten Reset semantics + concurrency comment + test` — Reset now also clears `_enabled`, returning the tracker to a known disabled ground state. Defensive correctness only (current call sites all `Enable` after `Reset`); makes the contract a true ground-state reset.

### 3. Schema documented nullable types but runtime omits empty fields

**Symptom.** Initial schema validation passed, but the documented type unions (`["array", "null"]` etc.) misled consumers about wire reality.

**Root cause.** Server emits with `JsonOpts.DefaultIgnoreCondition = WhenWritingNull` — null/empty fields are **omitted** from the JSON entirely, never emitted as `"key": null`. The schema's nullable-union types described a wire shape the runtime never produces.

**Fix.** `7b28ab4 docs(schema): document absolute paths and iterations in v2 summary` + `ed6e2ca docs(schema): tighten nullable types to match WhenWritingNull serialization`:
- `coverage[].file` and `capturedValues[].alSourceFile` documented as absolute fwd-slash paths.
- `IterationLoop` definition added to `Summary.iterations`.
- `Ready` and `ShutdownAck` definitions added so AJV can validate real protocol output (`{"ready":true}` and `{"status":"shutting down"}` are real wire lines).
- `parentLoopId`, `parentIteration`, `steps[].messages`, `Summary.iterations` tightened from `["X","null"]` to `"X"` with descriptions saying "field is omitted entirely when ..." instead of the contradictory "null at top level".

Schema is now a true contract: any field listed is emitted; missing fields mean "omitted" not "null".

**`docs/protocol-v2-samples/runtests-iterations.ndjson`** captures a real session for downstream cross-checking.

### 4. Living gap inventory (`Gaps.md`)

`f3c1c9e docs: add Gaps.md tracking known/suspected protocol-v2 gaps` — created an audit-trail document at the repo root. Each gap entry has a status (Confirmed / Suspected / Resolved), a surface description, downstream impact, fix candidates, and a verification-test reference. The doc is updated alongside this PR's commits to reflect each fix.

---

## Plan E4 — Per-Test Scope as Iteration Delta Source (commits `94253ea`, `988c575`, `9f23dce`)

`Server.cs` v2 streaming path emitted `iterations[].steps[].capturedValues = []` and `iterations[].steps[].messages = []` regardless of how many captures/messages actually fired during the iteration. Stepper indicator updated correctly; inline values disappeared.

**Root cause.** `IterationTracker.FinalizeIteration` snapshotted from `ValueCapture.GetCaptures()` and `MessageCapture.GetMessages()`. Both are GLOBAL aggregates only populated when their respective `Enable()` was called — the legacy v1 `--output-json` path does this; the v2 streaming `Executor.RunTests` path does NOT (per Plan E1's per-test isolation, captures go to `TestExecutionScope.Current` only). Global aggregate stayed empty → delta was always 0..0 → step.capturedValues/messages always empty.

**Fix in `94253ea fix(iteration): read iteration delta from TestExecutionScope, not global`:** switch the snapshot source to `TestExecutionScope.Current.CapturedValues` / `.Messages`. Both v1 and v2 paths share this code, so both fix simultaneously.

`988c575 docs(iteration): clarify scope-null assumption + align test comment` — explicit comment in `FinalizeIteration` documenting the scope-non-null assumption (guaranteed today by `Executor.RunTests` wrapping the test in `TestExecutionScope.Begin`).

`9f23dce test(gaps): empirical verification tests for Gaps.md G2-G8` — created `AlRunner.Tests/GapVerificationTests.cs` with one `[Fact]` per Suspected gap. Empirically determined which suspicions were real vs non-issues. Findings documented in `Gaps.md`.

---

## Plan E5 — Per-Loop Accumulators + Loop-Variable Capture + Casing (commits `ca19ba7` → `ebd68a9`, 9 commits)

Three Confirmed gaps from the Plan E4 verification audit:

### G2 — Loop variable absent from per-iteration captures

`for i := 1 to 10 do begin ... end` — runner emitted ONE capture for `i` at test scope (final value 10). Per-iteration `i` was never captured because the AL→C# rewriter only instruments assignment targets (`sum` from `sum += i`), not loop counters (managed by the runtime).

**Fix in `0e2f842 feat(iteration): inject per-iteration loop-variable capture`:** `IterationInjector.Inject` now extracts the loop variable name from `ForStatementSyntax` and injects a `ValueCapture.Capture` call after `EnterIteration`. While and do-while loops have no inline-declared loop variable, so the injection is conditional.

**AL→C# transpiler reality found during implementation:** the BC compiler does NOT emit `for (int i = ...; ...; ...)`. It emits `this.i = 1; for (; this.i <= @tmp0; )` — loop counter is a class field on the scope class, not a C# local; `Declaration` is null. Implementation extracts from `Condition` LHS (`MemberAccessExpression` of form `this.<name>`) with a `Declaration` fallback for forward-compat.

**`67ca347 fix(iteration): use AL object name for loop-variable capture`** — follow-up: the original cut used `_currentScopeClass` for both `scopeName` and `objectName` arguments. That broke `alSourceFile` resolution downstream because `Server.cs:SerializeTestEvent` resolves via `SourceFileMapper.GetFile(c.ObjectName)` which is keyed by AL object name (`"LoopTest"`), not by scope class name (`"RunsLoop_Scope__1409475562"`). Thread `ScopeToObject` mapping into the injector.

### G4 — Nested loop captures double-counted

When loop B ran inside loop A's iteration N, `FinalizeIteration` for outer iteration N read all captures since outer iteration N started — that span included every capture inside inner loop B, so inner captures appeared in both the inner step AND the outer step.

**Root cause.** Snapshot/delta math against a flat shared list (`TestExecutionScope.Current.CapturedValues`). Nested loops produce overlapping index ranges; outer iteration N's range subsumes inner loop B's entire execution.

**Fix in `ca19ba7 fix(iteration): per-loop capture accumulators`:** replaced the snapshot/delta math with **per-loop accumulator lists**. Each `ActiveLoop` carries `CurrentIterationCaptures` and `CurrentIterationMessages`. `ValueCapture.Capture` and `MessageCapture.Capture` push to the INNERMOST active loop's accumulator (`_loopStack.Peek()`). `EnterIteration` clears the lists; `FinalizeIteration` copies them into the `IterationStep`. Each capture lands in exactly one loop's iteration step by construction. The snapshot/delta math is gone.

This also supersedes Plan E4's `TestExecutionScope`-based reading: `FinalizeIteration` now reads directly from accumulators, not from the test scope. The Plan E4 contract (per-iteration captures populate) is preserved with a cleaner state machine.

`6779e53 docs(iteration): tighten comments after Plan E5 Group A review` — comment polish.

### G8 — Variable-name casing passes through declaration verbatim

AL is case-insensitive for identifiers. The runner emits `variableName` with the AL `var` declaration's case (`myint` if declared `myint: Integer`). If source code uses a different case (`myInt`), consumer-side case-sensitive Map.get fails.

**Decision:** consumer-side fix only. Per Gaps.md G8 entry's own recommendation, ALchemist does case-insensitive lookup; runner emits declaration case verbatim (no normalization). Both v1 and v2 producers preserve the runner's behavior unchanged. Resolved in ALchemist commit `6868811` (out of scope for this PR but referenced in `Gaps.md` R8).

### Other Plan E5 commits

- `5347678 refactor(injectors): extract IsPlumbingField to shared helper` — `ValueCaptureInjector` and `IterationInjector` had divergent copies of an `IsPlumbingField` helper (filters β/γ-prefixed transpiler-internal temps). Consolidated into `PlumbingFieldFilter`.
- `e1cff13 test(gaps): retire obsolete G2/G4 verification tests` — the original `G2_*` and `G4_*` verification tests pinned the BAD behavior (loop variable absent, nested double-count). Plan E5 fixes both. The `*_Fixed` regression tests added during implementation are the new pins; the originals were retired.
- `ebd68a9 docs(gaps): G2/G4/G8 moved to Resolved (Plan E5)` — Gaps.md sweep. The Confirmed section is now empty (all confirmed gaps resolved or mitigated). G3, G5, G6, G7 remain in the Verified Non-Issue section with their verification tests.

---

## Updated Architectural Invariants

The Plan E1 invariants stand. Plan E3/E4/E5 add three more:

5. **Wire format is cwd-independent.** All emitted paths are absolute, forward-slash. No spawn-time configuration affects the wire shape.
6. **Per-loop capture accumulators.** Each capture lands in EXACTLY ONE iteration step (innermost active loop). No snapshot/delta math, no nested-loop attribution issues.
7. **Schema is a true contract.** Fields listed in `protocol-v2.schema.json` are emitted; missing fields mean omitted (per `WhenWritingNull`), never `"key": null`. Sample NDJSON validates against the schema with AJV in CI.

## Updated Bottom-Line Defense

Plan E1 shipped a well-scoped foundational design. Plans E3–E5 surfaced and fixed three correctness gaps in the wire-format / iteration / capture pathways that paper-only review would have missed:

- Cwd-dependent path emission (E3) only manifests when the runner is spawned from a wildly different cwd than the workspace — exactly what VS Code does.
- Empty per-iteration captures (E4) only manifest in the v2 streaming path, not the legacy v1 `--output-json` path that integration tests exercised.
- Nested-loop double-counting (E5/G4) only manifests with nested AL loops, which the existing v1 fixtures didn't include.

The `Gaps.md` audit format — Suspected → Confirmed → Resolved/Verified non-issue — codifies the discovery + verification pattern so future audits have a starting point.

The fork-first development model continued to pay off: Plans E3/E4/E5 each followed the same pattern (real consumer hits a symptom → trace to runner-side architectural cause → fix at the architectural level → verification test). All 270 passing tests are pinning current behavior; the 6 pre-existing failures (summary-line format-string related) are unchanged.
