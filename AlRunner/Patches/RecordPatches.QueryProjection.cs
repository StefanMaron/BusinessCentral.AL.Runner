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
//   stays a follow-up. An aggregate / const / non-source column has no SourceTableField
//   and is left at its slot default rather than faked — surfaced as a follow-up, never
//   silently wrong for source columns.
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

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
        var execRequest = TranslateQueryFilters(request);
        var raw = (IEnumerable<ReadOnlyRecordBuffer>)_mTtdpFindImpl!.Invoke(self, new[] { execRequest })!;
        raw = ApplyFirstOnly(request, raw);
        return ProjectIfQuery(request, raw);
    }

    /// <summary>
    /// Replacement for TempTableDataProvider.FindFromPosition(PositionedFindProviderRequest, Func&lt;bool&gt;).
    /// </summary>
    public static IEnumerable<ReadOnlyRecordBuffer> TempTableDataProvider_FindFromPosition(
        object self, object request, Func<bool>? onlyCurrentKeyNeededForNextRow)
    {
        EnsureQueryProjectionReflection(self);
        var execRequest = TranslateQueryFilters(request);
        var raw = (IEnumerable<ReadOnlyRecordBuffer>)_mTtdpFindByPositionImpl!.Invoke(self, new[] { execRequest })!;
        raw = ApplyFirstOnly(request, raw);
        return ProjectIfQuery(request, raw);
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

        var includedTables = (System.Collections.IEnumerable)_pQueryDefIncludedTables!.GetValue(queryDefinition)!;
        var tableList = includedTables.Cast<object>().ToList();

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

    private static void EnsureGetDataAccessForQueryReflection(object dataAccessSource)
    {
        if (_pQueryDefIncludedTables != null) return;
        var nclAsm = dataAccessSource.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var tQueryDef = nclAsm.GetType(rt + "NCLMetaQueryDefinition")!;
        _pQueryDefIncludedTables = tQueryDef.GetProperty("IncludedTables",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NCLMetaQueryDefinition.IncludedTables not found");
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
    /// </summary>
    private static object TranslateQueryFilters(object request)
    {
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
        // Other expression kinds (wildcard/fieldEqualsField/etc.) are not produced by
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

    private static IEnumerable<ReadOnlyRecordBuffer> ProjectIfQuery(object request, IEnumerable<ReadOnlyRecordBuffer> rows)
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
            var joined = ExecuteJoinQuery(metaAppObj).Cast<ReadOnlyRecordBuffer>();
            // Apply the live NavQuery's runtime filters (SetRange/SetFilter) as a POST-projection
            // pass. The single-dataitem path pushes these into the temp provider's WHERE
            // (TranslateQueryFilters); the join executor reads each dataitem's table with only its
            // STATIC metadata filters, so runtime filters must be evaluated against the projected
            // rows here. Without this the join returns UNFILTERED rows (a correctness bug). Done
            // before Top so the cap applies to the filtered set, matching SQL TOP-after-WHERE.
            joined = ApplyJoinRuntimeFilters(metaAppObj, queryDef, request, joined);
            var topJ = _pReqTopNumberOfRows!.GetValue(request);
            int topNJ = topJ == null ? 0 : Convert.ToInt32(topJ);
            return topNJ > 0 ? joined.Take(topNJ) : joined;
        }

        // Query.TopNumberOfRowsToReturn caps the dataset. NavQuery passes it through the
        // request's TopNumberOfRowsToReturn; the temp provider's Find only honours
        // FindType.FirstOnly (Take(1)), never the Top cap — that's a query concept the SQL
        // provider would enforce via TOP. Apply it here, scoped to query requests so
        // ordinary table reads keep BC's exact (uncapped) behaviour.
        var top = _pReqTopNumberOfRows!.GetValue(request);
        int topN = top == null ? 0 : Convert.ToInt32(top);
        if (topN > 0) rows = rows.Take(topN);

        return ProjectQueryRows(metaAppObj, rows);
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

    // Cached per NCLMetaQuery: the (queryColumnIndex -> tableFieldColumnIndex) projection map
    // and the result slot count.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ProjectionPlan> _projectionPlans = new();

    private sealed class ProjectionPlan
    {
        public int SlotCount;
        // map[i] = (queryResultSlot, tableFieldSlot); a value of -1 tableFieldSlot means
        // the column has no NCLMetaField source (aggregate/const) → leave at default.
        public (int querySlot, int tableSlot)[] Map = Array.Empty<(int, int)>();
    }

    private static IEnumerable<ReadOnlyRecordBuffer> ProjectQueryRows(object nclMetaQuery, IEnumerable<ReadOnlyRecordBuffer> rows)
    {
        var plan = _projectionPlans.GetValue(nclMetaQuery, BuildProjectionPlan);
        foreach (var row in rows)
        {
            var fields = new object?[plan.SlotCount];
            foreach (var (querySlot, tableSlot) in plan.Map)
            {
                if (tableSlot < 0 || tableSlot >= row.FieldCount) continue; // unsupported column → default
                fields[querySlot] = row[tableSlot];
            }
            // ReadOnlyRecordBuffer(NCLMetaApplicationObject, params NavValue[])
            yield return (ReadOnlyRecordBuffer)_ctorReadOnlyRecordBuffer!.Invoke(
                new object?[] { nclMetaQuery, ToNavValueArray(fields) });
        }
    }

    private static Type? _tNavValue;
    private static Array ToNavValueArray(object?[] values)
    {
        _tNavValue ??= _tReadOnlyRecordBuffer!.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavValue")!;
        var arr = Array.CreateInstance(_tNavValue, values.Length);
        for (int i = 0; i < values.Length; i++) arr.SetValue(values[i], i);
        return arr;
    }

    private static ProjectionPlan BuildProjectionPlan(object nclMetaQuery)
    {
        // queryDef = nclMetaQuery.QueryDefinition; columns = queryDef.Columns
        var queryDef = _tNCLMetaQuery!.GetProperty("QueryDefinition", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(nclMetaQuery)!;
        var columns = (IEnumerable)queryDef.GetType()
            .GetProperty("Columns", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(queryDef)!;

        var map = new List<(int, int)>();
        int maxSlot = -1;
        foreach (var col in columns)
        {
            var ct = col.GetType(); // NCLMetaQueryColumn
            int querySlot = (int)ct.GetProperty("ColumnIndex", BindingFlags.Public | BindingFlags.Instance)!.GetValue(col)!;
            if (querySlot > maxSlot) maxSlot = querySlot;

            int tableSlot = -1;
            // SourceTableField is the NCLMetaField backing this column (null/throws for
            // aggregate/const columns — treat those as unsupported → leave default).
            try
            {
                var srcField = ct.GetProperty("SourceTableField", BindingFlags.Public | BindingFlags.Instance)!.GetValue(col);
                if (srcField != null)
                    tableSlot = (int)srcField.GetType().GetProperty("ColumnIndex", BindingFlags.Public | BindingFlags.Instance)!.GetValue(srcField)!;
            }
            catch { tableSlot = -1; }
            map.Add((querySlot, tableSlot));
        }
        return new ProjectionPlan { SlotCount = maxSlot + 1, Map = map.ToArray() };
    }
}
