# Cecil migration — architecture decision

**Status:** approved 2026-05-20. Phase 1 begins after the in-flight diagnostic-reporting work lands.

## Decision

Replace JmpHook-based runtime patching with Cecil IL rewriting. All new patches go through Cecil from this point forward. Existing JmpHook patches are migrated in hotspot order. Skeleton-state helpers (NavSession, threadlocals, etc.) stay in C# and are called *from* Cecil-rewritten bodies.

## Why now

### Profile evidence — `bucket-1/codeunit-runtime/01-pure-function`, 988 tests, 36.1s

| % inclusive | Frame | Category |
|---:|---|---|
| 31.7% | `EventSubscriberPatches.EnsureRegistryFresh` / `DoInject` | JmpHook reflection |
| 17.2% | `BcAssembler.Compile` (Roslyn) | Unrelated |
| 16.3% | `RuntimeModule.GetTypes` | Cascading reflection from JmpHook |
| 16.2% | `CustomAttribute.GetCustomAttributes` | Cascading reflection from JmpHook |
| 15.2% | `BcRuntime.SetTestAssembly` | JmpHook install |
| 14.7% | `ApplyNavObjectDictionaryGetTargetHooks` | JmpHook install |
| 14.5% | `EnsureApplied` / `ApplyAllPatches` | JmpHook install |
| **10.8%** | **`TestExecutor.Run`** | **Actual test execution** |

(Inclusive percentages overlap.) Actual test execution is ~11% of the sampled time. ~70% of the runner's "test run" window is JmpHook reflection machinery.

### Bug-class evidence

Several memories document JmpHook-specific traps:
- **R2R inlining bypasses JmpHook** on tiny Ncl methods — multiple sessions lost to this.
- **Calling-convention edge cases** — `ALDatabase.ALSid` segfault traced to a calling-convention bug, not R2R.
- **Tiered JIT undo** — required `<TieredCompilation>false</TieredCompilation>` workaround.
- **Silent signature drift across BC versions** — a hook can install into a wrong-shape method without complaint.

Cecil eliminates all four.

### Code-size evidence

Today's JmpHook patches need: signature match + reflection lookup + address grab + JMP write + (often) calling-convention shim. A typical Cecil patch is 5-30 lines of straightforward IL emission.

## Approach

### What Cecil owns

Method-body rewrites on engine DLLs we are permitted to modify (per `.claude/rules/precompiled-dll-respect.md`):
- `Microsoft.Dynamics.Nav.Ncl.dll`
- `Microsoft.Dynamics.Nav.Types.dll`
- AL Runner's own emitted test DLLs (during the compile pipeline, before the DLL is finalised)

### What stays in C#

Skeleton-state runtime helpers — population of `NavSession`, `NavMethodScope`, threadlocals, in-memory MediaSet/Record backings, etc. Cecil rewrites *call into* these helpers; the helpers don't disappear.

### What's forbidden (unchanged)

`BaseApplication.dll`, `SystemApplication.dll`, ISV-AL DLLs — body contents are sacred. Cecil only modifies engine DLLs and our own emit output.

## Phases

### Phase 0 — Prerequisites
- [x] Cecil rewrite mechanism proven (`NclCecilRewrite.RewriteInPlace`)
- [ ] Diagnostic reporting improvements (in flight — gives us better failure surfacing for the migration)
- [ ] Rewrite-cache: hash (source Ncl mtime + rewriter version) → skip rewrite when cached output is fresh. ~30 lines. Required before Phase 1 to avoid re-rewriting on every startup.

### Phase 1 — EventSubscriberPatches → static IL-wired subscriber table (~31.7% projected)
Replace the runtime reflection registry with a Cecil pass over the test assembly that emits a static `[Subscriber]` lookup table. `DoInject` becomes a direct lookup against the static table. Open questions:
- Cecil pass at compile time, or Roslyn source generator? Cecil is more consistent with the rest of the migration.
- Subscriber dispatch order and recursion guards must be preserved bit-for-bit. Plan: dump the current registry for a known suite, verify the static table dispatches identically.

### Phase 2 — BcRuntime per-assembly hooks → Cecil rewrites on the test assembly (~30% projected)
`SetTestAssembly` and `ApplyNavObjectDictionaryGetTargetHooks` currently install JmpHooks against the emitted test codeunits. Move this work into a Cecil pass inside the AL → C# → DLL compile pipeline, so the emitted DLL has the hooks baked in. Falls inside the "our AL output is meant to be cacheable" contract — the rewrite happens before the DLL is finalised, so the cache stays consistent.

### Phase 3 — Consolidate `EnsureApplied` / `ApplyAllPatches`
After phases 1-2, most of `ApplyAllPatches` is dead code. Audit, delete. Skeleton-state init stays.

### Phase 4 — Migrate remaining hand-written JmpHook patches
In rough order of priority:
- ALDatabase cluster (currently blocked on JmpHook calling-convention bugs — Cecil sidesteps the whole class)
- Field-init patches
- Remaining `AlRunner/Patches/*.cs`

Each migration: ~5-30 lines of Cecil + delete the corresponding JmpHook code. Per-commit verification: 4-bucket smoke must stay within ±1 P of baseline (no silent regressions on the migration).

### Phase 5 — Remove JmpHook infrastructure
Once no callers remain:
- Delete JmpHook implementation
- Re-enable tiered JIT (`<TieredCompilation>true</TieredCompilation>`)

## Cross-cutting rules that still apply

- **No silent no-ops.** Cecil rewrites that can't faithfully execute the BC behaviour MUST throw an AL-Runner-branded exception. See `.claude/rules/loud-failures.md` and `memory/feedback_no_silent_noop.md`. Migration is NOT a license to relax this rule.
- **Precompiled-DLL respect.** Only engine DLLs and our own emit output. See `.claude/rules/precompiled-dll-respect.md`.
- **TDD.** Every Cecil rewrite has a RED test first.

## Maintainability gains

- Cecil failure on version drift is loud (`module.GetType(name)` returns null → fail at rewrite time, not at test run via segfault).
- Cecil rewrites are visible in any IL viewer (ILSpy, dnSpy) — debugging shows the actual executed body, not the JMP target.
- Reduced patch-code surface (estimated 50-70% reduction).
- Per-version fingerprinting becomes trivial — we can refuse to rewrite against an unexpected BC version with a clear diagnostic.

## Risk register

| Risk | Mitigation |
|---|---|
| EventSubscriberPatches recursion-guard and ordering semantics are subtle | Phase 1 spike compares static-table dispatch against current registry dump for a known suite |
| Migration takes longer than expected | The freeze on new JmpHook patches ensures no new throwaway code accumulates during the migration |
| BC version dependency | Cecil-rewrite cache invalidates on BC artifact version change |
| Cecil rewrite cost on cold startup | Phase 0 cache means it runs once per BC version on a clean machine |

## Sequencing (user-specified, 2026-05-20)

1. Finish diagnostic reporting (in flight)
2. Freeze new JmpHook patches (rule effective immediately)
3. Phase 1: EventSubscriberPatches Cecil migration
4. Phases 2-5 in order

## Related

- `.claude/rules/precompiled-dll-respect.md` — what Cecil may and may not touch
- `.claude/rules/loud-failures.md` — Cecil rewrites must throw, not silently default
- `memory/feedback_cecil_migration_freeze.md` — the freeze rule
- `memory/project_cecil_migration_plan.md` — execution plan with profile data
- `memory/feedback_r2r_inlining_traps.md` — bug class that disappears
- `memory/feedback_aldatabase_hard.md` — bug class that disappears
