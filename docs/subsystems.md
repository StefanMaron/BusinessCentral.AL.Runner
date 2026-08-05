# BC service-tier subsystem boundary analysis

> **STATUS UPDATE — 2026-05-07.** This document is preserved as historical analysis.
> Several specifics are stale:
>
> - The "**14 patches**" count and the "**633 / 791 tests pass**" baseline are
>   superseded. v2 now applies ~30+ JMP-hook patches and the visible test
>   population is 3,628 (post the 2026-05-07 al-runner.json-discovery fix).
>   Current pass rate: **2,131 / 3,628 (59%)** corpus-wide, OLD architecture
>   (per-suite-subprocess emit + Roslyn compile against pre-rewrite C#).
>
> - The recommendation "**stay reactive**" needs nuance: for state-pollution
>   issues across calls into the BC IL, reactive JMP-hooks remain the right
>   answer (the `--isolation` work in `CLASSIFICATION.md` W-7 still applies).
>   But for the AL→C#→DLL pipeline itself, the architectural pivot is to call
>   BC's `Microsoft.Dynamics.Nav.CodeAnalysis.Compilation.Emit()` directly,
>   which means BC's compiler does the post-emit rewrites natively (ByRef
>   wraps, OnInvoke dispatch, lambda call-site wraps) — no separate v2
>   rewriter. See `CLASSIFICATION.md` header for details.
>
> - The "155 NavMethodScope ctor failures" / "convergence on a tractable JMP-hook
>   patch set" framing was correct for the codeunit-runtime category but the
>   visible failure surface today is broader: top failure classes include
>   `NavApplicationObjectBaseHandle\`1.get_Target` (forms/reports/queries),
>   `NavRecordRef.get_Target`, `NavTestPageHandle.CreateTarget`, etc — see the
>   live `v2-classification.json` for the current histogram.
>
> ## Subsystem analysis still applies
>
> Boundaries identified below (`NavGlobal` is a forwarder; `NCLMetadata` is the
> highest-leverage cut; `NavSession` is a god object only patchable per-property;
> `NavMethodScope` ctor needs body replacement) are correct and continue to drive
> the patch shape. The list of categories at the top is still the right map.
>
> ──────────────────────── original analysis below ────────────────────────

**Status:** spike artifact, architectural analysis only.
**Audience:** anyone deciding whether AlRunner v2 should continue with reactive JMP-hook patches or pivot to principled subsystem replacement at natural interface boundaries.
**Method:** ILSpy-decompiled BC 27.5.46862.48827 service-tier types, read-only reference (decompiled output not committed). Conclusions cross-checked against `AlRunner/BcRuntime.cs` (promoted from `spike/v2/Runner/BcRuntime.cs` at cutover), `docs/archive/spike-bc-abi-identity-findings.md`, the live failure stacks in a since-removed spike snapshot (`spike/v2/results-after-w1.json`, no longer present), and the bc-linux StartupHook (`~/Documents/Repos/community/bc-linux/src/StartupHook/StartupHook.cs`).

---

## TL;DR

The two interesting questions are:

1. **Where in BC's service-tier code is "where the test IL meets the runtime" small enough to replace cleanly, rather than patched method-by-method?**
2. **Which boundaries are clean (interface-shaped, DI-style, swappable via a static field assignment), and which are tangled (concrete sealed classes whose construction reaches across half the service tier)?**

The answers are surprisingly clear and not what the JMP-hook history suggests.

