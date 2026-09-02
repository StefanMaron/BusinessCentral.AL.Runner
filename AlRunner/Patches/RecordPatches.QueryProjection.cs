// RecordPatches.QueryProjection — project query columns from in-memory table rows.
//
// PROBLEM (single-dataitem query reads return 0):
//   NavQuery.FindDataImplAsync issues a find against
//   DataAccessSource.GetDataAccessForQuery(NCLMetaQuery).FindAsync(request) where the
//   request's MetaApplicationObject is the NCLMetaQuery. On the skeleton runtime that
//   DataAccess is backed by BC's TempTableDataProvider (the in-memory store where the
//   AL test inserted its rows). TempTableDataProvider is TABLE-shaped: it returns
//   ReadOnlyRecordBuffers whose slots are indexed by the table field ColumnIndex.
//   But NavQuery.GetColumnValue reads CurrentDataRow[queryColumn.ColumnIndex], where
//   queryColumn.ColumnIndex is the 0-based QUERY result slot. The two index spaces do
//   not line up, so every column comes back as the default (0 / '').
//
//   In real BC the SQL provider projects via a SELECT (table field -> result slot);
//   the temp provider never does because queries normally never reach it. We reproduce
//   exactly that projection here.
//
// FAITHFUL FIX (mirrors SQL SELECT projection):
//   The public TempTableDataProvider.Find / FindFromPosition entry points are
//   Cecil-redirected (NclCecilRewrite) to the two helpers below. They call the
//   provider's own private FindImplementation / FindByPositionImplementation (the
//   genuine in-memory storage + filter + sort logic, untouched), then — and only when
//   the request's MetaApplicationObject is an NCLMetaQuery — re-shape each table buffer
//   into a query-shaped ReadOnlyRecordBuffer:
//       projected[col.ColumnIndex] = tableBuffer[col.SourceTableField.ColumnIndex]
//   For non-query (ordinary Record) reads the buffers pass straight through unchanged,
//   so this is a no-op on the 99% table-read path.
//
// SCOPE: single-dataitem (no join) queries. A join request would have a query
//   definition with >1 included table; the temp provider already cannot serve that
//   (DataAccessSource.GetDataAccessForQuery throws QueriesBetweenDataSourcesNotSupported
//   when the included tables map to different DataAccess instances), so join handling
//   stays a follow-up. A const (ColumnType.ConstValue) column has no SourceTableField
//   and is left at its slot default rather than faked — surfaced as a follow-up, never
//   silently wrong for source columns.
//
// AGGREGATION (Method = Sum/Count/Average/Min/Max — issue #2137):
//   NCLMetaQueryColumn.AggregationType is BC's own per-column aggregation method. A query
//   with at least one aggregated column has an IMPLICIT GROUP BY over every OTHER Normal
//   (non-aggregated, non-const, non-filter-only) column — exactly what BC's compiled SQL
//   SELECT ... GROUP BY does. ProjectQueryRows detects that case (ProjectionPlan.HasAggregate)
//   and groups the already-filtered/sorted raw rows by the non-aggregate columns' source
//   field values (GroupKey, using NavValue's own IEquatable<NavValue>/GetHashCode — the same
//   equality BC's record buffers already trust), then computes each aggregate column's value
//   over its group via BuildAggregateRow/ComputeAggregate. A query with ONLY aggregate columns
//   (no grouping column at all) is BC's scalar-aggregate case — one output row always, even
//   over zero matched source rows (SUM/COUNT/AVERAGE default to 0, MIN/MAX to the column's
//   typed default via FlowFieldPatches.TypedDefaultForField) — matching SQL's "GROUP BY ()"
//   always producing exactly one group. TOP is applied AFTER aggregation now (previously it
//   capped the RAW rows before projection, which is only correct when there's no grouping —
//   capping raw rows first would silently drop rows out of a group before they're summed).
//
//   Runtime SetRange/SetFilter on an AGGREGATED column is a HAVING-clause filter (evaluated
//   against the aggregated result, not the raw row), which TranslateQueryFilters' WHERE-style
//   per-row pushdown cannot express — it throws RunnerOutOfScopeException rather than
//   silently filtering raw rows by the source field instead (see #2146). A multi-dataitem
//   JOIN with any aggregated column also throws: the isolated JoinExecutor (QueryJoin.cs)
//   has no GROUP BY of its own, so letting it through would silently return unaggregated
//   joined rows — the exact bug #2137 reports, just downstream of a join instead of a plain
//   scan (see #2146 for both follow-ups: HAVING-style filters and join+aggregate).
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static MethodInfo? _mTtdpFindImpl;
    private static MethodInfo? _mTtdpFindByPositionImpl;
    private static Type? _tFindTypeEnum;
    private static Type? _tReadOnlyRecordBuffer;
    private static ConstructorInfo? _ctorReadOnlyRecordBuffer;
    private static PropertyInfo? _pReqMetaAppObj;
    private static PropertyInfo? _pReqFindType;
    private static PropertyInfo? _pReqTopNumberOfRows;

    private static void EnsureQueryProjectionReflection(object tempProvider)
    {
        if (_mTtdpFindImpl != null) return;
        var ttdp = tempProvider.GetType(); // TempTableDataProvider
        _mTtdpFindImpl = ttdp.GetMethod("FindImplementation",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("TempTableDataProvider.FindImplementation not found");
        _mTtdpFindByPositionImpl = ttdp.GetMethod("FindByPositionImplementation",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("TempTableDataProvider.FindByPositionImplementation not found");

        var nclAsm = ttdp.Assembly;
        _tFindTypeEnum = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FindType");
        _tReadOnlyRecordBuffer = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.ReadOnlyRecordBuffer")
            ?? throw new InvalidOperationException("ReadOnlyRecordBuffer not found");
        // public ReadOnlyRecordBuffer(NCLMetaApplicationObject metaApplicationObject, params NavValue[] immutableFields)
        var navValueArr = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavValue")!.MakeArrayType();
        var metaAppObj = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject")!;
        _ctorReadOnlyRecordBuffer = _tReadOnlyRecordBuffer.GetConstructor(new[] { metaAppObj, navValueArr })
            ?? throw new InvalidOperationException("ReadOnlyRecordBuffer(NCLMetaApplicationObject, NavValue[]) ctor not found");

        var reqBase = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataProviderRequest")!;
        _pReqMetaAppObj = reqBase.GetProperty("MetaApplicationObject", BindingFlags.Public | BindingFlags.Instance)!;
        var findReq = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FindProviderRequest")!;
        _pReqFindType = findReq.GetProperty("FindType", BindingFlags.Public | BindingFlags.Instance)!;
        _pReqTopNumberOfRows = findReq.GetProperty("TopNumberOfRowsToReturn", BindingFlags.Public | BindingFlags.Instance)!;
    }

    /// <summary>
    /// Replacement for TempTableDataProvider.Find(FindProviderRequest, Func&lt;bool&gt;).
    /// Mirrors the original: FindImplementation(request), Take(1) when FindType.FirstOnly,
    /// then projects query columns when the request targets a query.
    /// </summary>
    public static IEnumerable<ReadOnlyRecordBuffer> TempTableDataProvider_Find(
        object self, object request, Func<bool>? onlyCurrentKeyNeededForNextRow)
    {
        EnsureQueryProjectionReflection(self);
        var execRequest = TranslateQueryFilters(request, out var havingFilters);
        var raw = (IEnumerable<ReadOnlyRecordBuffer>)_mTtdpFindImpl!.Invoke(self, new[] { execRequest })!;
        raw = ApplyFirstOnly(request, raw);
        return ProjectIfQuery(request, raw, havingFilters);
    }

    /// <summary>
    /// Replacement for TempTableDataProvider.FindFromPosition(PositionedFindProviderRequest, Func&lt;bool&gt;).
    /// </summary>
    public static IEnumerable<ReadOnlyRecordBuffer> TempTableDataProvider_FindFromPosition(
        object self, object request, Func<bool>? onlyCurrentKeyNeededForNextRow)
    {
        EnsureQueryProjectionReflection(self);
        var execRequest = TranslateQueryFilters(request, out var havingFilters);
        var raw = (IEnumerable<ReadOnlyRecordBuffer>)_mTtdpFindByPositionImpl!.Invoke(self, new[] { execRequest })!;
        raw = ApplyFirstOnly(request, raw);
        return ProjectIfQuery(request, raw, havingFilters);
    }

    private static MethodInfo? _mGetDataAccessForTable_Orig;
    private static PropertyInfo? _pQueryDefIncludedTables;
    private static PropertyInfo? _pDataAccessDataProvider;
    private static PropertyInfo? _pNclMetaQueryQueryDefinition2;

    /// <summary>
    /// Replacement for DataAccessSource.GetDataAccessForQuery(NCLMetaQueryDefinition).
    ///
    /// Single-dataitem queries map to ONE in-memory DataAccess (the temp provider holding
    /// the inserted rows) — return it (original behaviour). Multi-dataitem (join) queries
    /// map each included table to its OWN temp DataAccess; the real engine throws
    /// QueriesBetweenDataSourcesNotSupported because an in-memory cross-provider join is not
    /// supported. The FAITHFUL result of a join over EMPTY tables is zero rows (BC's SQL
    /// join produces no rows when either side is empty). So: if every included table is
    /// empty, return the ROOT (driving) table's DataAccess — FindAsync then runs the query
    /// over an empty driving table and the projection layer yields no rows (correct). If any
    /// included table actually has rows, an in-memory join WOULD change the result, so we
    /// throw RunnerOutOfScopeException rather than silently return wrong/unjoined data.
    /// </summary>
    public static object DataAccessSource_GetDataAccessForQuery(object self, object queryDefinition)
    {
        EnsureGetDataAccessForQueryReflection(self);

        // #2300: NOT NCLMetaQueryDefinition.IncludedTables. That BC-real property calls
        // NCLMetaQueryDefinition.GetAllDataItems, which for a FlowField-calculation synthesized
        // dataitem (SubQueryDefinition != null — see JoinExecutor.cs's field comment) does NOT
        // skip it, it recurses into dataItem.SubQueryDefinition.DataItems and includes THAT
        // table too (real BC needs it there for its own SQL sub-query). This runner never runs
        // that sub-query — the FlowField column is computed directly instead (FlowFieldPatches.
        // CalcOneFlowFieldForQueryRow) — so from here a query with one real dataitem plus a
        // FlowField column must still resolve to ONE table, not two, or the "all tables share
        // one DataAccess" check below wrongly routes it into the multi-dataitem JOIN path.
        var tableList = new List<object>();
        var rawDataItems = (System.Collections.IEnumerable)_pQueryDefDataItems2!.GetValue(queryDefinition)!;
        foreach (var di in rawDataItems)
        {
            if (_pDataItemSubQueryDefinition2?.GetValue(di) != null) continue;
            var t = _pDataItemMetaTable2b!.GetValue(di);
            if (t != null) tableList.Add(t);
        }

        // Resolve each included table's DataAccess via the (already-hooked) per-table route.
        var accesses = new List<object>();
        foreach (var t in tableList)
            accesses.Add(NavDataAccessSource_GetDataAccessForTable(self, (NCLMetaTable)t, false));

        if (accesses.Count == 0)
            return NavDataAccessSource_GetDataAccessForTable(self, null!, false); // shouldn't happen; let original-style path surface

        // Single data source (single dataitem, or all tables already share one DataAccess) —
        // original behaviour: return that single instance. (A genuinely single-dataitem
        // query lands here; the projection layer reshapes the one table's rows.)
        bool singleDataItem = !IsMultiDataItemQuery(queryDefinition);
        bool allSame = accesses.All(a => ReferenceEquals(a, accesses[0]));
        if (singleDataItem && allSame)
            return accesses[0];

        // Multi-dataitem JOIN. We execute the join ourselves in the projection layer
        // (ExecuteJoinQuery), reading every dataitem's table via this DataAccessSource.
        // Stash the source keyed by the query definition so the projection layer can reach
        // the sibling tables, and return the ROOT table's DataAccess so FindAsync still has
        // a provider to call into (whose query-shaped Find we intercept and replace with the
        // joined result set). See RecordPatches.QueryJoin.cs.
        StashJoinSource(queryDefinition, self);
        QLog($"GetDataAccessForQuery: {tableList.Count}-dataitem join → in-memory join via root DataAccess");
        return accesses[0];
    }

    private static PropertyInfo? _pQueryDefDataItems2;
    private static PropertyInfo? _pDataItemSubQueryDefinition2;
    private static PropertyInfo? _pDataItemMetaTable2b;

    private static void EnsureGetDataAccessForQueryReflection(object dataAccessSource)
    {
        if (_pQueryDefIncludedTables != null) return;
        var nclAsm = dataAccessSource.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tQueryDef = nclAsm.GetType(rt + "NCLMetaQueryDefinition")!;
        _pQueryDefIncludedTables = tQueryDef.GetProperty("IncludedTables",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NCLMetaQueryDefinition.IncludedTables not found");
        // #2300: DataItems is internal (no InternalsVisibleTo from al-runner.dll), so it must be
        // read by reflection here, unlike IncludedTables' sibling fields above which happen to be
        // reachable directly elsewhere in this file.
        _pQueryDefDataItems2 = tQueryDef.GetProperty("DataItems",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NCLMetaQueryDefinition.DataItems not found");
        var tDataItem = nclAsm.GetType(rt + "NCLMetaQueryDataItem")!;
        _pDataItemSubQueryDefinition2 = tDataItem.GetProperty("SubQueryDefinition",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pDataItemMetaTable2b = tDataItem.GetProperty("MetaTable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var tDataAccess = nclAsm.GetType(rt + "DataAccess")!;
        _pDataAccessDataProvider = tDataAccess.GetProperty("DataProvider",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    // Does the in-memory temp provider behind <paramref name="table"/> hold any row?
    // Uses the provider's own FindImplementation with a table-shaped (NOT query) request so
    // no projection happens — we only need to know if a single row exists.
    private static bool TableHasAnyRow(object dataAccessSource, NCLMetaTable table)
    {
        try
        {
            var dataAccess = NavDataAccessSource_GetDataAccessForTable(dataAccessSource, table, false);
            var provider = _pDataAccessDataProvider!.GetValue(dataAccess);
            if (provider == null) return false;
            EnsureQueryProjectionReflection(provider);
            var req = BuildTableFindAnyRequest(provider, table);
            if (req == null) return true; // can't build probe → assume non-empty (safer: throw OOS, not fake)
            var rows = (System.Collections.IEnumerable)_mTtdpFindImpl!.Invoke(provider, new[] { req })!;
            foreach (var _ in rows) return true;
            return false;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            QLog($"TableHasAnyRow({table?.TableName}) probe failed: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace} → treating as non-empty");
            return true; // never silently claim empty on uncertainty
        }
    }

    private static ConstructorInfo? _ctorFindProviderRequestProbe;
    private static object? BuildTableFindAnyRequest(object provider, NCLMetaTable table)
    {
        var nclAsm = provider.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tFindReq = nclAsm.GetType(rt + "FindProviderRequest")!;
        // Reuse the public FindProviderRequest ctor (the 13+-arg one used in QueryProjection).
        _ctorFindProviderRequestProbe ??= tFindReq.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length >= 13
                && c.GetParameters()[1].ParameterType.Name == "NCLMetaApplicationObject");
        if (_ctorFindProviderRequestProbe == null) return null;

        object? StaticMember(string typeName, string member)
        {
            var t = nclAsm.GetType(rt + typeName)!;
            return t.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                ?? t.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        }
        var emptyFam = StaticMember("FiltersAndMarks", "Empty");
        var emptyTfd = StaticMember("TableFilterDictionary", "Empty");
        var fieldListEmpty = StaticMember("FieldList", "Empty");
        var ps = _ctorFindProviderRequestProbe.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            args[i] = ps[i].Name switch
            {
                "companyToken" => 0,
                "metaApplicationObject" => table,                 // table-shaped: no projection
                "lockState" => Enum.ToObject(ps[i].ParameterType, 0),
                "filtersAndMarks" => emptyFam,
                "globalAndSecurityFilters" => emptyTfd,
                "flowFieldSecurityFiltering" => ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null,
                "autoCalcFields" => null,
                "sortingFields" => null,
                "findType" => Enum.ToObject(_tFindTypeEnum!, FirstOnlyOrdinal()),
                "topNumberOfRowsToReturn" => 1,
                "skipNumberOfRows" => 0,
                "fastNumberOfRowsToReturn" => 1,
                "timeout" => null,
                "fieldLoadInfo" => null,
                _ => ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null)
            };
        }
        return _ctorFindProviderRequestProbe.Invoke(args);
    }

    // ── Query filter translation (SetRange / SetFilter on a query column) ──────────
    // NavQuery.SetRange/SetFilter store a FilterFieldDictionary keyed by the
    // NCLMetaQueryColumn, with each FilterExpression bound to that column's
    // ExpressionContext. The TempTableDataProvider filter visitor evaluates
    // `(NCLMetaField)expressionContext.Metadata` against the table buffer
    // (input[NCLMetaField.ColumnIndex]) — a query column is NOT an NCLMetaField, so the
    // raw filter never matches the table row. Real BC's SQL provider applies the filter
    // in the WHERE clause against the source column. We reproduce that: rebuild each
    // query-column-keyed filter so it targets the column's SourceTableField (the real
    // NCLMetaField) and re-key the dictionary by that field, then hand the temp provider
    // a table-shaped request it can evaluate. Single-dataitem only: every column maps to
    // one included table.
    private static Type? _tFiltersAndMarks;
    private static Type? _tFilterFieldDictionary;
    private static Type? _tUnaryFilterExpr;
    private static Type? _tBinaryFilterExpr;
    private static Type? _tWildcardFilterExpr;
    private static Type? _tFilterExpr;
    private static Type? _tNavFieldMetadata;
    private static bool _filterReflectionReady;

    // For the extended-slot recomputation in ApplyJoinRuntimeFilters — mirrors
    // AlRunner.QueryJoin.JoinExecutor's own DataItems/QueryColumns/ColumnType reflection
    // (a SEPARATE, isolated assembly that cannot share these PropertyInfo handles).
    private static Type? _tNCLMetaQueryDefinition;
    private static Type? _tNCLMetaQueryDataItem;
    private static PropertyInfo? _pQueryDefDataItemsQ;
    private static PropertyInfo? _pDataItemQueryColumnsQ;
    private static PropertyInfo? _pColColumnTypeQ;
    private static PropertyInfo? _pColColumnIndexQ2;

    private static void EnsureFilterReflection()
    {
        if (_filterReflectionReady) return;
        var asm = _tReadOnlyRecordBuffer!.Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        _tFiltersAndMarks = asm.GetType(rt + "FiltersAndMarks");
        _tFilterFieldDictionary = asm.GetType(rt + "FilterFieldDictionary");
        _tUnaryFilterExpr = asm.GetType(rt + "UnaryFilterExpression");
        _tBinaryFilterExpr = asm.GetType(rt + "BinaryFilterExpression");
        _tWildcardFilterExpr = asm.GetType(rt + "WildcardFilterExpression");
        _tFilterExpr = asm.GetType(rt + "FilterExpression");
        _tNavFieldMetadata = asm.GetType(rt + "INavFieldMetadata");
        _tNCLMetaQueryColumn = asm.GetType(rt + "NCLMetaQueryColumn");
        _tNCLMetaQueryDefinition = asm.GetType(rt + "NCLMetaQueryDefinition");
        _tNCLMetaQueryDataItem = asm.GetType(rt + "NCLMetaQueryDataItem");
        const BindingFlags anyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _pQueryDefDataItemsQ = _tNCLMetaQueryDefinition?.GetProperty("DataItems", anyInstance);
        _pDataItemQueryColumnsQ = _tNCLMetaQueryDataItem?.GetProperty("QueryColumns", anyInstance);
        _pColColumnTypeQ = _tNCLMetaQueryColumn?.GetProperty("ColumnType", anyInstance);
        _filterReflectionReady = true;
    }

    /// <summary>
    /// Recompute, for every QueryColumn across every DataItem of <paramref name="queryDef"/>, the
    /// slot it occupies in the join-projected row buffer AlRunner.QueryJoin.JoinExecutor
    /// produces — by reference identity, since neither assembly can hand the other a
    /// PropertyInfo/slot map directly (JoinExecutor is loaded in an isolated ALC specifically so
    /// its assembly never leaks an Ncl type into al-runner's own startup surface).
    ///
    /// MUST mirror JoinExecutor.BuildJoinProjectionPlan's two-pass algorithm EXACTLY (same
    /// dataitem enumeration order, same "normal columns first at their real ColumnIndex, then
    /// filter-only columns at sequential extra slots past the projected max" rule) — that
    /// algorithm is duplicated rather than shared for the isolation reason above, and duplicated
    /// logic that drifts is exactly the failure mode SCOPE-AUDIT-style comments warn about, so
    /// change both together.
    /// </summary>
    private static Dictionary<object, int> ComputeJoinColumnSlotMap(object queryDef)
    {
        var map = new Dictionary<object, int>();
        if (_pQueryDefDataItemsQ == null || _pDataItemQueryColumnsQ == null) return map;
        var dataItems = ((System.Collections.IEnumerable)_pQueryDefDataItemsQ.GetValue(queryDef)!).Cast<object>().ToList();

        int maxSlot = -1;
        foreach (var di in dataItems)
        {
            var cols = (_pDataItemQueryColumnsQ.GetValue(di) as System.Collections.IEnumerable)?.Cast<object>()
                ?? Enumerable.Empty<object>();
            foreach (var col in cols)
            {
                if (IsFilterOnlyColumnQ(col)) continue;
                _pColColumnIndexQ2 ??= _tNCLMetaQueryColumn!.GetProperty("ColumnIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
                var idx = (int)_pColColumnIndexQ2.GetValue(col)!;
                if (idx < 0) continue;
                map[col] = idx;
                if (idx > maxSlot) maxSlot = idx;
            }
        }

        int nextExtraSlot = maxSlot + 1;
        foreach (var di in dataItems)
        {
            var cols = (_pDataItemQueryColumnsQ.GetValue(di) as System.Collections.IEnumerable)?.Cast<object>()
                ?? Enumerable.Empty<object>();
            foreach (var col in cols)
            {
                if (!IsFilterOnlyColumnQ(col)) continue;
                map[col] = nextExtraSlot++;
            }
        }
        return map;
    }

    private static bool IsFilterOnlyColumnQ(object col)
        => _pColColumnTypeQ?.GetValue(col)?.ToString() == "FilterOnly";

    /// <summary>
    /// If <paramref name="request"/> targets a query and carries query-column-keyed
    /// filters, returns a clone of the request with those filters re-keyed/re-targeted to
    /// the source table fields so the temp provider can evaluate them. Otherwise returns
    /// the request unchanged.
    ///
    /// A runtime SetRange/SetFilter on an AGGREGATED column (Method = Sum/Count/Average/
    /// Min/Max — #2137/#2146) is a HAVING-clause filter: it must be evaluated against the
    /// per-GROUP aggregated result, not the raw row, so it is EXCLUDED from the pushed-down
    /// (WHERE-style) request here — retargeting it to the source table field the way an
    /// ordinary column's filter is retargeted below would silently filter raw rows by the
    /// unaggregated value instead, a different silently-wrong answer than #2137 but the same
    /// class of bug. Such filters are instead returned via <paramref name="havingFilters"/>
    /// so the caller can apply them AFTER aggregation (see ApplyHavingFilters).
    /// </summary>
    private static object TranslateQueryFilters(object request, out List<(object Column, object Expr)> havingFilters)
    {
        havingFilters = new List<(object, object)>();
        var metaAppObj = _pReqMetaAppObj!.GetValue(request);
        if (metaAppObj == null || _tNCLMetaQuery == null || !_tNCLMetaQuery.IsInstanceOfType(metaAppObj))
            return request; // ordinary table read — nothing to translate.

        EnsureFilterReflection();
        var filtersAndMarks = request.GetType().GetProperty("FiltersAndMarks", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(request);
        if (filtersAndMarks == null) return request;
        var filters = _tFiltersAndMarks!.GetProperty("Filters", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(filtersAndMarks);
        if (filters == null) return request;

        // FilterFieldDictionary.Items : Tuple<INavFieldMetadata, FilterExpression>[]
        var items = (Array?)_tFilterFieldDictionary!.GetProperty("Items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(filters);
        if (items == null || items.Length == 0) return request; // no field filters → nothing to do.

        var translatedTuples = new List<object>();
        bool anyTranslated = false;
        foreach (var item in items)
        {
            // Tuple<INavFieldMetadata, FilterExpression>
            var key = item!.GetType().GetProperty("Item1")!.GetValue(item);
            var expr = item.GetType().GetProperty("Item2")!.GetValue(item);
            if (key != null && _tNCLMetaQueryColumn != null && _tNCLMetaQueryColumn.IsInstanceOfType(key))
            {
                if (((NCLMetaQueryColumn)key).AggregationType != AggregationType.None)
                {
                    // HAVING-clause filter — exclude from the WHERE-style push-down (pushing it
                    // unchanged would make FindImplementation try to cast this NCLMetaQueryColumn
                    // key to NCLMetaField and fail; retargeting it to the source field would
                    // filter raw pre-aggregation rows, which is the #2137-class bug). Hand it
                    // back for post-aggregation evaluation instead.
                    havingFilters.Add((key!, expr!));
                    anyTranslated = true;
                    continue;
                }

                var srcField = key.GetType().GetProperty("SourceTableField", BindingFlags.Public | BindingFlags.Instance)!.GetValue(key);
                if (srcField != null && expr != null)
                {
                    var srcCtx = srcField.GetType().GetProperty("ExpressionContext", BindingFlags.Public | BindingFlags.Instance)!.GetValue(srcField);
                    var retargeted = RetargetFilterExpression(expr, srcCtx!);
                    translatedTuples.Add(MakeFieldTuple(srcField, retargeted));
                    anyTranslated = true;
                    continue;
                }
            }
            translatedTuples.Add(item); // already table-keyed or no source — keep as-is.
        }
        if (!anyTranslated) return request;

        // Build FilterFieldDictionary(IEnumerable<Tuple<INavFieldMetadata, FilterExpression>>)
        var newFilters = BuildFilterFieldDictionary(translatedTuples);
        var markedRecords = _tFiltersAndMarks.GetProperty("MarkedRecords", BindingFlags.Public | BindingFlags.Instance)!.GetValue(filtersAndMarks);
        var newFam = Activator.CreateInstance(_tFiltersAndMarks, newFilters, markedRecords)!;
        return CloneRequestWithFilters(request, newFam);
    }

    private static Type? _tNCLMetaQueryColumn;

    private static object MakeFieldTuple(object field, object expr)
    {
        // Tuple<INavFieldMetadata, FilterExpression>
        var tupleType = typeof(Tuple<,>).MakeGenericType(_tNavFieldMetadata!, _tFilterExpr!);
        return Activator.CreateInstance(tupleType, field, expr)!;
    }

    private static object BuildFilterFieldDictionary(List<object> tuples)
    {
        var tupleType = typeof(Tuple<,>).MakeGenericType(_tNavFieldMetadata!, _tFilterExpr!);
        var arr = Array.CreateInstance(tupleType, tuples.Count);
        for (int i = 0; i < tuples.Count; i++) arr.SetValue(tuples[i], i);
        // FilterFieldDictionary(IEnumerable<Tuple<INavFieldMetadata, FilterExpression>>)
        var ienumType = typeof(IEnumerable<>).MakeGenericType(tupleType);
        var ctor = _tFilterFieldDictionary!.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == ienumType);
        return ctor.Invoke(new object[] { arr });
    }

    /// <summary>Rebuild a filter expression tree, retargeting Unary leaves to <paramref name="targetCtx"/>.</summary>
    private static object RetargetFilterExpression(object expr, object targetCtx)
    {
        var t = expr.GetType();
        if (_tUnaryFilterExpr!.IsInstanceOfType(expr))
        {
            // new UnaryFilterExpression(FilterExpressionType, NavValue, FilterExpressionContext, valueToken, isConstInMetadata)
            var exprType = _tFilterExpr!.GetProperty("ExpressionType", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var value = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var ctor = _tUnaryFilterExpr.GetConstructors()
                .First(c => c.GetParameters().Length >= 3
                    && c.GetParameters()[0].ParameterType.Name == "FilterExpressionType");
            var ps = ctor.GetParameters();
            var args = new object?[ps.Length];
            args[0] = exprType; args[1] = value; args[2] = targetCtx;
            for (int i = 3; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
            return ctor.Invoke(args);
        }
        if (_tBinaryFilterExpr!.IsInstanceOfType(expr))
        {
            var exprType = _tFilterExpr!.GetProperty("ExpressionType", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var left = t.GetProperty("Left", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var right = t.GetProperty("Right", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var newLeft = RetargetFilterExpression(left!, targetCtx);
            var newRight = RetargetFilterExpression(right!, targetCtx);
            var ctor = _tBinaryFilterExpr.GetConstructors()
                .First(c => c.GetParameters().Length == 3 && c.GetParameters()[0].ParameterType.Name == "FilterExpressionType");
            return ctor.Invoke(new object?[] { exprType, newLeft, newRight });
        }
        // #2299: Query.SetFilter(<col>, 'ABC*') on a query column DOES produce a
        // WildcardFilterExpression (SetRange with an exact value produces the Unary/Binary
        // shapes above, but a filter containing wildcard characters does not). Left
        // unretargeted, its ExpressionContext.Metadata stays the NCLMetaQueryColumn key, and
        // BC's own TempTableDataProvider.RecordBufferEvaluatorVisitor.Evaluate unconditionally
        // casts `(NCLMetaField)expressionContext.Metadata` for the wildcard branch — an
        // InvalidCastException at the first Read(), not a silent non-match. Rebuild it against
        // the retargeted (real table field) context the same way Unary/Binary already are.
        if (_tWildcardFilterExpr!.IsInstanceOfType(expr))
        {
            // public WildcardFilterExpression(bool isNegated, string pattern, bool isCaseAndAccentInsensitive, FilterExpressionContext expressionContext)
            var isNegated = t.GetProperty("IsNegated", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var pattern = t.GetProperty("Pattern", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var isCaseAndAccentInsensitive = t.GetProperty("IsCaseAndAccentInsensitive", BindingFlags.Public | BindingFlags.Instance)!.GetValue(expr);
            var ctor = _tWildcardFilterExpr.GetConstructors()
                .First(c => c.GetParameters().Length == 4 && c.GetParameters()[0].ParameterType == typeof(bool));
            return ctor.Invoke(new object?[] { isNegated, pattern, isCaseAndAccentInsensitive, targetCtx });
        }
        // Other expression kinds (fieldEqualsField/fullText/etc.) are not produced by
        // single-column SetRange/SetFilter; leave them (will not match a table field and
        // is a documented follow-up if a test relies on them).
        return expr;
    }

    private static object CloneRequestWithFilters(object request, object newFiltersAndMarks)
    {
        // Both FindProviderRequest and PositionedFindProviderRequest share the same field
        // set; reconstruct via the full ctor pulling every other field off the original.
        var t = request.GetType();
        object Get(string n) => t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance)!.GetValue(request)!;
        var isPositioned = t.Name == "PositionedFindProviderRequest";
        var ctor = t.GetConstructors().First(c =>
        {
            var ps = c.GetParameters();
            return ps.Length >= 13 && ps[1].ParameterType.Name == "NCLMetaApplicationObject";
        });
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            args[i] = ps[i].Name switch
            {
                "companyToken" => Get("CompanyToken"),
                "metaApplicationObject" => Get("MetaApplicationObject"),
                "lockState" => Get("LockState"),
                "filtersAndMarks" => newFiltersAndMarks,
                "globalAndSecurityFilters" => GetOrNull("GlobalAndSecurityFilters"),
                "flowFieldSecurityFiltering" => Get("FlowFieldSecurityFiltering"),
                "autoCalcFields" => GetOrNull("AutoCalcFields"),
                "sortingFields" => GetOrNull("SortingFields"),
                "findType" => Get("FindType"),
                "startingPosition" => isPositioned ? GetOrNull("StartingPosition") : null,
                "includeCurrent" => isPositioned ? Get("IncludeCurrent") : false,
                "topNumberOfRowsToReturn" => Get("TopNumberOfRowsToReturn"),
                "skipNumberOfRows" => Get("SkipNumberOfRows"),
                "fastNumberOfRowsToReturn" => Get("FastNumberOfRowsToReturn"),
                "timeout" => GetOrNull("Timeout"),
                "fieldLoadInfo" => GetOrNull("FieldLoadInfo"),
                _ => ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null)
            };
        }
        return ctor.Invoke(args);

        object? GetOrNull(string n) => t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance)?.GetValue(request);
    }

    private static IEnumerable<ReadOnlyRecordBuffer> ApplyFirstOnly(object request, IEnumerable<ReadOnlyRecordBuffer> rows)
    {
        // Original Find/FindFromPosition return enumerable.Take(1) when FindType == FirstOnly.
        var findType = _pReqFindType!.GetValue(request);
        var firstOnly = findType != null && Convert.ToInt32(findType) == FirstOnlyOrdinal();
        return firstOnly ? rows.Take(1) : rows;
    }

    private static int _firstOnlyOrdinal = -1;
    private static int FirstOnlyOrdinal()
    {
        if (_firstOnlyOrdinal < 0)
            _firstOnlyOrdinal = Convert.ToInt32(Enum.Parse(_tFindTypeEnum!, "FirstOnly"));
        return _firstOnlyOrdinal;
    }

    private static IEnumerable<ReadOnlyRecordBuffer> ProjectIfQuery(
        object request, IEnumerable<ReadOnlyRecordBuffer> rows, List<(object Column, object Expr)> havingFilters)
    {
        var metaAppObj = _pReqMetaAppObj!.GetValue(request);
        if (metaAppObj == null || _tNCLMetaQuery == null || !_tNCLMetaQuery.IsInstanceOfType(metaAppObj))
            return rows; // ordinary table read — pass through unchanged.

        // Multi-dataitem JOIN: ignore the single-table `rows` (the root scan the engine
        // requested) and produce the joined+projected result set ourselves by reading
        // every dataitem's table. See RecordPatches.QueryJoin.cs.
        var queryDef = _tNCLMetaQuery.GetProperty("QueryDefinition", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(metaAppObj);
        if (queryDef != null && IsMultiDataItemQuery(queryDef))
        {
            // The isolated executor returns boxed ReadOnlyRecordBuffers (non-generic
            // IEnumerable) so its assembly carries no Ncl type in its public surface; cast
            // back here, where QueryProjection.cs already (necessarily) references the type.
            // #2137/#2146: when the query has any aggregated (Method = Sum/Count/Average/
            // Min/Max) column, ExecuteJoinQuery performs the implicit GROUP BY over the
            // joined rows itself (mirroring ProjectQueryRows' single-dataitem GROUP BY) —
            // see AlRunner.QueryJoin.JoinExecutor.BuildGroupedRows.
            var joined = ExecuteJoinQuery(metaAppObj).Cast<ReadOnlyRecordBuffer>();
            // Apply the live NavQuery's runtime filters (SetRange/SetFilter) as a POST-projection
            // pass. The single-dataitem path pushes these into the temp provider's WHERE
            // (TranslateQueryFilters); the join executor reads each dataitem's table with only its
            // STATIC metadata filters, so runtime filters must be evaluated against the projected
            // rows here. Without this the join returns UNFILTERED rows (a correctness bug). Because
            // the join rows are already GROUPED/aggregated by the time they reach here, a filter on
            // an aggregated column is naturally evaluated against the aggregated RESULT — i.e. this
            // one pass already gives HAVING semantics for the join path, with no separate throw
            // needed (unlike the single-dataitem path, which pushes non-aggregated filters into the
            // WHERE clause BEFORE aggregation and so needs havingFilters kept out of that push-down
            // — see TranslateQueryFilters/ApplyHavingFilters below). Done before Top so the cap
            // applies to the filtered set, matching SQL TOP-after-WHERE/HAVING.
            joined = ApplyJoinRuntimeFilters(metaAppObj, queryDef, request, joined);
            var topJ = _pReqTopNumberOfRows!.GetValue(request);
            int topNJ = topJ == null ? 0 : Convert.ToInt32(topJ);
            return topNJ > 0 ? joined.Take(topNJ) : joined;
        }

        // Query.TopNumberOfRowsToReturn caps the dataset. NavQuery passes it through the
        // request's TopNumberOfRowsToReturn; the temp provider's Find only honours
        // FindType.FirstOnly (Take(1)), never the Top cap — that's a query concept the SQL
        // provider would enforce via TOP. Applied AFTER projection (not on the raw `rows`
        // here) so an aggregate query's TOP caps the number of GROUPS, matching SQL's
        // TOP-after-GROUP-BY — capping the raw rows first would silently drop rows out of
        // a group before they're ever summed/counted/averaged.
        var top = _pReqTopNumberOfRows!.GetValue(request);
        int topN = top == null ? 0 : Convert.ToInt32(top);
        var projected = ProjectQueryRows(metaAppObj, rows);
        // #2146: HAVING-clause filters (runtime SetRange/SetFilter on an aggregated column)
        // are evaluated here, against the already-aggregated per-group result — never against
        // the raw pre-aggregation row. Applied AFTER grouping/aggregation, BEFORE Top, matching
        // SQL's WHERE → GROUP BY → HAVING → TOP order.
        projected = ApplyHavingFilters(metaAppObj, havingFilters, projected);
        return topN > 0 ? projected.Take(topN) : projected;
    }

    /// <summary>
    /// Apply HAVING-clause filters (runtime SetRange/SetFilter on an aggregated column,
    /// extracted by TranslateQueryFilters) against the already-projected/aggregated rows.
    /// Each filter's FilterExpression is evaluated with BC's own
    /// <c>FilterExpression.Evaluate(NavValue, ISortingRulesProvider)</c> against the NavValue
    /// in the column's OWN result slot (NCLMetaQueryColumn.ColumnIndex) — i.e. the aggregated
    /// value, not a raw row value. A row (group) failing any filter is dropped, matching SQL's
    /// "HAVING drops groups that don't satisfy the condition".
    /// </summary>
    private static IEnumerable<ReadOnlyRecordBuffer> ApplyHavingFilters(
        object nclMetaQuery, List<(object Column, object Expr)> havingFilters, IEnumerable<ReadOnlyRecordBuffer> rows)
    {
        if (havingFilters.Count == 0) return rows;
        var session = TryGetCurrentSession(nclMetaQuery);
        var conds = havingFilters
            .Select(hf => (slot: ((NCLMetaQueryColumn)hf.Column).ColumnIndex, expr: hf.Expr))
            .ToList();
        return rows.Where(row =>
        {
            foreach (var (slot, expr) in conds)
            {
                if (slot < 0 || slot >= row.FieldCount)
                    throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        "NavQuery.SetRange/SetFilter on an aggregated column",
                        $"query-having-filter-on-nonprojected-column — filtered slot {slot} is outside the " +
                        $"projected row (FieldCount {row.FieldCount}); cannot evaluate HAVING; see docs/scope.md");
                if (!EvaluateFilterExpression(expr, row[slot], session))
                    return false;
            }
            return true;
        });
    }

    private static MethodInfo? _mFilterExprEvaluate;

    /// <summary>
    /// Apply the live NavQuery's runtime filters (the request's FiltersAndMarks, keyed by
    /// NCLMetaQueryColumn) to the already-projected join rows. Each filter's FilterExpression
    /// is evaluated — using BC's own <c>FilterExpression.Evaluate(NavValue, ISortingRulesProvider)</c>,
    /// so range / &lt;&gt; / &amp; / | semantics match real BC exactly — against the NavValue in the
    /// column's projection slot (NCLMetaQueryColumn.ColumnIndex). Rows failing any filter are
    /// dropped. If a filtered column is NOT projected (no result slot, ColumnIndex &lt; 0, or out
    /// of range) we cannot evaluate it post-projection, so we throw RunnerOutOfScopeException
    /// rather than silently return wrong rows (loud-failures rule).
    /// </summary>
    private static IEnumerable<ReadOnlyRecordBuffer> ApplyJoinRuntimeFilters(
        object nclMetaQuery, object queryDef, object request, IEnumerable<ReadOnlyRecordBuffer> rows)
    {
        EnsureFilterReflection();
        var fam = request.GetType().GetProperty("FiltersAndMarks", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(request);
        if (fam == null) return rows;
        var filters = _tFiltersAndMarks!.GetProperty("Filters", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(fam);
        if (filters == null) return rows;
        var items = (Array?)_tFilterFieldDictionary!.GetProperty("Items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(filters);
        if (items == null || items.Length == 0) return rows; // no runtime filters → unchanged.

        // Build (projectionSlot, FilterExpression) pairs.
        //
        // NCLMetaQueryColumn.ColumnIndex CANNOT distinguish a filter-only column (one declared
        // only via `filter(...)`, or a bare join-key field with no declared column at all — see
        // Query 777's own "User Security ID") from a genuinely-projected column at slot 0: BC's
        // own runtime ctor only assigns ColumnIndex when the column isn't FilterOnly, so a
        // filter-only column's ColumnIndex is left at the CLR default (0) — the same value a
        // real slot-0 column has. Reading it naively made every runtime filter on a filter-only
        // column alias onto whatever real column happened to land in slot 0, so a Guid-typed
        // filter (Query 777's "User Security ID") got compared against an unrelated Integer
        // column's value and threw NavNCLInvalidComparisonException instead of ever being
        // evaluated as a filter. ComputeJoinColumnSlotMap gives filter-only columns their OWN
        // dedicated extra slots (mirroring the ones JoinExecutor.BuildJoinProjectionPlan already
        // populates them into), so this now evaluates against the real filtered value.
        var slotMap = ComputeJoinColumnSlotMap(queryDef);
        var conds = new List<(int slot, object expr)>();
        foreach (var item in items)
        {
            // Tuple<INavFieldMetadata, FilterExpression>
            var key = item!.GetType().GetProperty("Item1")!.GetValue(item);
            var expr = item.GetType().GetProperty("Item2")!.GetValue(item);
            if (expr == null) continue;
            if (key == null || _tNCLMetaQueryColumn == null || !_tNCLMetaQueryColumn.IsInstanceOfType(key))
                // A non-query-column key on a query request should not occur; refuse to guess.
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    "NavQuery (multi-dataitem join)",
                    "query-join-runtime-filter-on-nonprojected-column — a runtime filter is keyed by a " +
                    $"non-query-column ({key?.GetType().Name ?? "null"}); cannot evaluate post-projection; see docs/scope.md");
            if (!slotMap.TryGetValue(key, out var slot))
                // The filtered column isn't in ANY dataitem's QueryColumns of this query
                // definition — should not occur (the filter dictionary is keyed by columns that
                // came from this same query), but refuse to guess rather than silently drop it.
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    "NavQuery (multi-dataitem join)",
                    "query-join-runtime-filter-unresolved-column — a runtime filter's column could not be " +
                    "located in the query's own DataItems/QueryColumns; see docs/scope.md");
            conds.Add((slot, expr));
        }
        if (conds.Count == 0) return rows;

        var session = TryGetCurrentSession(nclMetaQuery);
        return rows.Where(row =>
        {
            foreach (var (slot, expr) in conds)
            {
                if (slot >= row.FieldCount)
                    throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        "NavQuery (multi-dataitem join)",
                        $"query-join-runtime-filter-on-nonprojected-column — filtered slot {slot} is outside the " +
                        $"projected row (FieldCount {row.FieldCount}); cannot evaluate post-projection; see docs/scope.md");
                var navValue = row[slot];
                if (!EvaluateFilterExpression(expr, navValue, session))
                    return false;
            }
            return true;
        });
    }

    /// <summary>Invoke BC's FilterExpression.Evaluate(NavValue, ISortingRulesProvider) by reflection.</summary>
    private static bool EvaluateFilterExpression(object expr, object navValue, object? sortingRules)
    {
        _mFilterExprEvaluate ??= _tFilterExpr!.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("FilterExpression.Evaluate(NavValue, ISortingRulesProvider) not found");
        return (bool)_mFilterExprEvaluate.Invoke(expr, new[] { navValue, sortingRules })!;
    }

    private static PropertyInfo? _pNavCurrentThreadSession;
    private static bool _navCurrentThreadResolved;

    /// <summary>
    /// The current NavSession (which implements ISortingRulesProvider) — the same sorting-rules
    /// provider BC passes to FilterExpression.Evaluate on the real WHERE path. Used only by the
    /// Text/Code-collation comparison branch of FilterExpressionContext.Compare; null is tolerated
    /// for numeric/integer comparisons. Resolved via NavCurrentThread.Session.
    /// </summary>
    private static object? TryGetCurrentSession(object anyNclTyped)
    {
        if (!_navCurrentThreadResolved)
        {
            _navCurrentThreadResolved = true;
            var nclAsm = anyNclTyped.GetType().Assembly;
            var tNavCurrentThread = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread");
            _pNavCurrentThreadSession = tNavCurrentThread?.GetProperty("Session",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        }
        try { return _pNavCurrentThreadSession?.GetValue(null); }
        catch { return null; }
    }

    // Cached per NCLMetaQuery: which column goes in which result slot, whether it's backed
    // by a source table field, and (issue #2137) its aggregation method.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ProjectionPlan> _projectionPlans = new();

    private sealed class ColumnPlan
    {
        public int QuerySlot;
        // -1 means "no source table field to read" (a ConstValue column, or a column whose
        // SourceTableField resolution failed) → leave the slot at its NavValue default.
        public int TableSlot;
        public AggregationType Aggregation;
        // The implicit GROUP BY key: a genuinely-projected (Normal), non-aggregated column.
        public bool IsGroupKey;
        // The NCLMetaQueryColumn itself — also an INavValueMetadata, so it can be handed
        // straight to NavValue.CreateNavValueFromObject / FlowFieldPatches.TypedDefaultForField
        // to produce a value of the COLUMN's own declared type, not the source field's.
        public NCLMetaQueryColumn Column = null!;
        // #2300: non-null when SourceTableField.FieldClass == FlowField. A FlowField has no
        // stored slot in the table's row buffer (TableSlot's ColumnIndex points at storage that
        // was never written), so BuildRow computes it directly via FlowFieldPatches instead of
        // reading the buffer, the same way BC's own query engine computes it via a synthesized
        // OuterApply sub-query this runner has no SQL to execute.
        public NCLMetaField? FlowFieldMeta;
    }

    private sealed class ProjectionPlan
    {
        public int SlotCount;
        public ColumnPlan[] Columns = Array.Empty<ColumnPlan>();
        // True when at least one column has Method = Sum/Count/Average/Min/Max, i.e. this
        // query has an implicit GROUP BY over its other (non-aggregated) Normal columns.
        public bool HasAggregate;
    }

    private static IEnumerable<ReadOnlyRecordBuffer> ProjectQueryRows(object nclMetaQuery, IEnumerable<ReadOnlyRecordBuffer> rows)
    {
        var plan = _projectionPlans.GetValue(nclMetaQuery, BuildProjectionPlan);

        if (!plan.HasAggregate)
        {
            // No Method column anywhere in this query → every row projects independently,
            // exactly as before #2137 (this is the 99% case: a plain, non-aggregated query).
            foreach (var row in rows)
                yield return BuildRow(nclMetaQuery, plan, new[] { row });
            yield break;
        }

        // #2137: at least one aggregated column → an implicit GROUP BY over every OTHER
        // Normal (non-aggregated) column, mirroring BC's compiled SQL SELECT ... GROUP BY.
        // Grouping needs to see every candidate row before it can know the groups, so the
        // filtered/sorted source rows are materialised once here.
        var sourceRows = rows as IReadOnlyList<ReadOnlyRecordBuffer> ?? rows.ToList();
        var groupKeyColumns = plan.Columns.Where(c => c.IsGroupKey).ToArray();

        if (groupKeyColumns.Length == 0)
        {
            // Scalar aggregate (no non-aggregated column at all) — SQL's "GROUP BY ()" always
            // produces exactly one group, even over zero source rows (SUM/COUNT/AVERAGE then
            // default to 0; MIN/MAX to the column's own typed default — see ComputeAggregate).
            yield return BuildRow(nclMetaQuery, plan, sourceRows);
            yield break;
        }

        // Group by the group-key columns' source values, preserving first-seen order (the
        // filtered/sorted row order the temp provider already produced — the closest faithful
        // approximation available without a real SQL engine's own GROUP BY ordering).
        var groups = new Dictionary<GroupKey, List<ReadOnlyRecordBuffer>>();
        var groupOrder = new List<GroupKey>();
        foreach (var row in sourceRows)
        {
            var key = new GroupKey(groupKeyColumns.Select(c =>
                c.TableSlot >= 0 && c.TableSlot < row.FieldCount ? row[c.TableSlot] : null).ToArray());
            if (!groups.TryGetValue(key, out var groupRows))
            {
                groupRows = new List<ReadOnlyRecordBuffer>();
                groups[key] = groupRows;
                groupOrder.Add(key);
            }
            groupRows.Add(row);
        }

        foreach (var key in groupOrder)
            yield return BuildRow(nclMetaQuery, plan, groups[key]);
    }

    /// <summary>
    /// The implicit GROUP BY key: the group-key columns' source NavValues, compared by
    /// NavValue's own IEquatable&lt;NavValue&gt;/GetHashCode — the same value equality BC's
    /// record buffers already trust, not a re-derived one.
    /// </summary>
    private readonly struct GroupKey : IEquatable<GroupKey>
    {
        private readonly NavValue?[] _values;
        public GroupKey(NavValue?[] values) => _values = values;

        public bool Equals(GroupKey other)
        {
            if (_values.Length != other._values.Length) return false;
            for (int i = 0; i < _values.Length; i++)
            {
                var a = _values[i]; var b = other._values[i];
                if (ReferenceEquals(a, b)) continue;
                if (a is null || b is null) return false;
                if (!a.Equals(b)) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is GroupKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var v in _values) hash.Add(v?.GetHashCode() ?? 0);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Project one output row from <paramref name="groupRows"/> — either a single raw row
    /// (the non-aggregate path calls this with a one-row "group") or every row sharing one
    /// GROUP BY key (the aggregate path). Non-aggregated columns read the first row's source
    /// value (every row in a real group shares it by construction); aggregated columns compute
    /// over the whole group via ComputeAggregate.
    /// </summary>
    private static ReadOnlyRecordBuffer BuildRow(object nclMetaQuery, ProjectionPlan plan, IReadOnlyList<ReadOnlyRecordBuffer> groupRows)
    {
        var fields = new object?[plan.SlotCount];
        foreach (var c in plan.Columns)
        {
            if (c.Aggregation != AggregationType.None)
            {
                fields[c.QuerySlot] = ComputeAggregate(c, groupRows);
                continue;
            }
            // #2300: a non-aggregated FlowField column — compute directly rather than reading
            // the (never-written) buffer slot. Method=Sum/etc. on a FlowField SOURCE column is
            // routed through ComputeAggregate above instead, which still reads the buffer slot;
            // that combination is unmeasured (no oracle case covers it) and left as a documented
            // follow-up rather than guessed at here.
            if (c.FlowFieldMeta != null && groupRows.Count > 0)
            {
                fields[c.QuerySlot] = FlowFieldPatches.CalcOneFlowFieldForQueryRow(groupRows[0], c.FlowFieldMeta);
                continue;
            }
            if (c.TableSlot < 0 || groupRows.Count == 0 || c.TableSlot >= groupRows[0].FieldCount)
                continue; // unsupported column (ConstValue) → leave at NavValue default
            fields[c.QuerySlot] = groupRows[0][c.TableSlot];
        }
        // ReadOnlyRecordBuffer(NCLMetaApplicationObject, params NavValue[])
        return (ReadOnlyRecordBuffer)_ctorReadOnlyRecordBuffer!.Invoke(
            new object?[] { nclMetaQuery, ToNavValueArray(fields) })!;
    }

    /// <summary>
    /// Compute one aggregated column's value over its group (single-dataitem path: a group is
    /// a set of raw ReadOnlyRecordBuffer rows sharing the GROUP BY key).
    /// </summary>
    private static NavValue? ComputeAggregate(ColumnPlan c, IReadOnlyList<ReadOnlyRecordBuffer> groupRows)
    {
        IEnumerable<NavValue?> SourceValues()
        {
            foreach (var row in groupRows)
            {
                if (c.TableSlot < 0 || c.TableSlot >= row.FieldCount) continue;
                yield return row[c.TableSlot];
            }
        }
        return ComputeAggregateCore(c.Aggregation, c.Column, groupRows.Count, SourceValues());
    }

    /// <summary>
    /// Compute one aggregated column's value given its aggregation method, its OWN
    /// NCLMetaQueryColumn (for CreateNavValueFromObject / TypedDefaultForField, so the result
    /// carries the column's declared type — which can differ from the source field's, e.g. an
    /// Average over an Integer field is typically Decimal), the number of raw rows in the group
    /// (Count's answer — independent of any particular column's value), and the column's own
    /// SOURCE values across those rows (Sum/Average/Min/Max operate over these; a missing/null
    /// value is skipped, same as a NULL column value is excluded from a SQL aggregate).
    ///
    /// Shared by the single-dataitem path (ComputeAggregate above, over ReadOnlyRecordBuffer
    /// rows) and the isolated AlRunner.QueryJoin.JoinExecutor (over combo values gathered from
    /// joined rows, via Join_ComputeAggregate in RecordPatches.QueryJoin.cs) — the join
    /// executor cannot share IReadOnlyList&lt;ReadOnlyRecordBuffer&gt; across the assembly
    /// isolation boundary (see RecordPatches.QueryJoin.cs's header comment), so it calls this
    /// core with an already-extracted value list instead.
    ///
    /// Sum/Average mirror RecordPatches.cs's TempTableDataProvider_CalcNumeric (Decimal18
    /// checked-arithmetic, no manual int/long coercion — the same FlowField aggregation
    /// pattern, just over query rows instead of a CalcFormula source table). Min/Max reuse
    /// FlowFieldPatches.NavValueCompare/TypedDefaultForField rather than re-deriving
    /// comparison/default semantics a second time.
    /// </summary>
    private static NavValue? ComputeAggregateCore(AggregationType aggregation, NCLMetaQueryColumn column, int rowCountInGroup, IEnumerable<NavValue?> sourceValues)
    {
        switch (aggregation)
        {
            case AggregationType.Count:
                return NavValue.CreateNavValueFromObject(column, rowCountInGroup);

            case AggregationType.Sum:
            case AggregationType.Average:
            {
                Decimal18 sum = default;
                int n = 0;
                foreach (var v in sourceValues)
                {
                    if (v == null) continue;
                    sum = checked(sum + v.ToDecimal());
                    n++;
                }
                return aggregation == AggregationType.Average
                    ? NavValue.CreateNavValueFromObject(column, n > 0 ? sum / n : (Decimal18)0m)
                    : NavValue.CreateNavValueFromObject(column, sum);
            }

            case AggregationType.Min:
            case AggregationType.Max:
            {
                NavValue? best = null;
                foreach (var v in sourceValues)
                {
                    if (v == null) continue;
                    if (best == null
                        || (aggregation == AggregationType.Min && FlowFieldPatches.NavValueCompare(v, best) < 0)
                        || (aggregation == AggregationType.Max && FlowFieldPatches.NavValueCompare(v, best) > 0))
                        best = v;
                }
                // No row in the group (only reachable via the zero-row scalar-aggregate case)
                // → the column's own typed default (0 / '' / 0D / …), never a bare literal.
                return best ?? FlowFieldPatches.TypedDefaultForField(column);
            }

            default:
                return null; // AggregationType.None is handled by the caller, not here.
        }
    }

    /// <summary>
    /// Adapter for AlRunner.QueryJoin.JoinContext.ComputeAggregate: the isolated JoinExecutor
    /// gathers one raw NavValue (boxed, or null) per row in the group and calls this so the
    /// aggregation math itself is not duplicated across the assembly boundary. See
    /// ComputeAggregateCore for the shared logic.
    /// </summary>
    private static object? Join_ComputeAggregate(object columnObj, object?[] rawValues)
    {
        var column = (NCLMetaQueryColumn)columnObj;
        var values = rawValues.Select(v => (NavValue?)v);
        return ComputeAggregateCore(column.AggregationType, column, rawValues.Length, values);
    }

    private static Type? _tNavValue;
    private static Array ToNavValueArray(object?[] values)
    {
        _tNavValue ??= _tReadOnlyRecordBuffer!.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavValue")!;
        var arr = Array.CreateInstance(_tNavValue, values.Length);
        for (int i = 0; i < values.Length; i++) arr.SetValue(values[i], i);
        return arr;
    }

    private static PropertyInfo? _pDataItemSourceFlowField;
    private static bool _sourceFlowFieldResolved;

    /// <summary>
    /// #2300: NCLMetaQueryDataItem.SourceFlowField (internal — reflection required, same as
    /// every other internal Ncl member this file already reaches this way). Non-null on the
    /// synthesized FlowField-calculation sub-dataitem BC's own NCLMetaQuery.
    /// CreateSubQueryForFlowFieldCalculation builds; identifies WHICH FlowField NCLMetaField
    /// this dataitem's own aggregate result column stands in for.
    /// </summary>
    private static NCLMetaField? GetSourceFlowField(object dataItem)
    {
        if (!_sourceFlowFieldResolved)
        {
            _sourceFlowFieldResolved = true;
            _pDataItemSourceFlowField = dataItem.GetType().GetProperty("SourceFlowField",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        return _pDataItemSourceFlowField?.GetValue(dataItem) as NCLMetaField;
    }

    private static ProjectionPlan BuildProjectionPlan(object nclMetaQuery)
    {
        var queryDef = (NCLMetaQueryDefinition)_tNCLMetaQuery!.GetProperty("QueryDefinition", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(nclMetaQuery)!;

        var columnPlans = new List<ColumnPlan>();
        int maxSlot = -1;
        bool hasAggregate = false;
        foreach (var col in queryDef.Columns) // excludes FilterOnly columns already
        {
            int querySlot = col.ColumnIndex;
            if (querySlot > maxSlot) maxSlot = querySlot;

            int tableSlot = -1;
            NCLMetaField? flowFieldMeta = null;

            // #2300: a column whose dataitem is a FlowField-calculation synthesized subquery
            // (SUB$<dataitem>$<column>, DataItemLinkType.OuterApply) represents the OUTER
            // "TotalAmount"-style result — the very column AL's compiled Id/ColumnIndex expect
            // (CreateSubQueryForFlowFieldCalculation builds it with flowFieldColumn.Id/
            // .QueryColumnIndex verbatim). ITS OWN AggregationType is Sum/Count/etc — BC's
            // SQL groups it away inside the sub-query, invisibly to the outer projection — and
            // its SourceTableField resolves to the SOURCE field on the SUB-QUERY's OWN inner
            // table (e.g. "Qff Line".Amount), not a field on THIS row's table at all. Reading
            // that field's ColumnIndex against the CURRENT (outer-table) row buffer is exactly
            // the #2300 corruption: whatever real, unrelated field happens to sit at that slot
            // on the OUTER table (observed: the table's own SystemId, a Guid) gets returned
            // instead. This must be checked and handled BEFORE the generic aggregation/
            // TableSlot branches below, which would otherwise take it first.
            var sourceFlowField = GetSourceFlowField(col.ParentDataItem);
            if (sourceFlowField != null)
            {
                flowFieldMeta = sourceFlowField;
            }
            else
            {
                // SourceTableField throws NotSupportedException for a ConstValue column (no
                // source field at all — pre-existing, documented follow-up, unrelated to #2137);
                // treat that as "unsupported → leave at default", same as before this fix.
                try
                {
                    if (col.ColumnType != QueryColumnType.ConstValue)
                    {
                        var srcField = col.SourceTableField;
                        if (srcField != null) tableSlot = srcField.ColumnIndex;
                    }
                }
                catch { tableSlot = -1; }
            }

            var aggregation = col.AggregationType;
            // A FlowField-calculation column is computed whole (FlowFieldPatches.
            // CalcOneFlowFieldForQueryRow), independent of the runner's OWN #2137 GROUP BY —
            // its Sum/Count/etc AggregationType describes what BC's SQL sub-query does
            // INTERNALLY, not an aggregation this projection layer must additionally perform.
            if (flowFieldMeta == null && aggregation != AggregationType.None) hasAggregate = true;

            columnPlans.Add(new ColumnPlan
            {
                QuerySlot = querySlot,
                TableSlot = tableSlot,
                Aggregation = flowFieldMeta == null ? aggregation : AggregationType.None,
                IsGroupKey = flowFieldMeta == null && aggregation == AggregationType.None && col.ColumnType == QueryColumnType.Normal,
                Column = col,
                FlowFieldMeta = flowFieldMeta,
            });
        }
        return new ProjectionPlan { SlotCount = maxSlot + 1, Columns = columnPlans.ToArray(), HasAggregate = hasAggregate };
    }
}
