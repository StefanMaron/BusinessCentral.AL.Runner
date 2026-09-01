// RecordPatches.StoredTableCensus — read-only "how many rows does this table hold right now"
// queries against the live in-memory store (issue #2240, diagnostic half).
//
// WHY IT IS A SEPARATE FILE AND NOT PART OF InstallBaseline
//   InstallBaseline walks the same structures, but everything it does MUTATES: it captures a
//   snapshot, restores one, wipes the store. Nothing here writes. Keeping the read-only
//   queries apart means a diagnostic that runs at failure time can never be the reason a
//   failing run's state changed — which matters because the one thing the #2240 diagnostic may
//   not do is alter the failure it is explaining.
//
// WHY IT IS ALLOWED TO RETURN "I DON'T KNOW"
//   Every entry point returns false rather than throwing or guessing. A census that cannot see
//   the table (no DataAccessSource has materialised it, the provider is not a
//   TempTableDataProvider, BC's private field layout moved) must NOT be reported as "the table
//   is empty" — that is precisely the false positive .claude/rules/loud-failures.md's sister
//   concern in #2240 warns about, where a developer is told their genuine bug is a missing-data
//   problem. Absent evidence, the caller says nothing.
using System.Collections;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>What the store knows about one table right now.</summary>
    /// <param name="TableId">AL table id.</param>
    /// <param name="TableName">The AL object name BC's own NCLMetaTable carries.</param>
    /// <param name="Rows">Total rows across every DataAccessSource that materialised it.</param>
    internal sealed record StoredTableCensus(int TableId, string TableName, int Rows);

    /// <summary>
    /// Census by AL table id. False when no materialised DataAccessSource holds the table, or
    /// when the provider behind it is not the in-memory one this census can read — see the file
    /// header for why that is "unknown", never "empty".
    /// </summary>
    internal static bool TryCensusTable(int tableId, out StoredTableCensus census)
        => TryCensus(m => m.TableId == tableId, out census);

    /// <summary>
    /// Census by AL table NAME (BC's NCLMetaTable.TableName, which is what
    /// NavTestFieldException.TableName carries). Ordinal first, then ordinal-ignore-case.
    ///
    /// AMBIGUITY IS "UNKNOWN", NOT A COIN FLIP: if two distinct table ids in the store answer to
    /// the same name, this returns false. Picking one would mean reporting a row count that
    /// might belong to the other table.
    /// </summary>
    internal static bool TryCensusTableByName(string tableName, out StoredTableCensus census)
    {
        census = default!;
        if (string.IsNullOrWhiteSpace(tableName)) return false;
        if (TryCensusUnique(m => string.Equals(m, tableName, StringComparison.Ordinal), out census))
            return true;
        return TryCensusUnique(m => string.Equals(m, tableName, StringComparison.OrdinalIgnoreCase), out census);
    }

    private static bool TryCensusUnique(Func<string, bool> nameMatches, out StoredTableCensus census)
    {
        census = default!;
        var hits = new Dictionary<int, StoredTableCensus>();
        CollectCensus(meta => nameMatches(MetaTableName(meta)), hits);
        if (hits.Count != 1) return false;
        census = hits.Values.Single();
        return true;
    }

    private static bool TryCensus(Func<NCLMetaTable, bool> predicate, out StoredTableCensus census)
    {
        census = default!;
        var hits = new Dictionary<int, StoredTableCensus>();
        CollectCensus(predicate, hits);
        if (hits.Count != 1) return false;
        census = hits.Values.Single();
        return true;
    }

    /// <summary>
    /// Walk every (DataAccessSource, tableId) pair and total the rows of those whose metatable
    /// satisfies <paramref name="predicate"/>. Rows are SUMMED across sources rather than taken
    /// from the first hit: the same AL table can be materialised on more than one
    /// DataAccessSource in a run, and "empty" has to mean empty everywhere before anyone claims
    /// the data is missing.
    /// </summary>
    private static void CollectCensus(Func<NCLMetaTable, bool> predicate,
                                      Dictionary<int, StoredTableCensus> into)
    {
        foreach (var (_, perTable) in _dataAccessByTable)
        {
            foreach (var (tableId, dataAccess) in perTable)
            {
                object? provider;
                object? metaTableObj;
                object? primaryTree;
                try
                {
                    provider = GetDataProvider(dataAccess);
                    if (provider == null || provider.GetType().Name != "TempTableDataProvider") continue;
                    var providerType = provider.GetType();
                    metaTableObj = RequiredField(providerType, "table").GetValue(provider);
                    primaryTree = RequiredField(providerType, "primaryTree").GetValue(provider);
                }
                catch (MissingFieldException) { continue; }   // BC's private layout moved — unknown, not empty
                catch (TargetInvocationException) { continue; }

                if (metaTableObj is not NCLMetaTable meta) continue;
                if (!predicate(meta)) continue;

                // A null primaryTree is BC's representation of "no row was ever inserted" — see
                // the same read in CaptureInstallBaselineSnapshot.
                var rows = 0;
                if (primaryTree is IEnumerable tree)
                    foreach (var _ in tree) rows++;

                into[tableId] = into.TryGetValue(tableId, out var prior)
                    ? prior with { Rows = prior.Rows + rows }
                    : new StoredTableCensus(tableId, MetaTableName(meta), rows);
            }
        }
    }

    private static string MetaTableName(NCLMetaTable meta)
    {
        try { return meta.TableName ?? ""; }
        catch { return ""; }
    }
}
