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
    private sealed record BaselineTable(int TableId, object MetaTable, NavValue[][] Rows);
    private sealed record BaselineSource(object Source, IReadOnlyList<BaselineTable> Tables);

    private static IReadOnlyList<BaselineSource>? _installBaseline;
    private static object? _isolatedStorageBaseline;
    private static object? _recordLinkBaseline;
    private static IReadOnlyDictionary<int, long>? _autoIncrementBaseline;
    private static ConstructorInfo? _ibMutableBufferCtor;

    public static void CaptureInstallBaseline()
    {
        var sources = new List<BaselineSource>();
        foreach (var (source, perTable) in _dataAccessByTable)
        {
            var tables = new List<BaselineTable>();
            foreach (var (tableId, dataAccess) in perTable)
            {
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

        _installBaseline = sources;
        _isolatedStorageBaseline = TenantStoragePatches.CaptureInstallBaseline();
        _recordLinkBaseline = RecordLinkPatches.CaptureInstallBaseline();
        _autoIncrementBaseline = BcRuntime.CaptureAutoIncrementBaseline();
        PerfTrace.Log($"InstallBaseline.Capture {sources.Sum(s => s.Tables.Count)} table(s), " +
                      $"{sources.Sum(s => s.Tables.Sum(t => t.Rows.Length))} row(s)");
    }

    public static void RestoreInstallBaseline()
    {
        ResetPerTestState();
        if (_installBaseline == null)
            return;

        var restoredRows = 0;
        foreach (var source in _installBaseline)
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

        TenantStoragePatches.RestoreInstallBaseline(_isolatedStorageBaseline);
        RecordLinkPatches.RestoreInstallBaseline(_recordLinkBaseline);
        BcRuntime.RestoreAutoIncrementBaseline(_autoIncrementBaseline);
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
