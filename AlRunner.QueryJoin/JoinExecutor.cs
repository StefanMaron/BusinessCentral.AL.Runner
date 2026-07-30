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
        _pColParentDataItem = tCol.GetProperty("ParentDataItem", F)!;
        _pFieldColumnIndex = tField.GetProperty("ColumnIndex", F)!;
        _pFieldFieldClass = tField.GetProperty("FieldClass", F);
        _pOrderByColumn = tOrderBy.GetProperty("Column", F) ?? tOrderBy.GetProperty("QueryColumn", F);
        _pOrderBySorting = tOrderBy.GetProperty("Sorting", F) ?? tOrderBy.GetProperty("SortOrder", F);
        _pMetaQueryQueryDefinition = tMetaQuery.GetProperty("QueryDefinition", BindingFlags.Public | BindingFlags.Instance)!;
        _ready = true;
    }

    /// <summary>True iff this query definition has more than one (flat) dataitem — i.e. a join.</summary>
    public static bool IsMultiDataItem(object queryDefinition)
    {
        EnsureReflection(queryDefinition);
        var items = (ICollection)_pQueryDefDataItems!.GetValue(queryDefinition)!;
        return items.Count > 1;
    }

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

        var dataItems = ((IEnumerable)_pQueryDefDataItems!.GetValue(queryDef)!).Cast<object>().ToList();

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
        var projected = new List<object?[]>(combos.Count);
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
    }
    private sealed class JoinProjectionPlan
    {
        public int SlotCount;
        public List<JoinColumn> Columns = new();
    }

    private static JoinProjectionPlan BuildJoinProjectionPlan(JoinContext ctx, object queryDef)
    {
        var plan = new JoinProjectionPlan();
        int maxSlot = -1;
        var dataItems = ((IEnumerable)_pQueryDefDataItems!.GetValue(queryDef)!).Cast<object>().ToList();
        foreach (var di in dataItems)
        {
            var name = (string)_pDataItemName!.GetValue(di)!;
            var cols = ((IEnumerable?)_pDataItemQueryColumns!.GetValue(di))?.Cast<object>() ?? Enumerable.Empty<object>();
            foreach (var col in cols)
            {
                int querySlot = (int)_pColColumnIndex!.GetValue(col)!;
                if (querySlot < 0) continue; // filter-only column (no result slot)
                if (querySlot > maxSlot) maxSlot = querySlot;
                int tableSlot = -1;
                object? srcField = null;
                try
                {
                    srcField = _pColSourceTableField!.GetValue(col);
                    if (srcField != null)
                        tableSlot = (int)_pFieldColumnIndex!.GetValue(srcField)!;
                }
                catch { tableSlot = -1; srcField = null; }
                plan.Columns.Add(new JoinColumn { QuerySlot = querySlot, OwnerName = name, TableSlot = tableSlot, SourceField = srcField });
            }
        }
        plan.SlotCount = maxSlot + 1;
        return plan;
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