| Subsystem | Boundary shape | Replaceable how |
|---|---|---|
| **NavGlobal** (static facade) | Pure forwarder — every accessor resolves via `NavEnvironment.Instance.Tenants.SystemTenant.<X>` | Replace at the **NavSystemTenant** layer once — every NavGlobal accessor flows through |
| **NavSystemTenant** | Concrete sealed class, ctor reaches DataAccess/Database/Apps/SymbolCache/etc. | Cannot construct a real one. Build a `HeadlessSystemTenant` skeleton via `GetUninitializedObject` + reflection-poke a small set of fields (NCLMetadata, MetadataProvider, NavAppGroupResolver) |
| **NCLMetadata** | Concrete class, ctor takes 5 collaborators, each of which has its own ctor chain | Skeleton instance + replace `GetMetaCodeunitById` / `GetMetaTableById` with reflection lookups against the loaded test assembly. **This is the highest-leverage cut.** |
| **MetadataProvider** | Subclass-per-tenant; concrete ctors reach Apps subsystem | Skeleton + override `GetCodeunitMetadata` / `GetTableMetadata` |
| **NavSession** | 4000-line god object. Auto-properties + concrete fields like `SessionAccessLock`, `DataAccessSource`, `IServiceConnection` | Skeleton instance + targeted JMP-hooks on individual property getters (`get_Company`, `get_AccessLock`, `get_DataAccessSource`). **Cannot be replaced wholesale.** |
| **NavMethodScope** ctor chain | Abstract base + nested `RootMethodScope`/`TryMethodScope`. Each AL test method emits a `Scope_NNN : NavMethodScope<Codeunit>` subclass; ctor reaches `applicationObject.Session.{ServiceConnection,SqlDebuggingStatistics,AccessLock}.X` | Already half-patched. The remaining 155 NRE failures are one or two field reads inside the 3-arg ctor. JMP-hook the 3-arg ctor itself with a minimal replacement — don't try to satisfy the 18-field skeleton. |
| **NavApplicationObjectBase.ctor** | Reaches `NavCurrentThread.ResolveAppGroup(session) → preferredSession.NavAppGroup ?? NavAppGroup.BaseGroup` | `NavAppGroup.BaseGroup` static is loadable; `session.NavAppGroup` returns null on skeleton → falls through to `BaseGroup`. **Probably already works.** Verify, no code change. |
| **TreeHandler / TreeObject** | Real, non-replaceable. Public abstract w/ private nested concrete subclasses. AL-emitted types instantiate `NavComplexValue → TreeObject(parent)` which calls `TreeHandler.CreateTreeHandler(parent, this)` and asserts `parent.Tree != null`. | Already handled by `RootTreeStub` / `RootHandler` in BcRuntime.cs. **Keep this; it works.** |
| **IServiceTopology** | Pure interface, no required behavior past `IsServiceRunningInLocalEnvironment=false` for AL tests | DispatchProxy. **Not actually exercised by AL test execution paths** — this is bc-linux's path, not ours. Skip. |
| **NavRecord / NavRecordHandle** | Concrete classes, deep into NavDatabase, DataAccessSource, persistence | Out of scope for code-only AL tests. record-table bucket tests are deferred to W-6 / Phase 2. Existing AlScope's in-memory store is the only proven design. |
| **Win32 P/Invoke** | kernel32/user32/advapi32 | Already handled — bc-linux's `kernel32_stubs.c`, no change. **Keep**. |
| **NavEnvironment.cctor** | Static ctor touches WindowsIdentity, registry, perf counters | Already JMP-hooked .cctor → skeleton singleton. **Keep**, irreducible. |

**Recommended cut order (single principal change, not an architectural rewrite):**

1. **Add a single replacement for `NavMethodScope..ctor(NavApplicationObjectBase, MethodScopeFlags, bool)`** — closes 155/158 remaining failures. ~80 LOC. **2–3 hours.**
2. After (1), the residual is a long tail of small NRE-shaped issues. Continue reactive JMP-hook patching from there.
3. Defer subsystem-shaped replacement of NCLMetadata / MetadataProvider until a *third* failure category that would benefit from it actually shows up. Today there is no such category.

**The principled-replacement pivot is not warranted by the data.** The full test corpus today is converging on a tractable JMP-hook patch set (~14 patches → 16 patches expected). The boundary that *would* be worth replacing as a subsystem (`NCLMetadata`) is already bypassed by `NavCodeunitHandle.CreateTarget` JMP-hook — adding a `HeadlessNCLMetadata` would only matter if tests started reaching `NCLMetadata` via paths other than `CreateTarget` (`GetMetaTableById` from `NavRecordHandle.CreateTarget` is the obvious one — relevant for record-table buckets, not codeunit-runtime).

**Total effort, full subsystem-replacement migration:** ~6–8 weeks, mostly to discover unknowns inside `NavSession` and `NavTenant`. **Total effort, continuing reactive JMP-hooks:** ~1–2 weeks to clear current failures. **Recommendation: stay reactive.**

---

## Background: what we have today

`AlRunner/BcRuntime.cs` applies 14 patches at process start. Categorized:

| Category | Count | Patches |
|---|---|---|
| **cctor / .cctor replacements** (one-time init that reaches Windows-only APIs) | 1 | `NavEnvironment..cctor` |
| **Property getters returning a skeleton** (instance-shaped subsystem boundary) | 4 | `NavEnvironment.Instance`, `.ServiceAccount`, `.ServiceAccountName`, `NavApplicationObjectBase.Session` |
| **Property getters returning a stub** | 1 | `NavSession.CurrentMethodScope` |
| **Method no-ops** (would NRE on skeleton; effect is irrelevant for tests) | 6 | `VerifyExecutePermission`, `ThrowStackOverflow`, `EmitServerStartupTraceEvents`, `LogALErrorTelemetry`, `Rollback`, `NavCancellationToken.ThrowIf*` |
| **Method body replacements** (substantive logic change) | 2 | `NCLEnumMetadata.Create(int)` → return Default; `NavCodeunitHandle.CreateTarget` → reflection lookup |

