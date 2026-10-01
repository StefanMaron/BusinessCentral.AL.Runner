// RecordPatches.QueryJoinWhereFilters — the WHERE half of a multi-dataitem JOIN query's
// filters, handed to the isolated executor so it can apply them BEFORE grouping (#5145).
//
// Kept out of RecordPatches.QueryJoin.cs, whose shim IL references no Ncl type (see that
// file's header); this file is Ncl-typed like RecordPatches.QueryProjection.cs.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The query's WHERE conditions as (query column, predicate) pairs: every runtime
    /// SetRange/SetFilter, and every static ColumnFilter on a column no runtime filter
    /// replaces, whose column is neither aggregated (those are HAVING) nor backed by a
    /// FlowFilter field (#2925: those parameterise FlowFields, not rows).
    ///
    /// Observably equivalent to BC: each predicate is BC's own
    /// <c>FilterExpression.Evaluate</c> over the same NavValue the post-projection pass,
    /// <see cref="ApplyJoinRuntimeFilters"/>, evaluates, so it keeps exactly the rows that pass
    /// keeps; only its position moves, to before grouping, where BC's SQL puts a WHERE clause
    /// (corpus codeunit 68600, corpus PR #524). Selection mirrors that pass's own, so a change
    /// to which filters it applies belongs in both.
    /// </summary>
    private static List<KeyValuePair<object, Func<object?, bool>>> BuildJoinWhereFilters(
        object nclMetaQuery, object request)
    {
        const string surface = "AL query execution (multi-dataitem join WHERE filters)";
        EnsureFilterReflection();
        var tFfd = _tFilterFieldDictionary
            ?? throw new BcShapeGapException(surface, "Microsoft.Dynamics.Nav.Runtime.FilterFieldDictionary",
                "type not found — the runner reads a join query's runtime filters from it");
        var tFam = _tFiltersAndMarks
            ?? throw new BcShapeGapException(surface, "Microsoft.Dynamics.Nav.Runtime.FiltersAndMarks",
                "type not found — the runner reads a join query's runtime filters from it");

        var tQuery = _tNCLMetaQuery
            ?? throw new BcShapeGapException(surface, "Microsoft.Dynamics.Nav.Runtime.NCLMetaQuery",
                "type not found — the runner reads a join query's static ColumnFilters from it");

        var fam = BcShape.Property(request.GetType(), "FiltersAndMarks", BindingFlags.Public | BindingFlags.Instance, surface)
            .GetValue(request);
        var filters = fam == null ? null
            : BcShape.Property(tFam, "Filters", BindingFlags.Public | BindingFlags.Instance, surface).GetValue(fam);
        var items = filters == null ? null
            : (Array?)BcShape.Property(tFfd, "Items", BcShape.AnyInstance, surface).GetValue(filters);

        var conds = new List<(NCLMetaQueryColumn Column, object Expr)>();
        var runtimeFilteredColumnIds = new HashSet<int>();
        if (items != null)
            foreach (var item in items)
            {
                // Tuple<INavFieldMetadata, FilterExpression>. A key that is not a query column
                // is refused by ApplyJoinRuntimeFilters; nothing to add here.
                if (item is not ITuple t || t[0] is not NCLMetaQueryColumn col) continue;
                runtimeFilteredColumnIds.Add(col.Id);
                if (t[1] is { } expr) conds.Add((col, expr));
            }
        foreach (var (col, expr) in GetStaticColumnFilters(nclMetaQuery, tQuery))
            if (!runtimeFilteredColumnIds.Contains(col.Id))
                conds.Add((col, expr));

        var result = new List<KeyValuePair<object, Func<object?, bool>>>();
        object? session = null;
        bool sessionRead = false;
        foreach (var (col, expr) in conds)
        {
            if (col.AggregationType != AggregationType.None) continue;
            if (IsFlowFilterColumn(col)) continue;
            result.Add(new KeyValuePair<object, Func<object?, bool>>(col, value =>
            {
                if (!sessionRead) { session = TryGetCurrentSession(nclMetaQuery); sessionRead = true; }
                // Through the same NavValue[] the projected row is built from, so the value
                // evaluated is the one the post-projection pass would read.
                var navValue = ToNavValueArray(new[] { value }).GetValue(0)
                    ?? throw RunnerShapeGap.Query(
                        "NavQuery (multi-dataitem join)",
                        "query-join-where-filter-no-value",
                        $"filtered column '{col.Name}' has no value on a joined row, so its filter "
                        + "cannot be evaluated before grouping");
                return EvaluateFilterExpression(expr, navValue, session);
            }));
        }
        return result;
    }
}
