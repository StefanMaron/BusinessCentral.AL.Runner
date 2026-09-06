// RecordPatches.CalcFormulaRetry — issue #3121, differential 1: a table whose FlowField
// CalcFormula could not be resolved because the SOURCE table's .app had not been registered
// yet when the table's NCLMetaTable was built.
//
// A built NCLMetaTable is one more piece of state derived from the registered BC .app set —
// exactly the family InvalidateBcAppIndexes already enumerates (#2478, #2888, #2889) — and it
// was the one nothing dropped when that set GREW. Measured: a source-bearing dependency .app
// consumed through --package-cache has its two tables built while only the System Application
// (223 tables) is registered; the Base Application .apps register afterwards, taking the index
// to 1890 tables. `CalcFormula = lookup(Customer.Name where(...))` therefore resolved nothing
// at build time (BuildMetaCalcFormula: "source table 'Customer' not found in parsed tables"),
// the field reached NCLMetaField with CalculationFormula = EmptyFormula, and CalcFields refused
// it with BC's own "You must define a CalcFormula for the {0} FlowField in the {1} table" —
// while a `count` formula over a table in the SAME package passed, because its source table was
// already parsed. The same AL passed as a source bundle, where the dependency resolution runs
// before the tables are built.
//
// The repair is BC's own construction path, not a patch of the built field: evict the affected
// table ids from _metaTableCache AND from the skeleton NCLMetadata's own cache dictionary, then
// repopulate — the mechanism TddReparseAndRefreshTable already uses for the --tdd case. Poking
// NCLMetaField.calculationFormula instead would fix only the reader that noticed: BC reads the
// same property from FlowFieldsHelper's nested-FlowField check (NCLMetaField.CalculationFormula
// .Filters) and from NCLMetaField.SourceField, and every one of those has to see the same object.
//
// Only the "source table not found" refusal is retried. A missing source FIELD or filter field
// is a shape more registrations cannot improve, so re-running the build for it would be pure
// cost.

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>Table ids whose build hit an unresolved CalcFormula source table, pending a
    /// rebuild once the registered .app set grows.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<string>>
        _tablesWithUnresolvedCalcFormulaSource = new();

    /// <summary>Guards against re-entering the rebuild from inside its own repopulate pass.</summary>
    [ThreadStatic] private static bool _retryingUnresolvedCalcFormulas;

    /// <summary>Record that <paramref name="tableId"/> was built while the CalcFormula source
    /// table <paramref name="sourceTableName"/> was unknown. Called from
    /// <c>BuildMetaCalcFormula</c>, including from inside a rebuild: a formula that is still
    /// unresolvable after one is simply pending again for the NEXT registration, and the
    /// name check below is what stops that from looping within a single one.</summary>
    internal static void NoteUnresolvedCalcFormulaSourceTable(int tableId, string sourceTableName)
    {
        if (tableId <= 0 || string.IsNullOrEmpty(sourceTableName)) return;
        var names = _tablesWithUnresolvedCalcFormulaSource.GetOrAdd(
            tableId, static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (names) names.Add(sourceTableName);
    }

    /// <summary>Drop the pending set — a reload rebuilds every table from scratch anyway, and
    /// carrying bundle 1's ids into bundle 2 only buys a wasted rebuild pass.</summary>
    internal static void ClearUnresolvedCalcFormulaTables()
        => _tablesWithUnresolvedCalcFormulaSource.Clear();

    /// <summary>Number of table ids currently awaiting a CalcFormula rebuild (test seam).</summary>
    internal static int UnresolvedCalcFormulaTableCount
        => _tablesWithUnresolvedCalcFormulaSource.Count;

    /// <summary>
    /// Rebuild every table that was built with an unresolved CalcFormula source table, so a
    /// .app registered after it can be reached. Called at the end of <see cref="AddBcAppPath"/>,
    /// outside its lock: registration is what changes the answer, and nothing else does.
    /// A no-op — not even a cache walk — while the pending set is empty, which is the state
    /// of every run that never hit the ordering.
    /// </summary>
    internal static void RetryUnresolvedCalcFormulaTables(IEnumerable<string>? newlyRegisteredTableNames = null)
    {
        if (_tablesWithUnresolvedCalcFormulaSource.IsEmpty) return;
        if (_retryingUnresolvedCalcFormulas) return;
        // Nothing to repopulate into yet: the skeleton metadata is what PopulateNclMetadataCache
        // writes to, and the earliest .app registrations run before it exists.
        if (BcRuntime.SkeletonNCLMetadata == null) return;

        _retryingUnresolvedCalcFormulas = true;
        try
        {
            // Only a table whose missing source table can NOW be materialised is worth
            // rebuilding. Without this check every registration after the first rebuilds every
            // still-unresolvable table again — measured at 5 rebuild passes for one table in a
            // run with 8 registrations, where exactly one of them changed the answer. A table
            // that stays unresolvable simply stays pending; if no .app ever declares its source
            // table, it is never rebuilt at all and BuildMetaField's line names it.
            //
            // The check reads the .app JUST registered, whose symbols the caller already has in
            // hand, rather than asking TryPopulateParsedTableByName. That helper would answer
            // the same question, but it goes through EnsureBcSymbolTableIndex — and this runs
            // immediately after InvalidateBcAppIndexes dropped that index, so probing it here
            // forces a full rebuild (1890 table ids plus the tableextension merge, on a Base
            // Application closure) at EVERY registration while anything is pending: measured at
            // 7 index builds in the package run against 2 without a retry. A name lookup over
            // the new app's own tables costs nothing and answers the only case that can have
            // changed, since a registration is the event this runs on.
            var newNames = newlyRegisteredTableNames == null
                ? null
                : new HashSet<string>(newlyRegisteredTableNames, StringComparer.OrdinalIgnoreCase);

            var ids = new List<int>();
            foreach (var (tableId, names) in _tablesWithUnresolvedCalcFormulaSource)
            {
                string[] pending;
                lock (names) pending = names.ToArray();
                var resolvable = newNames != null
                    ? pending.Any(newNames.Contains)
                    : pending.Any(n => TryPopulateParsedTableByName(n) != null);
                if (!resolvable) continue;
                ids.Add(tableId);
                _tablesWithUnresolvedCalcFormulaSource.TryRemove(tableId, out _);
            }
            if (ids.Count == 0) return;

            foreach (var id in ids)
            {
                _metaTableCache.TryRemove(id, out _);
                EvictSkeletonMetadataTableEntry(id);
            }
            Console.Error.WriteLine(
                $"[RecordPatches] CalcFormula retry: rebuilding {ids.Count} table(s) whose FlowField "
                + $"source table has now been registered ({string.Join(", ", ids)})");
            PopulateNclMetadataCache();
        }
        finally
        {
            _retryingUnresolvedCalcFormulas = false;
        }
    }

    /// <summary>
    /// Remove <paramref name="tableId"/> from the skeleton <c>NCLMetadata</c>'s own
    /// <c>metadataCacheEntries[Table]</c> dictionary, so the next
    /// <see cref="PopulateNclMetadataCache"/> pass inserts a freshly built table instead of
    /// its "already present" skip finding the stale one. Best-effort by design: a shape change
    /// in that field leaves the previous entry in place — stale metadata, but never a crash
    /// during registration.
    /// </summary>
    internal static void EvictSkeletonMetadataTableEntry(int tableId)
    {
        var skeleton = BcRuntime.SkeletonNCLMetadata;
        if (skeleton == null) return;
        EnsureCachePopulatorReflection();
        if (_fNCLMetadataCacheEntries == null) return;
        try
        {
            var arr = _fNCLMetadataCacheEntries.GetValue(skeleton) as Array;
            const int objectTypeTable = 1;
            if (arr != null && arr.Length > objectTypeTable
                && arr.GetValue(objectTypeTable) is System.Collections.IDictionary dict)
            {
                dict.Remove(tableId);
            }
        }
        catch
        {
            // Best-effort eviction — see the summary.
        }
    }
}