Plus `Win32Stubs` (kernel32/user32 P/Invoke registration via `NativeLibrary.SetDllImportResolver`) and the `RootTreeObject`/`RootHandler` ITreeObject pair that satisfies `NavComplexValue.ctor`'s parent-non-null check.

Empirically these get to: 633 / 791 tests now pass (per `results-after-w1.json` corpus run vs. `CLASSIFICATION.md` — *correction: 791 - 158 = 633 expected; results-after-w1.json shows 158 remaining failures of which 155 share one root cause*).

The **remaining failure surface** is concentrated:

- **155** `NavMethodScope..ctor(NavApplicationObjectBase, MethodScopeFlags, bool)` NREs from tests using the codeunit-as-variable pattern (`MyCodeunit.Method(...)`) where the AL compiler emits `Scope_N : NavMethodScope<MyCodeunit>` subclasses.
- **2** compile-time `ConvertToDotNetFormatString` overload mismatches.
- **1** transient process-error.

Everything else passes.

---

## Subsystem-by-subsystem analysis

### Subsystem: `NavGlobal` (static facade)

#### Public surface used by IL we want to run
- `NavGlobal.NCLMetadata` — read by `NavCodeunitHandle.CreateTarget` (already bypassed), `NavRecordHandle.CreateTarget` (called by record-table tests), and various error-path code in `SessionContextHelper.GetALScope`.
- `NavGlobal.SystemTenant` — chained from every other `NavGlobal.*`.
- `NavGlobal.MetadataProvider`, `NavGlobal.MetaObjectCache`, `NavGlobal.NCLCodeLoader` — referenced by AL-emitted code only when AL pulls in metadata, app groups, or extension info. Code-only test buckets do not reach these.
- `NavGlobal.NavAppGroupResolver` — referenced by `NavApplicationObjectBase.ctor` indirectly via `NavCurrentThread.ResolveAppGroup`. **Not via NavGlobal directly** — `ResolveAppGroup` falls back to `NavAppGroup.BaseGroup` static.

#### Concrete classes vs interfaces
`NavGlobal` is a `public static class` with 12 expression-bodied properties, all of the form `=> NavEnvironment.Instance.Tenants.SystemTenant.<X>` (or `.Tenants.DefaultTenant.<X>`). It is itself **not replaceable** — every reference to it bakes the static call into IL. But all real work resolves through `NavSystemTenant`.

#### Replacement strategy
Do not replace `NavGlobal` directly. Either:
- (a) JMP-hook individual `NavGlobal` getters, or
- (b) Build a `HeadlessSystemTenant` and assign it to `NavEnvironment.Instance.Tenants.SystemTenant` (the `Tenants` field exists; verify accessor flow).

Option (b) is cleaner *if* we want a comprehensive metadata/codeunit lookup story. For today's needs (codeunit-runtime tests only), option (a) at single-getter granularity continues to work — and the only call paths that hit `NavGlobal.*` are:

- `NavCodeunitHandle.CreateTarget` → already bypassed.
- `NavRecordHandle.CreateTarget` → not in the 158 remaining failures.
- `SessionContextHelper.GetALScope` → only on error paths; already neutered via `LogALErrorTelemetry` no-op.

#### What it would unblock
Nothing today. This is dormant boundary. Becomes relevant when (a) record-table buckets are migrated, or (b) error-path telemetry reactivates.

