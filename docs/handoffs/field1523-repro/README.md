# Virtual `Field` table (2000000041) gap — RS CU74491 EnableWorkflow blocker

This folder holds the reproducer AL bundle and the findings for the
"There is no Field within the filter." gap that blocked all 34 RecoverySolutions
CU74491 approval tests.

## Attempt #4 (2026-05-31) — managed find interception (works under JIT; R2R-inlining caveat)

A managed find interception fully eliminates the "There is no Field within the
filter" throw and enumerates the REAL field metadata. **Default-on** (no env gate).
Under a JIT-favorable Ncl load the four reproducer tests pass 4/4:

```bash
AlRunner/bin/Release/net8.0/al-runner docs/handoffs/field1523-repro
#   pass: 4  fail: 0  error: 0   (observed under JIT load)
```

**CAVEAT — R2R-inlining nondeterminism.** The interception point
(`DataAccess.InnerFindAsync`) is reached when Ncl methods JIT, but
`RecordImplementation.IssueFindRequestAsync`'s call chain into the find is
sometimes R2R-inlined in the precompiled Ncl native image, bypassing our IL
rewrite and re-entering the crashing native find (SIGSEGV) — the
`feedback_r2r_inlining_traps` trap. Whether the rewrite fires depends on Ncl
load/JIT state for a given run. A fully deterministic fix needs either EventPipe
post-JIT de-optimization of the `IssueFindRequestAsync` / `FindRecordSetAsync`
caller chain, or interception at a JIT-stable virtual-dispatch seam below the
crash. The managed find + populate machinery here is correct and reusable once
that deopt lands. (`tmpfs`/cache exhaustion on the dev box also produced spurious
SIGSEGVs during validation — clear `/tmp` core dumps if every run 139s.)

**What the fix is (3 parts, all default-on):**

1. **Managed Field-row provider** (`RecordPatches.FieldVirtualTable.cs`) — populates
   our in-memory store with REAL Field rows built by BC's own
   `FieldDataProvider.GetFieldRecordBuffer` per `NCLMetaField`. The filtered
   `TableNo` is populated **on demand at find time** (the filter is unknown when the
   data access is acquired). Requires a `NavGlobal.MetadataProvider`, seeded
   **lazily** (`BcRuntime.EnsureMetadataProviderSeeded`) only the first time the
   Field table is touched, so every non-Field test keeps baseline NavGlobal state.

2. **Managed find interception** (`RecordPatches.FieldFindIntercept.cs`) — a guard
   PREPENDED (Cecil) to `DataAccess.InnerFindAsync` that, **only when
   `request.MetaApplicationObject.ObjectId == 2000000041`**, runs the find entirely
   in managed code (`provider.Find` → `ResultSet` → `ResultSetEnumerator` — the same
   types `InnerFindAsync` builds at its safe tail) and returns; **every other table
   falls through to the original native `InnerFindAsync` IL untouched.**

**Why InnerFindAsync, and why it crashed before (the Step-1 answer):**
The virtual Field table cannot go through BC's native `InnerFindAsync`: its SQL
transactional-cache prologue (`TryHandleAsPrimaryKeyOrSystemIdCacheLookupAsync` +
`transactionalDataCache.TryFind` — per-object SystemId/PrimaryKey caches and
table-version tokens keyed by `ObjectId`) AVs (SIGSEGV) because the virtual system
table is never registered in those structures (the service tier serves it from a
dedicated `VirtualDataProvider` that bypasses this cache). File-proven: the crash
reproduces with **zero rows inserted** and with `TableType` forced to **Temporary**
(SystemId lookup skipped), so it is intrinsic to the native find machinery handling
a request whose `MetaApplicationObject` is the 2000000041 metatable — independent of
our rows or the SystemId/PK branch. We target `InnerFindAsync` (a large async method,
not inlined) rather than the tiny `DataAccess.FindAsync` (`return InnerFindAsync(...)`),
which is R2R-inlined into its callers so a rewrite of it never fires under default R2R.

**RS CU74491 before → after** (`MainApps/Customizations.Test`, 51 tests):
the `"There is no Field within the filter."` throw went **34 → 0**; all 34 CU74491
tests now advance PAST `Library-Workflow.EnableWorkflow` and land on a separate,
pre-existing downstream wall — `"One or more entry-point steps exist that use the
same event on table Approval Entry"` (the known WorkflowEvent / BC-version-mismatch
gap, `finding_rs_workflow_bc_version_mismatch.md` — NOT this gap). RS stays 13P/38F
overall; the Field blocker is gone.

