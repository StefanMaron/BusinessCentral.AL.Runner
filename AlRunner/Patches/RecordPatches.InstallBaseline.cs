// RecordPatches.InstallBaseline — snapshot/restore of the committed post-installation
// state, so a test-codeunit boundary does not have to re-run every install trigger.
//
// WHY THIS EXISTS
//   Real BC rolls back each test (TestIsolation), but committed install seeding survives
//   that rollback. The runner reproduced this by wiping all in-memory state at every
//   codeunit boundary and then re-running InstallTriggerRunner.RunAll() to put the seed
//   back. Correct, but the re-seed is pure repeated work: it re-executes AL install
//   triggers whose result is identical every time. On Pageworks it dominated the run.
//
// WHAT THIS DOES
//   Install triggers run ONCE. The resulting rows are snapshotted out of the in-memory
//   TempTableDataProviders (plus isolated storage, record links and the auto-increment
//   counters, which are equally part of committed install state), and each codeunit
//   boundary restores that snapshot instead of re-running AL.
//
//   Rows are deep-copied on both capture and restore, so a test mutating a restored row
//   cannot corrupt the baseline for the next codeunit.
//
//   NOT snapshotted: the self-populating virtual tables (AllObj, Field, Table Metadata and
//   the rest of IsSelfPopulatingVirtualTableId). They are projections of the loaded-object
//   set that GetDataAccessForTableCore re-derives on every access, so a boundary restore
//   that carried them re-inserted ~22k rows it did not need, and then paid for them a second
//   time when the top-up re-attempted every insert against the fresh provider. See the
//   comment at the skip in CaptureInstallBaselineSnapshot (#2272).
//
// MEASURED (Pageworks 28.2, 1076 tests, same build, same session)
//   test run 163.0s -> 78.8s (2.07x), byte-identical outcomes: 964P/112F with the same
//   failing test set. al-language corpus failure set also byte-identical.
//
// Prototyped by Stefan on perf/install-baseline-thread; reviewed, hardened (loud failure
// on an unsnapshottable provider, per-restore instead of per-row reflection) and measured
// before landing.
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal sealed record BaselineTable(int TableId, object MetaTable, NavValue[][] Rows);

    /// <summary>One DataAccessSource's captured tables.
    ///
    /// <c>Tables</c> is a mutable <see cref="List{T}"/> rather than an IReadOnlyList, and that
    /// is load-bearing rather than sloppy: <see cref="AppendBaselineTable"/> (#2262) adds a
    /// lazily loaded --test-data table to a baseline that is already being held by other
    /// references — the per-app-group singleton AND TestExecutor's dep+company cache both
    /// hand out the same objects — so an append has to be visible through every one of them.
    /// Replacing the record would only update whichever reference did the replacing.</summary>
    internal sealed record BaselineSource(object Source, List<BaselineTable> Tables);

    /// <summary>An independent, self-contained snapshot of committed install state — the
    /// object-returning counterpart of the CaptureInstallBaseline()/RestoreInstallBaseline()
    /// singleton pair below. #1867: TestExecutor.Run uses this (not the singleton) to keep a
    /// process-lifetime cache of the dependency+company-initialize baseline keyed by
    /// dependency-assembly-set, independent of the per-app-group singleton these two hold
    /// (which is overwritten every app group once that group's OWN install triggers have
    /// also fired). Rows are deep-copied on both capture and restore, exactly like the
    /// singleton path, so two snapshots (or a snapshot and the live store) never alias.
    /// Internal, not public: BaselineSource (a private table snapshot shape) is one of its
    /// fields, and the only cross-file consumer is TestExecutor.cs in this same assembly.</summary>
    internal sealed record InstallBaselineSnapshot(
        List<BaselineSource> Sources,
        object? IsolatedStorage,
        object? RecordLinks,
        IReadOnlyDictionary<int, long>? AutoIncrement);

    private static List<BaselineSource>? _installBaseline;
    private static object? _isolatedStorageBaseline;
    private static object? _recordLinkBaseline;
    private static IReadOnlyDictionary<int, long>? _autoIncrementBaseline;
    private static ConstructorInfo? _ibMutableBufferCtor;

    /// <summary>The dep+company snapshot THIS app group was restored from (or captured
    /// into) — the object TestExecutor also holds in its process-lifetime cache, so an append
    /// here is seen by every later app group that takes a cache HIT on the same key.
    ///
    /// Registered by TestExecutor at all three branches of install-seed-dep-company-baseline.
    /// Null outside that window, and null for every run that never gets there; nothing below
    /// requires it to be set.</summary>
    private static InstallBaselineSnapshot? _activeDepCompanyBaseline;

    internal static void SetActiveDepCompanyBaseline(InstallBaselineSnapshot? snapshot)
        => _activeDepCompanyBaseline = snapshot;

    /// <summary>Test-only seam over the per-app-group baseline. A test exercising
    /// AppendBaselineTable hands it a synthetic DataAccessSource, and leaving that in a
    /// baseline some later test restores would hand _mCreateTempDataAccess an object that is
    /// not a DataAccessSource at all. Save, null, act, restore.</summary>
    internal static List<BaselineSource>? InstallBaselineForTests
    {
        get => _installBaseline;
        set => _installBaseline = value;
    }

    /// <summary>
    /// Record a table that was materialised OUTSIDE the capture window into the baselines the
    /// store is restored from, so it survives the next codeunit/test boundary instead of
    /// being wiped by ResetPerTestState and silently reloaded.
    ///
    /// This exists for #2262's lazy --test-data load. That load fires from
    /// GetDataAccessForTableCore, which is reached at any point in a run — including in the
    /// middle of a test, long after CaptureInstallBaselineSnapshot() walked the store. Rows
    /// put in the store at that moment are invisible to every snapshot, so the very next
    /// boundary would drop them.
    ///
    /// <paramref name="pristineRows"/> MUST be the rows as loaded, before any AL code could
    /// touch them; they are deep-copied here exactly like the capture path (CloneValues), so
    /// a test mutating the live row cannot reach back into a baseline.
    ///
    /// Idempotent per (source, tableId): a table already carried by a baseline is left alone
    /// rather than appended twice. That cannot happen through the lazy loader — a table a
    /// baseline carries is a table the restore put in the store, so the loader never fires
    /// for it — but duplicating install-seeded rows is a bad enough outcome to guard rather
    /// than argue about.
    /// </summary>
    internal static void AppendBaselineTable(object source, int tableId, object metaTable, NavValue[][] pristineRows)
    {
        // #2272: the capture path refuses to put a self-populating virtual table in a
        // baseline, and this is the only OTHER writer of the same lists — a table appended
        // here would survive every boundary restore exactly as if it had been captured, so
        // the two writers have to agree or the invariant is only half enforced.
        //
        // Unreachable today, and loudly so rather than silently skipped: the lazy loader that
        // calls this fires from GetDataAccessForTableCore's fall-through path, which every
        // virtual table returns before reaching. If that ever changes, re-seeding a projection
        // table from a backup is a bug worth stopping on, not one worth absorbing.
        if (IsSelfPopulatingVirtualTableId(tableId))
            throw new InvalidOperationException(
                $"install-baseline — table {tableId} is a self-populating virtual table "
                + "(see IsSelfPopulatingVirtualTableId) and must not be appended to an install "
                + "baseline; GetDataAccessForTableCore re-derives it on every access.");

        var rows = new NavValue[pristineRows.Length][];
        for (var i = 0; i < pristineRows.Length; i++)
            rows[i] = CloneValues(pristineRows[i]);

        if (_installBaseline != null)
            AppendInto(_installBaseline, source, tableId, metaTable, rows);
        // Both, deliberately (#2262): the per-app-group singleton is what a codeunit/test
        // boundary restores, and the dep+company snapshot is what the NEXT app group on this
        // dependency key is restored from before its own capture overwrites the singleton.
        // Appending to only the first would make the table's presence depend on which app
        // group happened to touch it.
        if (_activeDepCompanyBaseline != null)
            AppendInto(_activeDepCompanyBaseline.Sources, source, tableId, metaTable, rows);
    }

    private static void AppendInto(
        List<BaselineSource> sources, object source, int tableId, object metaTable, NavValue[][] rows)
    {
        BaselineSource? target = null;
        foreach (var candidate in sources)
            if (ReferenceEquals(candidate.Source, source)) { target = candidate; break; }
        if (target == null)
        {
            target = new BaselineSource(source, new List<BaselineTable>());
            sources.Add(target);
        }
        foreach (var existing in target.Tables)
            if (existing.TableId == tableId) return;   // already carried — see the doc comment
        target.Tables.Add(new BaselineTable(tableId, metaTable, rows));
    }

    public static void CaptureInstallBaseline()
    {
        var snapshot = CaptureInstallBaselineSnapshot();
        _installBaseline = snapshot.Sources;
        _isolatedStorageBaseline = snapshot.IsolatedStorage;
        _recordLinkBaseline = snapshot.RecordLinks;
        _autoIncrementBaseline = snapshot.AutoIncrement;
    }

    public static void RestoreInstallBaseline()
    {
        ResetPerTestState();
        if (_installBaseline == null)
            return;
        RestoreInstallBaselineSnapshot(new InstallBaselineSnapshot(
            _installBaseline, _isolatedStorageBaseline, _recordLinkBaseline, _autoIncrementBaseline),
            resetFirst: false);
    }

    /// <summary>Capture the current committed state as an independent snapshot object,
    /// without touching the CaptureInstallBaseline()/RestoreInstallBaseline() singleton
    /// fields above. Same capture logic as CaptureInstallBaseline(); the only difference is
    /// where the result is stored (returned, not assigned to statics), so a caller can hold
    /// several snapshots at once (e.g. one per distinct dependency-assembly set).</summary>
    internal static InstallBaselineSnapshot CaptureInstallBaselineSnapshot()
    {
        var sources = new List<BaselineSource>();
        // Diagnostic only (the PerfTrace line below). At most ten ids, so collecting them
        // unconditionally costs nothing worth gating.
        var skippedVirtual = new List<int>();
        foreach (var (source, perTable) in _dataAccessByTable)
        {
            var tables = new List<BaselineTable>();
            foreach (var (tableId, dataAccess) in perTable)
            {
                // ── Self-populating virtual tables are not install-trigger output (#2272) ──
                // AllObj, AllObjWithCaption, Field, Table/Page/Report Metadata and their
                // siblings are projections of the loaded-object set, and
                // GetDataAccessForTableCore re-populates each of them on EVERY access
                // (PopulateAllObjVirtualTable & co.) before it hands the data access back.
                // Their populated-guards are ConditionalWeakTables keyed by the in-memory
                // PROVIDER, and a boundary restore builds a brand-new provider, so a
                // restored table is re-derived from scratch on the next access either way —
                // carrying the rows across the boundary buys nothing.
                //
                // It cost, though. On a trivial fixture with the Base Application closure the
                // capture was 41 tables / 23,651 rows, of which 22,354 rows were these
                // tables; every codeunit (or, under TestIsolation.Test, every test) boundary
                // re-inserted all of them, ~95 ms each. And because the restore hands the new
                // provider an empty populated-guard, the very next access then re-attempted
                // every one of those inserts and swallowed a NavRecordAlreadyExistsException
                // per row — so the rows were paid for twice per boundary.
                //
                // This is the same filter the on-disk codec already applies on write
                // (RecordPatches.InstallBaselineDisk.cs), which is why a disk-cache HIT has
                // been running without these tables in its baseline on every warm machine
                // since that landed. NOT the cross-bundle-leak argument from that file's
                // header: in one process the parsed-object registries accumulate across
                // bundles anyway (Program.cs's run loop never resets them), so the top-up
                // already reports the union — dropping the rows here changes what it costs,
                // not what it answers.
                if (IsSelfPopulatingVirtualTableId(tableId))
                {
                    skippedVirtual.Add(tableId);
                    continue;
                }

                var provider = GetDataProvider(dataAccess);
                if (provider == null)
                    continue;

                // A data access we never handed out an in-memory provider for cannot be
                // snapshotted, and skipping it silently would drop that table's committed
                // install state at the next codeunit boundary — the previous
                // RunAll()-per-boundary approach reseeded EVERYTHING, so a quiet `continue`
                // here is a behaviour change disguised as an optimisation. Say so instead.
                if (provider.GetType().Name != "TempTableDataProvider")
                    throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        $"install-baseline snapshot (table {tableId})",
                        $"install-baseline — table {tableId} is backed by {provider.GetType().Name}, "
                        + "which the per-codeunit baseline snapshot cannot capture or restore; "
                        + "its install-seeded state would silently vanish at the next codeunit "
                        + "boundary. See docs/scope.md");

                var providerType = provider.GetType();
                var metaTable = RequiredField(providerType, "table").GetValue(provider)
                    ?? throw new InvalidOperationException("TempTableDataProvider.table is null");
                // A null primaryTree is simply "no rows were ever inserted into this table" —
                // nothing to snapshot, and the restore starts from an empty store anyway.
                var primaryTreeValue = RequiredField(providerType, "primaryTree").GetValue(provider);
                if (primaryTreeValue == null)
                    continue;
                if (primaryTreeValue is not IEnumerable primaryTree)
                    throw new InvalidOperationException("TempTableDataProvider.primaryTree is not enumerable");

                var rows = new List<NavValue[]>();
                foreach (var row in primaryTree)
                    if (row is TempTableRecordBuffer buffer)
                        rows.Add(CloneValues(buffer.ToArray()));
                tables.Add(new BaselineTable(tableId, metaTable, rows.ToArray()));
            }
            sources.Add(new BaselineSource(source, tables));
        }

        var snapshot = new InstallBaselineSnapshot(
            sources,
            TenantStoragePatches.CaptureInstallBaseline(),
            RecordLinkPatches.CaptureInstallBaseline(),
            BcRuntime.CaptureAutoIncrementBaseline());
        skippedVirtual.Sort();
        PerfTrace.Log($"InstallBaseline.Capture {sources.Sum(s => s.Tables.Count)} table(s), " +
                      $"{sources.Sum(s => s.Tables.Sum(t => t.Rows.Length))} row(s), " +
                      // #2272: named, not just counted — "which tables were left out" is the
                      // whole claim, and a bare count cannot distinguish "skipped AllObj" from
                      // "skipped something that should have been captured".
                      $"skipped-self-populating [{string.Join(",", skippedVirtual)}]" +
                      // #1867: a content digest, not just counts — lets a diagnostic run compare
                      // "the dep+company baseline this app group got via a cache HIT" against
                      // "what a fresh, uncached capture for that same app group would have
                      // produced" byte-for-byte, which is the actual claim the cache makes.
                      // Gated the same way as the rest of this line (PerfTrace.Enabled short-
                      // circuits Log(), but ComputeContentDigest itself is not free, so check
                      // explicitly rather than rely on that alone).
                      (PerfTrace.Enabled ? $" digest={ComputeContentDigest(sources)}" : ""));
        return snapshot;
    }

    /// <summary>Order-independent content digest over every captured table's rows —
    /// diagnostic only (see the PerfTrace.Log call above), never used for cache-key or
    /// correctness decisions. Table and row order already vary between an app group's own
    /// dictionary enumeration order and are not semantically meaningful, so both are sorted
    /// before hashing; only the actual (tableId, row values) content should affect the
    /// result.
    ///
    /// #1867 root-cause note: two DIFFERENT digests for the same conceptual dependency
    /// closure are EXPECTED and do not indicate drift. Two known, faithful sources of
    /// non-determinism guaranteed it:
    ///   1. System/virtual metadata tables (id >= 2,000,000,000, e.g. Field 2000000041)
    ///      are process-wide caches of loaded-assembly schema by design (see the
    ///      Field-virtual-table comment above GetDataAccessForTableCore) — they grow
    ///      monotonically as more test assemblies load into the process, independent of
    ///      install-trigger/company-init business logic. NO LONGER A SOURCE since #2272:
    ///      the self-populating ones are not captured at all, so they cannot move the
    ///      digest. The rest of the note stands.
    ///   2. Business rows carry BC-native SystemId (a GUID) and SystemCreatedAt/
    ///      SystemModifiedAt (wall-clock) fields assigned by the unmodified BC Insert path
    ///      at insert time (precompiled-dll-respect.md — we don't touch that). A fresh
    ///      re-run of the exact same AL Install trigger body legitimately gets a NEW
    ///      SystemId/timestamp every time, on real BC as much as here. Comparing digests
    ///      across two independently-fresh computations (as opposed to a cache HIT, which
    ///      reuses the same captured objects and is trivially identical) will therefore
    ///      differ even when every business-meaningful field is unchanged. Verified via a
    ///      per-table row-COUNT breakdown during the #1867 investigation: counts for real
    ///      business tables were stable across app groups; only the two known-volatile
    ///      sources above accounted for the digest churn.</summary>
    private static string ComputeContentDigest(IReadOnlyList<BaselineSource> sources)
    {
        var lines = new List<string>();
        foreach (var source in sources)
            foreach (var table in source.Tables)
                foreach (var row in table.Rows)
                    lines.Add($"{table.TableId}|{string.Join(",", row.Select(v => v?.ToString() ?? "<null>"))}");
        lines.Sort(StringComparer.Ordinal);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>Restore a previously captured snapshot object (see
    /// CaptureInstallBaselineSnapshot) into the live store. Wipes the store first
    /// (ResetPerTestState) unless <paramref name="resetFirst"/> is false — callers who just
    /// did their own equivalent reset (RestoreInstallBaseline() above) skip the duplicate.</summary>
    internal static void RestoreInstallBaselineSnapshot(InstallBaselineSnapshot snapshot, bool resetFirst = true)
    {
        if (resetFirst)
            ResetPerTestState();

        var restoredRows = 0;
        foreach (var source in snapshot.Sources)
        {
            var perTable = _dataAccessByTable.GetValue(source.Source,
                static _ => new ConcurrentDictionary<int, object>());
            foreach (var table in source.Tables)
            {
                var dataAccess = _mCreateTempDataAccess!.Invoke(source.Source, new[] { table.MetaTable })!;
                perTable[table.TableId] = dataAccess;
                var provider = GetDataProvider(dataAccess)!;
                var insert = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(m => m.Name == "Insert" && m.GetParameters().Length == 4
                             && m.GetParameters()[0].ParameterType == typeof(int));
                var insertOptions = Enum.ToObject(insert.GetParameters()[2].ParameterType, 0);

                // Resolved once per restore, not once per ROW: this loop runs at every
                // codeunit boundary over the whole install-seeded row set, and per-row
                // GetConstructor lookups spend back a slice of exactly the time this
                // baseline exists to save.
                _ibMutableBufferCtor ??= typeof(ReadOnlyRecordBuffer).Assembly
                    .GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBuffer")
                    ?.GetConstructor(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        binder: null, types: new[] { typeof(ReadOnlyRecordBuffer) }, modifiers: null)
                    ?? throw new InvalidOperationException(
                        "MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

                foreach (var values in table.Rows)
                {
                    var readOnly = new ReadOnlyRecordBuffer(
                        (NCLMetaApplicationObject)table.MetaTable, CloneValues(values));
                    var mutable = _ibMutableBufferCtor.Invoke(new object[] { readOnly });
                    insert.Invoke(provider, new object?[] { 0, mutable, insertOptions, null });
                    restoredRows++;
                }
            }
        }

        TenantStoragePatches.RestoreInstallBaseline(snapshot.IsolatedStorage);
        RecordLinkPatches.RestoreInstallBaseline(snapshot.RecordLinks);
        BcRuntime.RestoreAutoIncrementBaseline(snapshot.AutoIncrement);
        PerfTrace.Log($"InstallBaseline.Restore {restoredRows} row(s)");
    }

    private static object? GetDataProvider(object dataAccess) => dataAccess.GetType()
        .GetProperty("DataProvider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?.GetValue(dataAccess);

    private static FieldInfo RequiredField(Type type, string name) => type
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);

    private static NavValue[] CloneValues(NavValue[] values)
    {
        var clone = (NavValue[])values.Clone();
        for (var i = 0; i < clone.Length; i++)
            if (clone[i] is NavBLOB blob)
            {
                var deepCopy = blob.GetType().GetMethod("DeepCopy",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null)
                    ?? throw new MissingMethodException(blob.GetType().FullName, "DeepCopy()");
                clone[i] = (NavValue)deepCopy.Invoke(blob, null)!;
            }
        return clone;
    }
}
