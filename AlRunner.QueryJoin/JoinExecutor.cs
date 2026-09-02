// JoinExecutor — in-memory multi-dataitem query joins (isolated assembly).
//
// Ported verbatim (logic-for-logic) from al-runner's RecordPatches.QueryJoin.cs, with
// two deliberate changes:
//   1. ALL Microsoft.Dynamics.Nav.* types are accessed by REFLECTION over `object` —
//      there is not a single direct Ncl type reference, so this assembly contributes no
//      Ncl-referencing IL to anything. al-runner helpers it used to call statically are
//      now invoked through the JoinContext delegate boundary. (See JoinContext / csproj
//      for the R2R-isolation rationale.)
//   2. LeftOuterJoin unmatched-child columns are filled with the child field's TYPED
//      DEFAULT NavValue (ctx.TypedDefaultForField) instead of leaving the slot null —
//      fixing the NRE in NavQuery.GetColumnValue on a null child column.
//
// FAITHFULNESS (loud-failures): any join sub-case we cannot reproduce in-memory throws
// the project's RunnerOutOfScopeException (via ctx.OutOfScope) with a SPECIFIC reason —
// never a silent default. Supported: InnerJoin / LeftOuterJoin over field=field equi-links
// on stored (non-FlowField) fields. Unsupported (RightOuter/Full/Cross/Apply, Const/
// Expression links, FlowField links, missing link) → named OOS throw.
namespace AlRunner.QueryJoin;

using System.Collections;
using System.Reflection;