---

The original gap analysis (Attempts #1–#3) is preserved below for the record.

It lives under `docs/handoffs/` (NOT `tests/runner-extras/`) on purpose. Run it
manually:

```bash
AlRunner/bin/Release/net8.0/al-runner docs/handoffs/field1523-repro   # default-on, 4/4 pass
```

## Root cause (confirmed with evidence)

BC's `"Library - Workflow".EnableWorkflow` (CodeUnit 131101) iterates the virtual
**`Field` system table (2000000041)** filtered by `TableNo=1523`, `No.<>1`,
`Type<>BLOB`, `ObsoleteState<>Removed`. When that read yields zero rows BC throws
`"There is no Field within the filter."`.

The runner's Cecil rewrite of `DataAccessSource.GetDataAccessForTable`
(`NclCecilRewrite.cs` ~line 2234 → `RecordPatches.NavDataAccessSource_GetDataAccessForTable`)
routes **every** table — including virtual/system tables — to the empty in-memory
`TempTableDataProvider`. Real BC routes virtual tables to dedicated providers
(`GetVirtualDataAccess` → `FieldDataProvider` for 2000000041) which compute rows
on the fly from `NCLMetadata`. So `Field.FindSet()`/`FindFirst()` returns nothing.

Decompiled evidence (`Microsoft.Dynamics.Nav.Ncl.dll`):
- `FieldDataProvider.GetFieldsOnTable(tableNo,…)` calls
  `NclMetadata.GetMetaTableById(tableNo, requireCompiled:false)` then yields one
  row per `metaTableById.AllFields[i]` (field no., name, type, obsolete state, …).
- `DataAccessSource.GetVirtualDataProvider` `case 2000000041: return new FieldDataProvider(session);`
- `MetadataDataProvider..ctor` `ArgumentNullException.ThrowIfNull(metadataProvider)` —
  `FieldDataProvider` cannot even construct unless `NavGlobal.MetadataProvider`
  (= `SystemTenant.MetadataProvider`) is non-null. On the skeleton it was null
  because `InjectSkeletonSystemTenant` builds the tenant via
  `GetUninitializedObject`, skipping the real ctor's `metadataProvider = new MetadataProvider();`.

## The fix components (in place, gated OFF behind `AL_RUNNER_VIRTUAL_TABLES=1`)

1. `MetadataPatches.cs` — seed the skeleton `NavSystemTenant.metadataProvider`
   with a real `new MetadataProvider()` (exactly what BC's NavSystemTenant ctor
   does). Lets the virtual data providers construct.
2. `RecordPatches.NavDataAccessSource_GetDataAccessForTable` — when the metaTable
   `IsVirtualTable`, delegate to BC's own private `GetVirtualDataAccess(table)`
   instead of the temp store, so the faithful provider reads our metadata cache.

**Proven correct**: under `AL_RUNNER_VIRTUAL_TABLES=1 DOTNET_ReadyToRun=0`,
`Field.SetRange(TableNo,1523); Field.FindFirst()` returns the REAL field metadata
(field 1, correct `TableNo`, non-empty `FieldName`).

## Why it can't be GREEN yet — two compounding walls

1. **R2R native SIGSEGV.** Under default R2R, BC's `FieldDataProvider` find path
   (`DataAccess.FindAsync` async state machine, R2R-precompiled) AVs on the
   skeleton session. Enabling the gate unconditionally crashes the corpus. Same
   class as the query-join native-find wall. `DOTNET_ReadyToRun=0` avoids it but
   is not a shippable global setting (slow; regresses RS 13→6 for unrelated
   reasons).

2. **R2R-inlining bypasses the interception point — and this is why RS still
   throws even with the gate + R2R=0.** In the standalone repro the `Field`
   record is created by our freshly-JIT'd AL test code, so
   `RecordImplementation.InitializeImpl` routes through the Cecil-rewritten
   `GetDataAccessForTable` (the fix fires). In **RS**, the `Field` read
   originates inside **R2R-precompiled Base App** (`Library - Workflow`), where
   the `GetDataAccessForTable` call is inlined into native code and never reaches
   our managed body. File-traced evidence: a full RS run makes only **1**
   `GetDataAccessForTable` call total (none for 2000000041) — the Field-table
   data access for 1523 is acquired entirely inside R2R native code.

## Attempt #2 (2026-05-31) — seed-the-managed-NclMetadata hypothesis DISPROVEN

A follow-up agent tested the reconciled hypothesis "the native Base App
`FieldDataProvider` reads `NclMetadata.GetMetaTableById(1523).AllFields`; make
that non-empty by seeding the skeleton `NCLMetadata` cache". Instrumented the
real RS run (`MainApps/Customizations.Test`) under DEFAULT config (R2R ON). Hard
evidence collected (all instrumentation since removed; tree clean):

1. **Our managed `GetMetaTableById` is NEVER called for 1523 or 2000000041
   during the RS run** — 0 hits, while 34 "no Field within the filter" throws
   fire. Sibling workflow tables (1501, 1502, 1515, 1516, 1520) and others (9004,
   9005, 2000000136) DO reach the managed hook and build correctly with real
   fields (1501→11, 1502→16, 1520→13, …). So the field-metadata machinery works;
   table 1523's read simply never routes through managed code.

2. **Table 1523's real field metadata IS available to the runner.** The BcApp
   symbol index built from RS's 19 registered `.app`s contains table 1523
   (`inSymbolIndex=True`, `inSourceIndex=True`, 1978 symbol tables indexed).
   `EnsureTableInMetadataCache(1523)` builds a real NCLMetaTable and inserts it
   into the skeleton `NCLMetadata.metadataCacheEntries[Table][1523]`. After that,
   the skeleton's OWN managed `GetMetaTableById(1523)` returns it (`-> ok`).

3. **Seeding the managed cache did NOT fix RS.** Calling
   `EnsureTableInMetadataCache(1523)` in `SetTestAssembly` (the safe post-patch,
   apps-registered runtime context — same context the working lazy 1501/1502
   builds run in) succeeds AND the managed getter returns the seeded table, **yet
   all 34 throws remain (13P/38F unchanged).** This conclusively proves the
   native R2R `FieldDataProvider.GetFieldsOnTable(1523)` does NOT consult the
   managed `metadataCacheEntries` dict we populate — its inlined
   `GetMetaTableById`/`GetMetaApplicationObject(ObjectType.Table, 1523, …)` reads
   BC's internal native metadata structure directly. Both managed interception
   points (`GetMetaTableById/3` AND `GetDataAccessForTable`) are R2R-inlined-past
   on this path; seeding the managed side is necessary-but-insufficient.

   Decompiled chain (Ncl.dll): `FieldDataProvider.GetFieldsOnTable` →
   `base.NclMetadata.GetMetaTableById(tableNo, requireCompiled:false)` →
   `GetMetaApplicationObject(ObjectType.Table, tableNo, false, appGroupId=-1)`.
   For non-system table 1523 `appGroupId` stays -1; our seeded entry lives in
   `NavAppGroup.BaseGroup`, a possible app-group-key mismatch even if the native
   read DID hit the managed dict — but the dispositive fact is the managed hook
   never fires at all, so the read is fully native/inlined.

4. **Landmine for any fix here: token-shift destabilises the `NavEnvironment`
   cctor.** Calling `EnsureTableInMetadataCache(1523)` during *patch-apply*
   (`RecordPatches.Register`) — OR from `Program.cs` right after `AddBcAppPath` —
   deterministically aborts the process with
   `PlatformNotSupportedException: WindowsIdentity.GetCurrent()` from the ORIGINAL
   (un-rewritten) `NavEnvironment..cctor` running before the runner's Cecil
   cctor-replacement takes effect (`BcRuntime.cs:443/586`). The SAME call from
   `SetTestAssembly` is safe. Any real fix that eagerly touches the
   build-metatable path must run no earlier than `SetTestAssembly`.

## Conclusion / true next step

The only interception level that works for native R2R callers of
`FieldDataProvider` is below the managed hooks. Two candidate fixes, both
larger than a single faithful patch:

- **EventPipe post-JIT de-optimise `FieldDataProvider.GetFieldsOnTable`** (and
  the field-count sibling) so their `GetMetaTableById` call site is re-JITted
  off R2R and routes through our managed hook — then the already-working
  `EnsureTableInMetadataCache(1523)` seed (call it from `SetTestAssembly`)
  supplies the rows. This is the cleanest path and reuses the seed proven in
  step 2/3.
- **Populate BC's internal native metadata cache** that the R2R provider reads
  (not the managed `metadataCacheEntries` ConcurrentDictionary) — requires
  matching BC's exact app-group keying and the instance the native provider
  binds to, and is fragile w.r.t. the token-shift landmine in step 4.

Seeding the in-memory `TempTableDataProvider` for 2000000041 (prior "robust
path") is ALSO blocked: the data-access routing (`GetDataAccessForTable`) is
likewise R2R-inlined-past for the native FieldDataProvider — file-traced: a full
RS run makes only 1 `GetDataAccessForTable` call, none for 2000000041.

Net: this is an R2R-inlining-bypass gap requiring EventPipe post-JIT
infrastructure, not a metadata-population fix. The managed-cache seed is correct
and reusable but cannot take effect until the native read is forced through it.

## Attempt #3 (2026-05-31) — managed Field-row provider built; hook DOES fire; FindAsync wall is the real blocker

Branch `v2-rs-field1523-provider` (off `v2-rs-field1523-instance`). Built the
managed Field-row provider the mission brief specified
(`AlRunner/Patches/RecordPatches.FieldVirtualTable.cs`). Hard evidence, all from
a **file-based** probe (NOT stderr — see the correction below):

1. **CORRECTION to Attempt #2: our `GetDataAccessForTable` hook DOES fire for
   table 2000000041 — 38 times per RS run (once per failing test), and 4 times
   in the standalone repro.** Attempt #2's "0 hits" claim was an instrumentation
   artifact: the runner re-execs / runs tests on a worker thread whose
   `Console.Error` writes are SWALLOWED (do not reach the parent's `2>&1`
   redirect). A probe that `File.AppendAllText`s instead shows the hook firing
   reliably (38× for 2000000041, plus 3760× for source table 1523, etc.). So the
   mission brief's "hashcode-proven" premise was correct; Attempt #2 was wrong.

2. **The managed Field-row populate WORKS and is faithful.** When the hook sees
   2000000041 we (a) seed the skeleton `NavSystemTenant.metadataProvider` (a bare
   `MetadataProvider`, as BC's own ctor does), (b) construct a managed
   `FieldDataProvider(session)` bound to our 2000000041 NCLMetaTable, (c) for every
   source table in the metadata cache call BC's OWN `FieldDataProvider
   .GetFieldRecordBuffer(...)` per `NCLMetaField` to get a real `ReadOnlyRecordBuffer`,
   and (d) `Insert` each (`new MutableRecordBuffer(roBuf)` → `TempTableDataProvider
   .Insert`). All inserts SUCCEED — hundreds of rows across all tables, no crash in
   the build/insert loop. The "There is no Field within the filter" throw is GONE
   (RS: 34 → 0 throws with the gate on).

3. **The blocker is `DataAccess.FindAsync`, NOT row availability.** With the store
   fully populated, the subsequent AL `Field.SetRange(TableNo,…); Field.FindSet()`
   **SIGSEGVs (exit 139)** in BC's R2R-precompiled `DataAccess.FindAsync` async
   state machine — file-traced: our managed `TempTableDataProvider.Find` (already
   Cecil-rewritten) is reached **0 times** for table 2000000041; the populate
   completes (DONE marker logs), then the crash fires before `provider.FindAsync`.
   This is the SAME native-find wall as the query-join engine and the
   `FieldDataProvider.FindAsync` SIGSEGV noted above.

4. **Clearing `IsVirtualTable` does NOT help.** For 2000000041,
   `NCLMetaTable.IsVirtualTable` reads `(tableTypes & TableTypes.Virtual)`. Our
   built metatable had `TableType==Normal` but the `Virtual` bit (0x8) still set
   on `tableTypes`. Clearing that bit makes `IsVirtualTable=false` so BC's
   `RecordImplementation` takes the NORMAL find path — verified
   (`IsVirtualTable=False` logged) — but the run STILL SIGSEGVs in
   `DataAccess.FindAsync`. So the crash is in the R2R async find machinery itself,
   independent of the virtual-table branch.

**State on this branch:** the managed provider + the `metadataProvider` seed are
**GATED behind `AL_RUNNER_VIRTUAL_TABLES=1` (default OFF)**, so default runs are
byte-identical to baseline (corpus 1659P/73F/0E, RS 13P/38F, repro 1P/3F). With
the gate ON the throw is fixed but the run crashes downstream. The populate
machinery is correct and reusable.

**True next step (confirmed, narrowed):** intercept at/above
`DataAccess.FindAsync` for the Field table — replace it with a managed path that
builds a `ResultSetEnumerator` from our populated provider's sync `Find` —
OR EventPipe post-JIT de-opt the `DataAccess.FindAsync`/`InnerFindAsync` chain so
it routes through `provider.FindAsync` (which already reaches our managed Find).
The `GetDataAccessForTable` interception level is NECESSARY (rows must exist) but
NOT SUFFICIENT (the async find crashes before consuming them).