#### Estimated effort
**S** for getter-by-getter JMP-hook (matches today's pattern). **M** for skeleton `HeadlessSystemTenant`.

#### Risks / unknowns
`NavSystemTenant` extends `NavTenant`, which has a 100+ field surface. A skeleton via `GetUninitializedObject` then accessing an unpopulated field would NRE — every getter that ends up read becomes a JMP-hook anyway. The "subsystem boundary" framing oversells the simplification.

---

### Subsystem: `NCLMetadata` (codeunit/table metadata lookup)

#### Public surface used by IL we want to run
- `GetMetaCodeunitById(int, bool requireCompiled, int appGroupId = -1)` — used by `NavCodeunitHandle.CreateTarget`. **Already bypassed.**
- `GetMetaTableById(int, bool requireCompiled, int appGroupId = -1)` — used by `NavRecordHandle.CreateTarget` (record-table bucket; deferred).
- `TryGetMetaTableById` — same.

#### Concrete classes vs interfaces
`NCLMetadata` is a **concrete public class** (not an interface), with this constructor:

```
NCLMetadata(NavDatabase ownerDatabase, INCLObjectXmlMetadataLoader xmlLoader,
            INCLCodeLoader codeLoader, INavAppClrTypeRetriever appClrTypeRetriever,
            IMetaObjectCache metaObjectCache)
```

The five collaborators are themselves real BC implementations whose construction reaches across `NavAppMetadataRetriever`, `MetadataBlobStorageProvider`, `NavDatabase`, etc. **Building a real `NCLMetadata` from scratch is infeasible.**

But: `GetMetaCodeunitById` and `GetMetaTableById` are virtual-dispatchable on a `NCLMetadata` instance. A `HeadlessNCLMetadata` could either:
- Subclass and override (if those methods are virtual — they are not virtual in 27.5; verified).
- Or get a skeleton via `GetUninitializedObject` and JMP-hook the two methods.

#### Replacement strategy
- **Today (codeunit-runtime):** keep the existing `NavCodeunitHandle.CreateTarget` JMP-hook. **No NCLMetadata replacement needed.**
- **Phase 2 (record-table):** JMP-hook `NavRecordHandle.CreateTarget` similarly, looking up `RecordNNNN : NavRecord` types from the loaded test assembly. Same pattern as the codeunit case.
- **Optional principled future:** assign a skeleton `NCLMetadata` to `NavSystemTenant.nclMetadata` field (private readonly; reflective-poke). JMP-hook the two `Get*ById` methods. Then `NavGlobal.NCLMetadata.GetMetaCodeunitById(...)` works without any per-call-site JMP-hook.

#### What it would unblock
- 0 tests in the 155-NRE bucket. (NavMethodScope NREs have no NCLMetadata involvement.)
- All future `NavRecordHandle.CreateTarget` failures from record-table buckets — but those will need an in-memory record store regardless.

#### Estimated effort
**S** for the per-callsite JMP-hook pattern (matches W-2 today). **M** for a skeleton `NCLMetadata` parked in `NavSystemTenant`.

#### Risks / unknowns
Some AL constructs (e.g. `Codeunit.Run(ID)` indirect dispatch, EventSubscription discovery, RecordRef) cross `NCLMetadata` paths beyond the two `Get*ById` calls. Hard to enumerate without each test category hitting them in turn. Defer.

---

### Subsystem: `NavSession` (the god object)

#### Public surface used by IL we want to run
From the corpus failure traces, only the following members are read on the skeleton session:
- `Session.CurrentMethodScope` — already JMP-hooked → skeleton root scope.
- `Session.SqlDebuggingStatistics` — auto-property, returns null on skeleton, callers all use `?.` short-circuit. **OK as-is.**
- `Session.ServiceConnection` — auto-property-ish (private field `serviceConnection`). Returns null on skeleton. Caller uses `session.ServiceConnection?.CurrentServiceCallCancellationToken ?? default`. **OK as-is.**
- `Session.AccessLock` — getter for a private auto-property field set in real ctor. Returns null on skeleton. The 3-arg `NavMethodScope.ctor` does not reach AccessLock; downstream code paths might. **Pending verification.**
- `Session.Company` — read inside the 3-arg `NavMethodScope.ctor`'s `parentForm` walk. Only reached if the test runs a `NavForm` subclass (not codeunit). **Not in failure path for codeunit tests.**
- `Session.VerifyExecutePermission(...)` — already JMP-hooked → no-op.
- `Session.DataAccessSource` — read by `Rollback`. Already JMP-hooked → no-op.

#### Concrete classes vs interfaces
`NavSession` is a 4000-line concrete class with no clean interface boundary. Public ctors all require a real `NavTenant`, which requires a real `NavDatabase`, which requires SQL.

#### Replacement strategy
Keep skeleton-via-`GetUninitializedObject` + getter-by-getter JMP-hook. **There is no usable interface boundary here.** The "subsystem replacement" framing does not apply.

#### What it would unblock
N/A — already handled by skeleton + JMP-hooks.

#### Estimated effort
**XS** to add a JMP-hook for an additional getter when one surfaces. **XL+** to attempt wholesale replacement with a `IHeadlessSession` interface (would require introducing a wrapper around every BC type that consumes `NavSession`, which is most of them — that's exactly the 3500-line `AlScope.cs` we are trying to eliminate).

#### Risks / unknowns
The 155-NRE bucket likely terminates inside `NavMethodScope.ctor`'s reads of `session.X` fields where the field is an *eagerly-initialized non-nullable type* (e.g. `SessionAccessLock`). For those, `?.` short-circuit is not how callers access them — they assume non-null. But: tracing the actual NRE site is a 10-minute exercise once you re-run with a single test under `dotnet trace` or LLDB. **Cheaper to investigate than to design around.**

---

### Subsystem: `NavMethodScope` ctor chain (the current hot path)

#### Public surface used by IL we want to run
The AL compiler emits, for every AL `procedure` body:

```csharp
private sealed class TestMethodName_Scope_N : NavMethodScope<MyCodeunit> {
    public TestMethodName_Scope_N(MyCodeunit βparent) : base(βparent) { ... }
    protected override async ValueTask RunAsync() { /* AL body */ }
}
```

Construction goes:
- 1-arg ctor `NavMethodScope<T>(TParent)` →
- 2-arg ctor `NavMethodScope<T>(TParent, bool eventSource)` →
- 2-arg ctor `NavMethodScope(NavApplicationObjectBase, bool)` →
- **3-arg ctor `NavMethodScope(NavApplicationObjectBase, MethodScopeFlags, bool)`** ← NRE site.

The 3-arg ctor reads, in this order:
1. `applicationObject.Session.CurrentMethodScope` — JMP-hooked, returns skeleton root scope. ✓
2. `parentScope = session.CurrentMethodScope` — same. ✓
3. `navMethodScope.IsInTryScope` — flags read on skeleton; flags=RootScope (1), so false. ✓
4. `session.VerifyExecutePermission(applicationObject)` — JMP-hooked → no-op. ✓
5. `session.SqlDebuggingStatistics` — null, `?.` short-circuit. ✓
6. `navMethodScope.cancellationToken` — field read on skeleton; uninitialized (default struct). ✓
7. `navMethodScope.IsRootScope` — flags=1 → true → enters branch.
8. `session.ServiceConnection?.CurrentServiceCallCancellationToken ?? default` — null short-circuits. ✓
9. `RuntimeHelpers.TryEnsureSufficientExecutionStack` — irrelevant.
10. `navMethodScope.StackDepth + 1` — StackDepth field on skeleton = 1 (already initialized in BcRuntime). ✓
11. **`session.CurrentMethodScope = this`** — setter. The setter is the auto-property `set;`. **This is the most likely NRE site if `CurrentMethodScope` is backed by a JMP-hooked getter and a regular setter — the setter writes to a backing field that does not exist on a `GetUninitializedObject` instance unless something later reads it.** Setting a backing field on a skeleton object is fine; the NRE would be on a *subsequent read*.
12. `navMethodScope.TopLevelApplicationObject != null` — `TopLevelApplicationObject` is an auto-property; backing field on skeleton is null. → falls through to else branch.
13. `applicationObject is NavFormExtension` → false for codeunits.
14. `applicationObject as NavForm` → null for codeunits.
15. `while (navForm != null && ...)` — false, skip.
16. `TopLevelApplicationObject = navForm ?? applicationObject` — setter write, fine.

The chain looks like it should *already* succeed end-to-end given the existing patches. The NRE in `results-after-w1.json` says it's inside the 3-arg ctor. The most likely explanation: one of these reads — probably step 11's setter writing `CurrentMethodScope = this`, or the JMP-hooked getter being re-entered later — has a subtle interaction with the JMP-hook trampoline that the spike doesn't account for.

#### Concrete classes vs interfaces
Sealed-from-AL — every test scope is a Microsoft-emitted private sealed nested class. The base class is abstract `NavMethodScope`. **There is no interface to swap out.** The chain has to be made to terminate.

#### Replacement strategy
**Two options:**

1. **Targeted JMP-hook on the 3-arg ctor itself** (the W-1 task): write a replacement that does the minimum work — set `parentScope = session.CurrentMethodScope`, set `flags`, set `StackDepth = parentScope.StackDepth + 1`, set `session.CurrentMethodScope = this`, set `TopLevelApplicationObject = applicationObject`, return. ~30 LOC. **This is the strict W-1 deliverable.**

2. **Skeleton parentScope tuning:** keep the existing patches, but additionally ensure that `session.CurrentMethodScope = this` setter does not silently fail. Verify with one sample test under `dotnet trace`. May find that the existing patch set already works on a clean process and the failures are a JIT-tiering or PrepareMethod ordering issue.

Both are surgical. Neither is a "subsystem replacement." Option (1) is the recommended path.

#### What it would unblock
**155 / 158 remaining failures (98%).**

#### Estimated effort
**S.** 2–3 hours of decompile-trace-write cycle, gated only on getting one test (e.g. `bucket-1/codeunit-runtime/14-assert-130000`) green and then re-running the corpus.

#### Risks / unknowns
- The 3-arg ctor calls `base(applicationObject.Session.CurrentMethodScope)` — meaning `NavScope.ctor(ITreeObject parent)` runs, which calls `TreeObject.ctor(ITreeObject parent)`, which calls `TreeHandler.CreateTreeHandler(parent, this)`. `parent` here is the skeleton root scope; `parent.Tree` is set to a real `RootHandler` by BcRuntime. That should succeed.
- Replacing the 3-arg ctor itself has the usual JMP-hook constraint that the replacement must be method-call-compatible (same instance shape). Already proven for the existing patches.

---

### Subsystem: `NavApplicationObjectBase` ctor (and `NavCurrentThread.ResolveAppGroup`)

#### Public surface used by IL we want to run
- `NavApplicationObjectBase.ctor(ITreeObject parent, ApplicationObjectId, NCLStaticMetadata staticMetadata = null)` runs once per codeunit instantiation.
- It reaches `NavCurrentThread.ResolveAppGroup(session)` which returns `NavAppGroup.BaseGroup` (a static loadable from the assembly, not requiring service-tier init) when `session.NavAppGroup` is null.

#### Replacement strategy
**No replacement required.** The fall-through to `NavAppGroup.BaseGroup` works on a skeleton session (BaseGroup is a static singleton initialized in NavAppGroup.cctor without dependencies). Verify in trace, no code change.

#### What it would unblock
N/A.

#### Estimated effort
**XS** (verification only).

#### Risks / unknowns
`NavAppGroup.BaseGroup`'s cctor might in turn touch a metadata path. If so, JMP-hook the BaseGroup getter or its cctor. Same pattern.

---

### Subsystem: `IServiceTopology`

#### Public surface used by IL we want to run
Properties returning bools/strings about cluster/SQL/cloud configuration.

#### Concrete classes vs interfaces
**Interface**, exposed as `NavEnvironment.Topology` static property. bc-linux replaces it with a `DispatchProxy` (`LinuxTopologyProxy`) that returns `IsServiceRunningInLocalEnvironment=false` and delegates the rest. This is the **only genuinely interface-shaped, DI-replaceable boundary** in the bc-linux patch set.

#### Replacement strategy
DispatchProxy. Trivial.

#### What it would unblock
Nothing in AlRunner. The `IServiceTopology` boundary matters for **service-tier startup** (replication, cluster, ACL APIs) — not for AL test execution. AL-emitted IL does not reach `Topology.*` on any path we run. **Skip.**

#### Estimated effort
N/A.

---

### Subsystem: `NavRecord` / `NavRecordHandle` / database

#### Public surface used by IL we want to run
- `NavRecord` per-table subclasses (table 18 = Customer, table 50100 = user table, etc.) are emitted by the AL compiler.
- `NavRecordHandle.CreateTarget` calls `NavGlobal.NCLMetadata.GetMetaTableById(id, requireCompiled: true).CreateObjectInstance(this, temp, null, "", securityFiltering)`.

#### Concrete classes vs interfaces
`NavRecord` is a 5000-line concrete class. Persistence flows through `NavSession.DataAccessSource` → `DataAccess.GetDataAccessForTable(table)` → real SQL adapters.

#### Replacement strategy
Out of scope for codeunit-runtime tests. Phase 2 work. The existing 3500-line `AlScope.cs` already implements an in-memory record store; the migration question is *port* not *invent*.

#### What it would unblock
~100+ tests in `bucket-1/record-table` and `bucket-2/record-*`.

#### Estimated effort
**XL.** This is the dominant phase-2 cost regardless of which compile-pipeline approach is taken.

#### Risks / unknowns
SetFilter/Find/Next/Modify/Insert/Delete semantics, key handling, FlowFields, computed columns, security filtering. All non-trivial. None of these are subsystem-replacement-shaped — they are *re-implementation*.

---

### Subsystem: `TreeHandler` / `TreeObject` (memory hierarchy)

#### Public surface used by IL we want to run
Every `NavComplexValue` ctor calls `TreeObject.ctor(parent)` which constructs a `TreeHandler`. The `parent` must be a non-null `ITreeObject` whose `Tree` is non-null.

#### Replacement strategy
Already handled by `RootTreeStub` / `RootHandler` in `BcRuntime.cs`. **Keep, irreducible.**

#### Risks / unknowns
None observed.

---

### Subsystem: Win32 P/Invoke

#### Public surface
kernel32, user32, advapi32 calls baked into BC IL.

#### Replacement strategy
`NativeLibrary.SetDllImportResolver` → bc-linux's `kernel32_stubs.so` (already integrated as `Win32Stubs.cs`). **Keep, irreducible.**

---

### Subsystem: `NavEnvironment` (process-wide singleton)

#### Replacement strategy
Already JMP-hooked .cctor + skeleton singleton + getter hooks. **Keep, irreducible.**

#### Risks / unknowns
If new code paths touch `NavEnvironment.Tenants`, `NavEnvironment.NavAppMetadataRetriever`, or `NavEnvironment.TemporaryPathHelper`, more skeleton pokes are needed. Each is a 1-property addition.

---

### Subsystem: Eventing / EventSubscription / Telemetry / License

These reach BC code only on error paths. Already neutered via `LogALErrorTelemetry` no-op and `Rollback` no-op. **Keep.**

If a test deliberately uses event subscriptions (`[EventSubscriber]` AL attribute → `NavEventScope`), this surface activates. None of the 158 remaining failures live here. **Defer.**

---

## Priority ranking

| Subsystem | Today's failures unblocked | Effort | Confidence |
|---|---|---|---|
| `NavMethodScope` 3-arg ctor (targeted patch) | 155 | S | **High** |
| `ConvertToDotNetFormatString` polyfill (W-3) | 2 | XS | High |
| Pass/Fail/Error classification (W-4) | 0 (cosmetic) | XS | High |
| In-process AL emit (W-5) | 0 (perf) | M | High |
| `NavGlobal` / `NCLMetadata` skeleton subsystem replacement | 0 today, ?? in Phase 2 | M | Med — speculative |
| `NavRecord` in-memory store | ~100 (Phase 2) | XL | Low — pure invention work |
| `IServiceTopology` DispatchProxy | 0 | XS | High — but not needed |

## Recommended cut order

1. **W-1 (today):** JMP-hook `NavMethodScope..ctor(NavApplicationObjectBase, MethodScopeFlags, bool)` with a replacement that does the minimum work. Closes 155 NREs.
2. **W-3 (anytime):** add `ConvertToDotNetFormatString(string, object[])` polyfill in `BcAssembler.PolyfillSource`. Closes 2 compile-fails.
3. **W-4 (alongside W-1):** map known BC test exception types in `TestExecutor.RunOne` so `NavAssertionException` → `Fail`, NRE → `Error`, etc.
4. **W-5 (post-stabilization):** replace `dotnet AlRunner --dump-csharp` subprocess with in-process `Compilation.Emit`. ~30× perf win.
5. **Phase 2 (deferred):** record-table buckets need an in-memory record store. Port the existing `AlScope.cs` record machinery to v2 *as-is*; do not redesign. **The subsystem-replacement framing does not help here either.**
6. **No `HeadlessSystemTenant` / `HeadlessNCLMetadata` cut** unless and until a third failure category emerges that genuinely benefits.

## Where the JMP-hook approach stays

Permanently:

- **NavEnvironment.cctor** — Windows registry / WindowsIdentity init, no replaceable boundary.
- **Win32 P/Invoke** — bc-linux's stubs.so is the canonical solution.
- **NavMethodScope.ThrowStackOverflow** — false-positive due to skeleton-tracking gaps; no-op.
- **NavCancellationToken throws** — uninitialized struct trips check.
- **ALTelemetryHelper / SessionTransactionExtensions.Rollback** — error-path neutering.
- **NCLEnumMetadata.Create(int)** — chains through `NavGlobal.MetadataProvider`; returning Default preserves arithmetic.
- **NavCodeunitHandle.CreateTarget** — bypassing `NCLMetadata` directly is *cheaper than building a HeadlessNCLMetadata*.

The JMP-hook surface is small, well-understood, and bounded. It does not grow polynomially.

## Estimated overall effort

| Path | Effort | Risk |
|---|---|---|
| Continue reactive JMP-hooks (current strategy) | 1–2 weeks to clear codeunit-runtime, +3–5 weeks for record-table phase | Low — each new failure is a bounded ILSpy exercise |
| Pivot to subsystem replacement (`HeadlessSystemTenant` + `HeadlessNCLMetadata` + `IHeadlessSession` interface) | 6–8 weeks of upfront design + still requires every JMP-hook above | High — `NavSession` has no clean interface; replacement work is largely speculative |

**Recommendation: stay with reactive JMP-hooks.** The pivot's main argument — that JMP-hooks "scale poorly" — is not supported by what we now know about the surface. The total patch count looks bounded around ~16–20 patches once W-1/W-3 land. Compare to bc-linux, which runs the entire BC service tier (cluster, SQL, AAD, replication, reporting, side-services, …) with 23 patches and zero subsystem replacements past the one `IServiceTopology` proxy. AlRunner v2's surface is a strict subset.

The one exception worth keeping in mind: if Phase 2 (record-table) discovers that `NavRecordHandle.CreateTarget` chains into more than just `NCLMetadata.GetMetaTableById` — for example into `MetaObjectCache`, `NCLObjectXmlMetadataLoader`, or `INavAppClrTypeRetriever` — *then* a `HeadlessSystemTenant` skeleton with two or three populated fields might genuinely simplify things. That is a Phase 2 reassessment, not a Phase 1 prerequisite.

---

## Appendix A — fields that must be populated on skeleton objects

For reference, here is the minimum field set BcRuntime currently populates on each skeleton:

**`NavEnvironment`** (skeleton via `GetUninitializedObject`):
- `lockObject` ← new object()
- `instanceId` ← Guid.NewGuid()
- `serviceInstanceName` ← ""
- `compactLohGate`, `TerminatedSessionsMetric`, `defaultAwaitedShutdownConnectionTypesList`, `defaultRestartNotificationConnectionTypesList` ← Activator.CreateInstance defaults

**`NavSession`** (skeleton via `GetUninitializedObject`):
- *No fields currently populated.* All accesses go through JMP-hooked getters (`CurrentMethodScope`) or `?.` short-circuit (`SqlDebuggingStatistics`, `ServiceConnection`).

**`NavMethodScope.RootMethodScope`** (skeleton via `GetUninitializedObject`):
- `tree` ← real `TreeHandler` from `TreeHandler.CreateTreeRoot(skel)`
- `session` ← skeleton NavSession
- `flags` ← `MethodScopeFlags.RootScope` (1)
- `<StackDepth>k__BackingField` ← 1

This is the entire skeleton state. The proposed W-1 patch would not need to grow this set.

## Appendix B — files referenced

| Path | What it is |
|---|---|
| `AlRunner/BcRuntime.cs` (was `spike/v2/Runner/BcRuntime.cs` pre-cutover) | All current JMP-hook patches |
| `AlRunner/TestExecutor.cs` (was `spike/v2/Runner/TestExecutor.cs` pre-cutover) | Test discovery + invoke |
| `docs/archive/spike-classification.md` (was `spike/v2/CLASSIFICATION.md`) | W-1..W-5 work plan, corpus state at spike time |
| *(removed — was `spike/v2/results-after-w1.json`)* | 158 remaining failures, classified, snapshot no longer present |
| `docs/archive/spike-bc-abi-identity-findings.md` (was `spike/bc-abi-identity/FINDINGS.md`) | The 18-layer trace explaining each existing patch |
| `/home/stefan/Documents/Repos/community/bc-linux/src/StartupHook/StartupHook.cs` | Reference patch set for the full BC service tier |
| `/home/stefan/Documents/Repos/community/BusinessCentral.AL.Runner/AlRunner/Runtime/AlScope.cs` | Existing v1 stand-in for the service tier — what subsystem replacement would aim to *eliminate*, not extend |

Decompiled BC types consulted (read-only, in `/tmp/bc-decomp/`, not committed):

- `NavGlobal` (43 LOC): pure facade.
- `NavSystemTenant` (181 LOC): concrete sealed, ctor reaches Database/Apps/Symbol caches.
- `NavMethodScope` (1289 LOC): abstract base + nested RootMethodScope/TryMethodScope; 4 ctors, the 3-arg one is the failure site.
- `NavScope` / `TreeObject` / `TreeHandler`: shape of the parent-pointer chain that `RootTreeStub` already satisfies.
- `NavApplicationObjectBase` (436 LOC): ctor reaches `NavCurrentThread.ResolveAppGroup(session) → NavAppGroup.BaseGroup` (acceptable fallback).
- `NavApplicationObjectBaseHandle<T>` (77 LOC): `Target` getter calls `CreateTarget()` lazily — already JMP-hooked for codeunit case.
- `NavCodeunitHandle` (104 LOC): `CreateTarget` calls `NavGlobal.NCLMetadata.GetMetaCodeunitById(...).CreateObjectInstance(session)` — bypassed.
- `NavRecordHandle` (187 LOC): `CreateTarget` calls `NavGlobal.NCLMetadata.GetMetaTableById(...).CreateObjectInstance(...)` — Phase 2 bypass needed.
- `NCLMetadata` (2032 LOC): the lookup point; replaceable as a skeleton-with-two-JMP-hooked-methods if/when Phase 2 demands.
- `IServiceTopology` (66 LOC): pure interface; bc-linux replaces via DispatchProxy; not needed here.
- `SessionContextHelper` (139 LOC): error-path telemetry; already neutered.
- `PlatformMetadataProvider` (675 LOC): system-app metadata loader; not on test execution path.