public static class JoinExecutor
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Reflection handles over Microsoft.Dynamics.Nav.Runtime.* (resolved once, lazily,
    // from whatever assembly the live queryDefinition object came from).
    private static PropertyInfo? _pQueryDefDataItems;
    private static PropertyInfo? _pQueryDefOrderBy;
    private static PropertyInfo? _pDataItemMetaTable;
    // #2300: NCLMetaQueryDataItem.SubQueryDefinition — non-null for a synthesized dataitem
    // BC's own NCLMetaQuery.CreateSubQueryForFlowFieldCalculation builds when a query column
    // is a FlowField. Such a dataitem has no `tableNo` of its own (it wraps an OuterApply
    // sub-query instead), so calling its `.MetaTable` getter NREs — exactly what BC's own
    // NCLMetaQueryDefinition.GetAllDataItems avoids by testing this same property and NOT
    // recursing/yielding a dataitem that has one. See GetRealDataItems below.
    private static PropertyInfo? _pDataItemSubQueryDefinition;
    private static PropertyInfo? _pDataItemLinks;
    private static PropertyInfo? _pDataItemLinkType;
    private static PropertyInfo? _pDataItemName;
    private static PropertyInfo? _pDataItemQueryColumns;
    private static PropertyInfo? _pLinkLinkType;
    private static PropertyInfo? _pLinkSourceColumn;
    private static PropertyInfo? _pLinkDestinationColumn;
    private static PropertyInfo? _pLinkSourceDataItemName;
    private static PropertyInfo? _pColSourceTableField;
    private static PropertyInfo? _pColColumnIndex;
    private static PropertyInfo? _pColColumnType;
    private static PropertyInfo? _pColAggregationType;
    private static PropertyInfo? _pColParentDataItem;
    private static PropertyInfo? _pFieldColumnIndex;
    private static PropertyInfo? _pFieldFieldClass;
    private static PropertyInfo? _pOrderByColumn;
    private static PropertyInfo? _pOrderBySorting;
    private static PropertyInfo? _pMetaQueryQueryDefinition;
    private static bool _ready;

    private static void EnsureReflection(object queryDefinition)
    {
        if (_ready) return;
        var asm = queryDefinition.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tQueryDef = asm.GetType(rt + "NCLMetaQueryDefinition")!;
        var tDataItem = asm.GetType(rt + "NCLMetaQueryDataItem")!;
        var tLink = asm.GetType(rt + "NCLMetaDataItemLink")!;
        var tCol = asm.GetType(rt + "NCLMetaQueryColumn")!;
        var tField = asm.GetType(rt + "NCLMetaField")!;
        var tOrderBy = asm.GetType(rt + "NCLMetaQueryOrderBy")!;
        var tMetaQuery = asm.GetType(rt + "NCLMetaQuery")!;

        _pQueryDefDataItems = tQueryDef.GetProperty("DataItems", F)!;
        _pQueryDefOrderBy = tQueryDef.GetProperty("OrderBy", F)!;
        _pDataItemMetaTable = tDataItem.GetProperty("MetaTable", F)!;
        _pDataItemSubQueryDefinition = tDataItem.GetProperty("SubQueryDefinition", F)!;
        _pDataItemLinks = tDataItem.GetProperty("DataItemLinks", F)!;
        _pDataItemLinkType = tDataItem.GetProperty("DataItemLinkType", F)!;
        _pDataItemName = tDataItem.GetProperty("Name", F)!;
        _pDataItemQueryColumns = tDataItem.GetProperty("QueryColumns", F)!;
        _pLinkLinkType = tLink.GetProperty("LinkType", F)!;
        _pLinkSourceColumn = tLink.GetProperty("SourceColumn", F)!;
        _pLinkDestinationColumn = tLink.GetProperty("DestinationColumn", F)!;
        _pLinkSourceDataItemName = tLink.GetProperty("SourceDataItemName", F)!;
        _pColSourceTableField = tCol.GetProperty("SourceTableField", F)!;
        _pColColumnIndex = tCol.GetProperty("ColumnIndex", F)!;
        // NCLMetaQueryColumn.ColumnType (QueryColumnType enum: Normal/FilterOnly/ConstValue) —
        // NOT the design-time MetaQueryColumn.ColumnType (a NavType). ColumnIndex alone cannot
        // tell a filter-only column from a genuinely-projected column at slot 0: the runtime
        // ctor only assigns ColumnIndex when ColumnType != FilterOnly, so a filter-only column's
        // ColumnIndex is left at its CLR default (0) — indistinguishable from a real slot-0
        // column by value alone. See BuildJoinProjectionPlan below.
        _pColColumnType = tCol.GetProperty("ColumnType", F);
        // NCLMetaQueryColumn.AggregationType (Microsoft.Dynamics.Nav.Types.AggregationType:
        // None/Sum/Count/Average/Min/Max) — issue #2146. Read by .ToString() rather than a
        // typed enum since this assembly touches no Ncl type directly.
        _pColAggregationType = tCol.GetProperty("AggregationType", F);
        _pColParentDataItem = tCol.GetProperty("ParentDataItem", F)!;
        _pFieldColumnIndex = tField.GetProperty("ColumnIndex", F)!;
        _pFieldFieldClass = tField.GetProperty("FieldClass", F);
        _pOrderByColumn = tOrderBy.GetProperty("Column", F) ?? tOrderBy.GetProperty("QueryColumn", F);
        _pOrderBySorting = tOrderBy.GetProperty("Sorting", F) ?? tOrderBy.GetProperty("SortOrder", F);
        _pMetaQueryQueryDefinition = tMetaQuery.GetProperty("QueryDefinition", BindingFlags.Public | BindingFlags.Instance)!;
        _ready = true;
    }

    /// <summary>
    /// The query's REAL (table-backed) dataitems — excludes any FlowField-calculation
    /// synthesized dataitem BC's own NCLMetaQuery.CreateSubQueryForFlowFieldCalculation
    /// added (SubQueryDefinition != null; see field comment above). Those columns are
    /// computed separately (RecordPatches.QueryProjection.cs's FlowFieldPatches.
    /// CalcOneFlowFieldForQueryRow), not by joining over their sub-query's own dataitem —
    /// mirrors BC's own NCLMetaQueryDefinition.GetAllDataItems test, just without the
    /// recursion into the sub-query's DataItems (this runner never executes that sub-query).
    /// </summary>
    private static List<object> GetRealDataItems(object queryDefinition)
    {
        EnsureReflection(queryDefinition);
        var raw = ((IEnumerable)_pQueryDefDataItems!.GetValue(queryDefinition)!).Cast<object>();
        var result = new List<object>();
        foreach (var di in raw)
        {
            if (_pDataItemSubQueryDefinition!.GetValue(di) != null) continue;
            result.Add(di);
        }
        return result;
    }

    /// <summary>True iff this query definition has more than one (flat, real) dataitem — i.e. a join.</summary>
    public static bool IsMultiDataItem(object queryDefinition)
        => GetRealDataItems(queryDefinition).Count > 1;

    private static void Log(JoinContext ctx, string m) => ctx.Log(m);

    // ── per-dataitem row buffer + buffer accessor over the (object) ReadOnlyRecordBuffer ──
    private sealed class DataItemRows
    {
        public object DataItem = null!;
        public string Name = "";
        public object Table = null!;
        public List<object> Rows = new(); // each: ReadOnlyRecordBuffer (object)
    }

    // ReadOnlyRecordBuffer exposes int FieldCount and an indexer this[int] returning NavValue.
    private static PropertyInfo? _pBufFieldCount;
    private static PropertyInfo? _pBufItem;
    private static void EnsureBufferAccess(object buffer)
    {
        if (_pBufFieldCount != null) return;
        var t = buffer.GetType();
        _pBufFieldCount = t.GetProperty("FieldCount", BindingFlags.Public | BindingFlags.Instance)!;
        // The default indexer (Item) taking a single int.
        _pBufItem = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 1
                && p.GetIndexParameters()[0].ParameterType == typeof(int))
            ?? t.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
    }
    private static int BufFieldCount(object buffer)
    {
        EnsureBufferAccess(buffer);
        return (int)_pBufFieldCount!.GetValue(buffer)!;
    }
    private static object? BufGet(object buffer, int slot)
    {
        EnsureBufferAccess(buffer);
        return _pBufItem!.GetValue(buffer, new object[] { slot });
    }

    /// <summary>
    /// Execute the join described by <paramref name="nclMetaQuery"/> over the in-memory tables
    /// reachable from <paramref name="dataAccessSource"/> and return the query-projected rows
    /// (ReadOnlyRecordBuffers as objects). Eagerly materialised so any failure surfaces as a
    /// managed exception at the call site, never a native crash mid-enumeration.
    /// </summary>
    public static List<object> Execute(JoinContext ctx, object nclMetaQuery, object dataAccessSource)
    {
        Log(ctx, "ExecuteJoinQuery start");
        EnsureReflection(nclMetaQuery);
        var queryDef = _pMetaQueryQueryDefinition!.GetValue(nclMetaQuery)!;
        EnsureReflection(queryDef);
        Log(ctx, "reflection ready");

        // #2300: excludes a FlowField-calculation synthesized dataitem, same as IsMultiDataItem
        // above — a real multi-dataitem join that ALSO selects a FlowField column would
        // otherwise crash here calling .MetaTable on the synthesized dataitem. That combination
        // isn't wired up to compute the FlowField column's value here yet (tracked as a
        // follow-up; the single-real-dataitem FlowField case is handled entirely before this
        // method is ever reached, via IsMultiDataItem now reporting "not multi").
        var dataItems = GetRealDataItems(queryDef);

        // 1. Read every dataitem's rows (honouring its own table filters). Validate the join
        //    shape up-front so an unsupported case throws BEFORE any partial output.
        var perItem = new List<DataItemRows>();
        foreach (var di in dataItems)
        {
            var table = _pDataItemMetaTable!.GetValue(di)!;
            var name = (string)_pDataItemName!.GetValue(di)!;
            var rows = ReadDataItemRows(ctx, dataAccessSource, di, table);
            Log(ctx, $"  read {rows.Count} rows for dataitem {name}");
            perItem.Add(new DataItemRows { DataItem = di, Name = name, Table = table, Rows = rows });
        }

        // 2. Nested-loop join in dataitem order. A "combo" is a map dataItemName → buffer
        //    (buffer may be null for a left-outer miss).
        var combos = new List<Dictionary<string, object?>>();
        bool first = true;
        foreach (var item in perItem)
        {
            if (first)
            {
                foreach (var r in item.Rows)
                    combos.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { [item.Name] = r });
                first = false;
                continue;
            }

            var joinKind = JoinKindOf(ctx, item.DataItem); // throws OOS for unsupported types
            var links = BuildLinkPlan(ctx, item.DataItem);  // throws OOS for non-field/flowfield links
            var next = new List<Dictionary<string, object?>>();
            foreach (var combo in combos)
            {
                bool matched = false;
                foreach (var childRow in item.Rows)
                {
                    if (LinksHold(links, combo, childRow))
                    {
                        var c = new Dictionary<string, object?>(combo, StringComparer.Ordinal) { [item.Name] = childRow };
                        next.Add(c);
                        matched = true;
                    }
                }
                if (!matched && joinKind == JoinKind.LeftOuter)
                {
                    var c = new Dictionary<string, object?>(combo, StringComparer.Ordinal) { [item.Name] = null };
                    next.Add(c);
                }
                // InnerJoin with no match → parent combo dropped (do nothing).
            }
            combos = next;
        }

        // 3. Project each combo into the query result slots.
        var plan = BuildJoinProjectionPlan(ctx, queryDef);
        bool hasAggregate = plan.Columns.Any(c => c.Aggregation != "None");
        List<object?[]> projected;
        if (!hasAggregate)
        {
            projected = new List<object?[]>(combos.Count);
            foreach (var combo in combos)
            {
                var fields = new object?[plan.SlotCount];
                foreach (var col in plan.Columns)
                {
                    if (col.TableSlot < 0) continue; // unsupported column → default
                    if (!combo.TryGetValue(col.OwnerName, out var buf) || buf == null)
                    {
                        // LeftOuterJoin unmatched child → fill the slot with the child field's
                        // TYPED default NavValue (BC's SQL NULL projects to the column's typed
                        // default), not a null slot. A null slot NREs NavQuery.GetColumnValue.
                        if (col.SourceField != null)
                            fields[col.QuerySlot] = ctx.TypedDefaultForField(col.SourceField);
                        continue;
                    }
                    if (col.TableSlot < BufFieldCount(buf))
                        fields[col.QuerySlot] = BufGet(buf, col.TableSlot);
                }
                projected.Add(fields);
            }
        }
        else
        {
            // Issue #2146: at least one aggregated (Method = Sum/Count/Average/Min/Max) column
            // across the join's dataitems → an implicit GROUP BY over every OTHER Normal column,
            // computed over the JOINED rows — mirrors RecordPatches.QueryProjection.cs's
            // single-dataitem GROUP BY (#2137), just fed by `combos` (this executor's own join
            // output) instead of a plain table scan.
            projected = BuildGroupedRows(ctx, plan, combos);
        }

        // 4. OrderBy over the projected result slots (top-level ordering).
        ApplyJoinOrderBy(projected, queryDef);

        var result = new List<object>(projected.Count);
        foreach (var fields in projected)
            result.Add(ctx.MakeReadOnlyRecordBuffer(nclMetaQuery, ctx.ToNavValueArray(fields)));
        return result;
    }

    private enum JoinKind { Inner, LeftOuter }

    private static JoinKind JoinKindOf(JoinContext ctx, object dataItem)
    {
        var lt = _pDataItemLinkType!.GetValue(dataItem)!.ToString();
        return lt switch
        {
            "InnerJoin" => JoinKind.Inner,
            "LeftOuterJoin" => JoinKind.LeftOuter,
            _ => throw ctx.OutOfScope(
                "NavQuery (multi-dataitem join)",
                $"query-join-{lt?.ToLowerInvariant() ?? "unknown"}-not-implemented — only InnerJoin and " +
                "LeftOuterJoin are supported in-memory; see docs/scope.md")
        };
    }

    private readonly struct LinkCond
    {
        public readonly string ParentName;
        public readonly int ParentSlot;
        public readonly int ChildSlot;
        public LinkCond(string parentName, int parentSlot, int childSlot)
        { ParentName = parentName; ParentSlot = parentSlot; ChildSlot = childSlot; }
    }

    private static List<LinkCond> BuildLinkPlan(JoinContext ctx, object dataItem)
    {
        var links = ((IEnumerable?)_pDataItemLinks!.GetValue(dataItem))?.Cast<object>().ToList()
            ?? new List<object>();
        if (links.Count == 0)
            throw ctx.OutOfScope(
                "NavQuery (multi-dataitem join)",
                "query-join-no-link — a non-root dataitem has no DataItemLink; only equi-linked " +
                "joins are supported in-memory; see docs/scope.md");

        var plan = new List<LinkCond>(links.Count);
        foreach (var link in links)
        {
            var linkType = _pLinkLinkType!.GetValue(link)!.ToString();
            if (linkType != "Field")
                throw ctx.OutOfScope(
                    "NavQuery (multi-dataitem join)",
                    $"query-join-nonfield-link — DataItemLink of type '{linkType}' (Const/Expression) " +
                    "is not supported in-memory; only field=field equi-links are; see docs/scope.md");

            var srcCol = _pLinkSourceColumn!.GetValue(link)!;       // parent column
            var dstCol = _pLinkDestinationColumn!.GetValue(link)!;  // child column
            var srcField = _pColSourceTableField!.GetValue(srcCol);
            var dstField = _pColSourceTableField!.GetValue(dstCol);
            if (srcField == null || dstField == null)
                throw ctx.OutOfScope(
                    "NavQuery (multi-dataitem join)",
                    "query-join-link-no-source-field — a DataItemLink column has no backing table " +
                    "field; see docs/scope.md");
            EnsureNotFlowField(ctx, srcField);
            EnsureNotFlowField(ctx, dstField);

            var parentName = (string)_pLinkSourceDataItemName!.GetValue(link)!;
            int parentSlot = (int)_pFieldColumnIndex!.GetValue(srcField)!;
            int childSlot = (int)_pFieldColumnIndex!.GetValue(dstField)!;
            plan.Add(new LinkCond(parentName, parentSlot, childSlot));
        }
        return plan;
    }

    private static void EnsureNotFlowField(JoinContext ctx, object field)
    {
        if (_pFieldFieldClass == null) return;
        var fc = _pFieldFieldClass.GetValue(field)?.ToString();
        if (fc == "FlowField")
            throw ctx.OutOfScope(
                "NavQuery (multi-dataitem join)",
                "query-join-flowfield-link — a DataItemLink references a FlowField; in-memory join " +
                "only supports links on stored (non-FlowField) fields; see docs/scope.md");
    }

    private static bool LinksHold(List<LinkCond> links, Dictionary<string, object?> combo, object childRow)
    {
        foreach (var lc in links)
        {
            if (!combo.TryGetValue(lc.ParentName, out var parentBuf) || parentBuf == null)
                return false; // parent missing (left-outer null) → equi-link cannot hold
            if (lc.ParentSlot >= BufFieldCount(parentBuf) || lc.ChildSlot >= BufFieldCount(childRow))
                return false;
            var pv = BufGet(parentBuf, lc.ParentSlot);
            var cv = BufGet(childRow, lc.ChildSlot);
            if (!NavValuesEqual(pv, cv)) return false;
        }
        return true;
    }

    private static bool NavValuesEqual(object? a, object? b)
    {
        if (a == null || b == null) return ReferenceEquals(a, b);
        // NavValue implements IEquatable<NavValue>/Equals — same semantics BC's join uses
        // to compare the linked column values.
        return a.Equals(b);
    }

    // ── GROUP BY over joined rows (issue #2146) ─────────────────────────────────────
    //
    // The implicit GROUP BY key: the group-key columns' resolved combo values, compared with
    // the same NavValue equality NavValuesEqual/the join's own link-matching already trusts —
    // not a re-derived one. Mirrors RecordPatches.QueryProjection.cs's single-dataitem GroupKey.
    private readonly struct JoinGroupKey : IEquatable<JoinGroupKey>
    {
        private readonly object?[] _values;
        public JoinGroupKey(object?[] values) => _values = values;

        public bool Equals(JoinGroupKey other)
        {
            if (_values.Length != other._values.Length) return false;
            for (int i = 0; i < _values.Length; i++)
                if (!NavValuesEqual(_values[i], other._values[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is JoinGroupKey k && Equals(k);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var v in _values) hash.Add(v?.GetHashCode() ?? 0);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A non-aggregated column's value for one combo (row) — the same value it would get in
    /// the ungrouped per-combo projection above: the owning dataitem's buffer value at the
    /// column's TableSlot, or the child field's typed default for an unmatched LeftOuterJoin
    /// combo, or null if unsupported (ConstValue/no source field).
    /// </summary>
    private static object? ResolveComboValue(JoinContext ctx, JoinColumn col, Dictionary<string, object?> combo)
    {
        if (!combo.TryGetValue(col.OwnerName, out var buf) || buf == null)
            return col.SourceField != null ? ctx.TypedDefaultForField(col.SourceField) : null;
        if (col.TableSlot < 0 || col.TableSlot >= BufFieldCount(buf)) return null;
        return BufGet(buf, col.TableSlot);
    }

    /// <summary>
    /// Group <paramref name="combos"/> (the joined, pre-projection rows) by their GROUP BY
    /// key — every non-aggregated column in <paramref name="plan"/> — and build one output
    /// row per group. A query with NO non-aggregated column at all is BC's scalar-aggregate
    /// case (SQL's "GROUP BY ()"): exactly one output row always, even over zero joined combos.
    /// </summary>
    private static List<object?[]> BuildGroupedRows(JoinContext ctx, JoinProjectionPlan plan, List<Dictionary<string, object?>> combos)
    {
        var groupKeyCols = plan.Columns.Where(c => c.Aggregation == "None").ToList();
        if (groupKeyCols.Count == 0)
            return new List<object?[]> { BuildAggregateRow(ctx, plan, combos) };

        var groups = new Dictionary<JoinGroupKey, List<Dictionary<string, object?>>>();
        var order = new List<JoinGroupKey>();
        foreach (var combo in combos)
        {
            var keyValues = new object?[groupKeyCols.Count];
            for (int i = 0; i < groupKeyCols.Count; i++)
                keyValues[i] = ResolveComboValue(ctx, groupKeyCols[i], combo);
            var key = new JoinGroupKey(keyValues);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<Dictionary<string, object?>>();
                groups[key] = list;
                order.Add(key);
            }
            list.Add(combo);
        }

        var result = new List<object?[]>(order.Count);
        foreach (var key in order)
            result.Add(BuildAggregateRow(ctx, plan, groups[key]));
        return result;
    }

    /// <summary>
    /// Project one output row from <paramref name="groupCombos"/> — either every combo sharing
    /// one GROUP BY key, or (the scalar-aggregate case) every joined combo. Non-aggregated
    /// columns read the first combo's resolved value (every combo in a real group shares it by
    /// construction); aggregated columns gather ONE raw value per combo in the group (null for
    /// a combo where the owning dataitem's buffer/slot is unavailable, so Count still sees the
    /// true combo count and Sum/Average/Min/Max skip exactly the missing ones) and hand them to
    /// ctx.ComputeAggregate — al-runner's own aggregation math, not re-derived here.
    /// </summary>
    private static object?[] BuildAggregateRow(JoinContext ctx, JoinProjectionPlan plan, IReadOnlyList<Dictionary<string, object?>> groupCombos)
    {
        var fields = new object?[plan.SlotCount];
        foreach (var col in plan.Columns)
        {
            if (col.Aggregation != "None")
            {
                var rawValues = new object?[groupCombos.Count];
                for (int i = 0; i < groupCombos.Count; i++)
                {
                    var combo = groupCombos[i];
                    rawValues[i] = combo.TryGetValue(col.OwnerName, out var buf) && buf != null
                        && col.TableSlot >= 0 && col.TableSlot < BufFieldCount(buf)
                        ? BufGet(buf, col.TableSlot)
                        : null;
                }
                fields[col.QuerySlot] = ctx.ComputeAggregate(col.ColumnObj, rawValues);
                continue;
            }
            if (groupCombos.Count > 0)
                fields[col.QuerySlot] = ResolveComboValue(ctx, col, groupCombos[0]);
        }
        return fields;
    }

    // Read all rows of a dataitem's table (honouring its own table filters) as table-shaped
    // buffers, via the provider's genuine FindImplementation (no projection).
    private static List<object> ReadDataItemRows(JoinContext ctx, object dataAccessSource, object dataItem, object table)
    {
        var result = new List<object>();
        var dataAccess = ctx.GetDataAccessForTable(dataAccessSource, table);
        var provider = ctx.GetDataProvider(dataAccess);
        if (provider == null) return result;
        ctx.EnsureProjectionReflection(provider);
        var req = ctx.BuildFindAllRequest(provider, dataItem, table);
        if (req == null) return result;
        var rows = ctx.FindImplementation(provider, req);
        foreach (var r in rows) result.Add(r);
        return result;
    }

    // ── projection plan ───────────────────────────────────────────────────────────
    private sealed class JoinColumn
    {
        public int QuerySlot;
        public string OwnerName = "";
        public int TableSlot = -1;
        public object? SourceField; // NCLMetaField (object) — for typed left-outer defaults
        public object ColumnObj = null!; // NCLMetaQueryColumn (object) — for ctx.ComputeAggregate
        // "None" unless Method = Sum/Count/Average/Min/Max (issue #2146). Every non-filter-only
        // column is either a GROUP BY key (Aggregation == "None") or aggregated — never both.
        public string Aggregation = "None";
    }
    private sealed class JoinProjectionPlan
    {
        public int SlotCount;
        public List<JoinColumn> Columns = new();
    }

    /// <summary>True when the runtime NCLMetaQueryColumn was created FilterOnly (a
    /// `filter(...)` element with no result `column(...)`) — ColumnIndex alone cannot tell
    /// this apart from a genuinely-projected slot-0 column (see the ColumnType reflection
    /// comment in EnsureReflection); QUERY-COLUMN.ColumnType is the only reliable signal.
    /// Falls back to false (treat as a normal/projected column) if the runtime type predates
    /// this property.</summary>
    private static bool IsFilterOnlyColumn(object col)
        => _pColColumnType?.GetValue(col)?.ToString() == "FilterOnly";

    // Issue #2146: AggregationType.None serializes as "None" — a column with any OTHER
    // value (Sum/Count/Average/Min/Max) is aggregated and implicitly GROUPs the join's
    // result by every other Normal column, mirroring RecordPatches.QueryProjection.cs's
    // single-dataitem GROUP BY.
    private static string AggregationOf(object col) => _pColAggregationType?.GetValue(col)?.ToString() ?? "None";

    private static JoinProjectionPlan BuildJoinProjectionPlan(JoinContext ctx, object queryDef)
    {
        var plan = new JoinProjectionPlan();
        int maxSlot = -1;
        var dataItems = ((IEnumerable)_pQueryDefDataItems!.GetValue(queryDef)!).Cast<object>().ToList();

        // #2423: a multi-real-dataitem JOIN that also selects a FlowField column reaches this
        // plan with the FlowField-calculation synthesized dataitem (SubQueryDefinition != null
        // -- see the field comment above) still in the RAW dataitem list, same as GetRealDataItems
        // filters out elsewhere. Its own "column" is a real NCLMetaQueryColumn with the outer
        // AL-compiled Id/ColumnIndex (see #2300's PR body), so ResolveTableSlot below resolves it
        // to a field on the FlowField's SOURCE table -- a table this join never reads a row buffer
        // for at all (Execute's own row-reading, above, already excludes this dataitem). Before
        // this guard, that silently produced the column's typed default (observed: 0 instead of
        // the oracle's 7.25) -- a wrong answer read back with no error, exactly the silent default
        // .claude/rules/loud-failures.md forbids. Neither BuildJoinProjectionPlan nor its
        // al-runner-side mirror ComputeJoinColumnSlotMap route a FlowField column through
        // FlowFieldPatches.CalcOneFlowFieldForQueryRow the way the single-dataitem projection path
        // (#2300) does -- tracked as the remaining gap in #2423. Fail loudly instead of guessing.
        foreach (var di in dataItems)
        {
            if (_pDataItemSubQueryDefinition!.GetValue(di) == null) continue;
            var subDataItemName = (string)_pDataItemName!.GetValue(di)!;
            throw ctx.OutOfScope(
                "NavQuery (multi-dataitem join with a FlowField column)",
                $"query-join-flowfield-column-not-implemented -- dataitem '{subDataItemName}' is the " +
                "synthesized FlowField-calculation sub-dataitem for a column selected alongside a " +
                "real multi-dataitem JOIN; this runner does not yet compute a FlowField column's " +
                "value in the join projection path (only the single-real-dataitem case is wired, " +
                "via FlowFieldPatches.CalcOneFlowFieldForQueryRow) -- see #2423");
        }

        // Pass 1: genuinely-projected (non-filter-only) columns get their real ColumnIndex slot.
        foreach (var di in dataItems)
        {
            var name = (string)_pDataItemName!.GetValue(di)!;
            var cols = ((IEnumerable?)_pDataItemQueryColumns!.GetValue(di))?.Cast<object>() ?? Enumerable.Empty<object>();
            foreach (var col in cols)
            {
                if (IsFilterOnlyColumn(col)) continue; // handled in pass 2 below.
                int querySlot = (int)_pColColumnIndex!.GetValue(col)!;
                if (querySlot < 0) continue; // defensive: shouldn't happen for a non-filter-only column.
                if (querySlot > maxSlot) maxSlot = querySlot;
                plan.Columns.Add(new JoinColumn { QuerySlot = querySlot, OwnerName = name, TableSlot = ResolveTableSlot(col, out var srcField), SourceField = srcField, ColumnObj = col, Aggregation = AggregationOf(col) });
            }
        }

        // Pass 2: filter-only columns (referenced only via `filter(...)`, never `column(...)`,
        // and — Query 777's own shape — join-key fields with no declared column at all) get NO
        // real ColumnIndex slot from BC, so mint dedicated EXTRA slots past every projected
        // column, in the SAME deterministic (dataitem, then declaration) order that
        // RecordPatches.QueryProjection.ApplyJoinRuntimeFilters recomputes independently (the two
        // live in separate assemblies and cannot share a single source of truth for this map —
        // see the comment there). This is what lets a runtime SetRange/SetFilter on a
        // non-projected column (e.g. Query 777's "User Security ID") be evaluated post-join
        // instead of either aliasing onto an unrelated real column's slot (the original bug —
        // NavNCLInvalidComparisonException comparing the filter's NavGuid against whatever
        // happened to land in slot 0) or being silently dropped.
        int nextExtraSlot = maxSlot + 1;
        foreach (var di in dataItems)
        {
            var name = (string)_pDataItemName!.GetValue(di)!;
            var cols = ((IEnumerable?)_pDataItemQueryColumns!.GetValue(di))?.Cast<object>() ?? Enumerable.Empty<object>();
            foreach (var col in cols)
            {
                if (!IsFilterOnlyColumn(col)) continue;
                plan.Columns.Add(new JoinColumn { QuerySlot = nextExtraSlot++, OwnerName = name, TableSlot = ResolveTableSlot(col, out var srcField), SourceField = srcField, ColumnObj = col });
            }
        }

        plan.SlotCount = Math.Max(maxSlot + 1, nextExtraSlot);
        return plan;
    }

    private static int ResolveTableSlot(object col, out object? srcField)
    {
        int tableSlot = -1;
        srcField = null;
        try
        {
            srcField = _pColSourceTableField!.GetValue(col);
            if (srcField != null)
                tableSlot = (int)_pFieldColumnIndex!.GetValue(srcField)!;
        }
        catch { tableSlot = -1; srcField = null; }
        return tableSlot;
    }

    // Apply the query's OrderBy (top-level result ordering) over the projected result rows.
    private static void ApplyJoinOrderBy(List<object?[]> projected, object queryDef)
    {
        var orderBys = ((IEnumerable?)_pQueryDefOrderBy!.GetValue(queryDef))?.Cast<object>().ToList();
        if (orderBys == null || orderBys.Count == 0) return;

        var keys = new List<(int slot, bool desc)>();
        foreach (var ob in orderBys)
        {
            int slot = -1;
            try
            {
                var col = _pOrderByColumn?.GetValue(ob);
                if (col != null)
                {
                    int ci = (int)_pColColumnIndex!.GetValue(col)!;
                    if (ci >= 0) slot = ci;
                }
            }
            catch { slot = -1; }
            if (slot < 0) continue;
            bool desc = false;
            try { desc = (_pOrderBySorting?.GetValue(ob)?.ToString() ?? "").IndexOf("Desc", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { desc = false; }
            keys.Add((slot, desc));
        }
        if (keys.Count == 0) return;

        IEnumerable<object?[]> seq = projected;
        for (int k = keys.Count - 1; k >= 0; k--)
        {
            var (slot, desc) = keys[k];
            seq = desc
                ? seq.OrderByDescending(r => r != null && slot < r.Length ? r[slot] : null, NavValueComparer.Instance)
                : seq.OrderBy(r => r != null && slot < r.Length ? r[slot] : null, NavValueComparer.Instance);
        }
        var sorted = seq.ToList();
        projected.Clear();
        projected.AddRange(sorted);
    }

    private sealed class NavValueComparer : IComparer<object?>
    {
        public static readonly NavValueComparer Instance = new();
        public int Compare(object? x, object? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            if (x is IComparable cmp) { try { return cmp.CompareTo(y); } catch { } }
            return string.CompareOrdinal(x.ToString(), y.ToString());
        }
    }
}
