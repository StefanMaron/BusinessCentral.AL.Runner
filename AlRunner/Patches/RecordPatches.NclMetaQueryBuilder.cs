// RecordPatches.NclMetaQueryBuilder — build a REAL NCLMetaQuery (with a populated
// QueryDefinition) for a query id, so the genuine async query engine
// (NavQuery.FindDataImplAsync → DataAccessSource.GetDataAccessForQuery →
// GetDataAccessForTable, already routed to the in-memory provider) executes
// against the in-memory table data instead of NRE-ing on a null NCLMetaQuery.
//
// Mechanism: construct a BC `MetaQuery` design object programmatically (its
// MetaQuery* design types have parameterless ctors + public settable
// properties), then call the PUBLIC static NCLMetaQuery.CreateDynamicQuery(
// ApplicationObjectId, MetaQuery, Type clrType, NavAppGroup) which runs
// PopulateDesignedQuery → ResolveColumnTypes (via the hooked GetMetaTableById)
// → ParseMetadata (fills the queryDefinition LazyEx that otherwise throws
// "cannot be read before calling ParseMetadata").
//
// SPIKE STAGE: query 60022 (corpus "ALT Universal Query") is hardcoded to prove
// the engine runs end-to-end on the skeleton. Generalised to a parsed-query
// builder + precompiled-query support in later tasks.
using System.Collections;
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _realMetaQueryCache = new();

    // Reflection handles for the MetaQuery design model + CreateDynamicQuery.
    private static Type? _tMetaQuery;
    private static Type? _tMetaQueryDataItem;
    private static Type? _tMetaQueryColumn;
    private static Type? _tMetaQueryOrderBy;
    private static Type? _tMetaQueryDataItemLink;
    private static MethodInfo? _mCreateDynamicQuery;

    private static void QLog(string msg)
    {
        if (Environment.GetEnvironmentVariable("AL_RUNNER_QDIAG") != "1") return;
        try { System.IO.File.AppendAllText("/tmp/qdiag.txt", "[NclMetaQueryBuilder] " + msg + "\n"); } catch { }
    }

    private static void EnsureQueryBuilderReflection()
    {
        if (_tMetaQuery != null && _mCreateDynamicQuery != null) return;
        EnsureFormReportReflection();
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        const string md = "Microsoft.Dynamics.Nav.Types.Metadata.";
        _tMetaQuery = typesAsm?.GetType(md + "MetaQuery");
        _tMetaQueryDataItem = typesAsm?.GetType(md + "MetaQueryDataItem");
        _tMetaQueryColumn = typesAsm?.GetType(md + "MetaQueryColumn");
        _tMetaQueryOrderBy = typesAsm?.GetType(md + "MetaQueryOrderBy");
        _tMetaQueryDataItemLink = typesAsm?.GetType(md + "MetaQueryDataItemLink");

        // public static NCLMetaQuery CreateDynamicQuery(ApplicationObjectId, MetaQuery, Type, NavAppGroup)
        if (_tNCLMetaQuery != null)
            _mCreateDynamicQuery = _tNCLMetaQuery.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateDynamicQuery" && m.GetParameters().Length == 4);
    }

    /// <summary>Set a property, coercing an int/string to the property's enum type when needed.</summary>
    private static void SetProp(object obj, string name, object? value)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{obj.GetType().Name}.{name} not found");
        var pt = p.PropertyType;
        if (value != null && pt.IsEnum && value is string s) value = Enum.Parse(pt, s);
        else if (value != null && pt.IsEnum) value = Enum.ToObject(pt, value);
        p.SetValue(obj, value);
    }

    private static IList GetList(object obj, string name)
        => (IList)obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(obj)!;

    /// <summary>
    /// Build (and cache) a real NCLMetaQuery for the given query id, or null if it
    /// cannot be built (caller falls back to the existing null-metaquery behaviour).
    /// </summary>
    internal static object? BuildRealNCLMetaQuery(int queryId, Type clrType)
    {
        return _realMetaQueryCache.GetOrAdd(queryId, _ => BuildRealNCLMetaQueryCore(queryId, clrType));
    }

    private static object? BuildRealNCLMetaQueryCore(int queryId, Type clrType)
    {
        EnsureQueryBuilderReflection();
        if (_tMetaQuery == null || _tMetaQueryDataItem == null || _tMetaQueryColumn == null
            || _mCreateDynamicQuery == null || _tApplicationObjectId == null
            || _tObjectTypeEnum == null || _tNCLMetaQuery == null)
        {
            QLog($"BuildRealNCLMetaQuery({queryId}): reflection unavailable " +
                $"(mq={_tMetaQuery != null}, di={_tMetaQueryDataItem != null}, col={_tMetaQueryColumn != null}, " +
                $"create={_mCreateDynamicQuery != null}, appObjId={_tApplicationObjectId != null}, " +
                $"objType={_tObjectTypeEnum != null}, nclMq={_tNCLMetaQuery != null})");
            return null;
        }

        try
        {
            var metaQuery = BuildMetaQueryDesign(queryId);
            if (metaQuery == null) { QLog($"BuildRealNCLMetaQuery({queryId}): no MetaQuery design (out of spike scope)"); return null; }

            var queryEnumVal = Enum.ToObject(_tObjectTypeEnum, 9); // ObjectType.Query
            var token = Activator.CreateInstance(_tApplicationObjectId, queryEnumVal, queryId);

            var meta = _mCreateDynamicQuery.Invoke(null,
                new object?[] { token, metaQuery, clrType, _baseAppGroup });
            QLog($"BuildRealNCLMetaQuery({queryId}): built {(meta == null ? "NULL" : meta.GetType().Name)} clrType={clrType.FullName}");
            return meta;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            QLog($"BuildRealNCLMetaQuery({queryId}) FAILED: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            return null;
        }
    }

    // Generic MetaQuery design builder, driven by the query's SymbolReference.json
    // definition (parsed by BcAppSymbolCache, indexed by BcAppFallback). Works for both
    // source-compiled queries (e.g. corpus 60022, symbols read from the bundle's own .app)
    // and precompiled BaseApp/SystemApp queries (e.g. 777, symbols from the dep .app).
    //
    // Column/filter Ids come VERBATIM from symbols (they are the BC-compiler-assigned ids
    // precompiled callers pass to NavQuery.ValidateExpectedType / GetColumnByNo). FieldNo is
    // resolved from the (field NAME → field no) map of the dataitem's RelatedTable. The
    // MetaQuery.DataItems list is FLAT (the join tree is reconstructed by the engine from
    // each dataitem's DataItemLinkType + DataItemLinks); the root dataitem has
    // DataItemLinkType=None and every nested dataitem carries its SqlJoinType + a
    // DataItemLink. QueryColumnIndex is assigned 0-based across all result (non-filter)
    // columns in dataitem order, matching what the projection layer expects.
    private static object? BuildMetaQueryDesign(int queryId)
    {
        var sym = TryGetQuerySymbol(queryId);
        if (sym == null) { QLog($"BuildMetaQueryDesign({queryId}): no SymbolReference query definition found in any registered .app"); return null; }

        // Pre-resolve every dataitem's (name → tableNo) so DataItemLink source-field
        // resolution works regardless of parent/child processing order.
        _dataItemTableNoByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in FlattenDataItems(sym.DataItems))
        {
            int tn = ResolveTableIdByName(d.RelatedTable);
            if (tn >= 0) _dataItemTableNoByName[d.Name] = tn;
        }

        var mq = Activator.CreateInstance(_tMetaQuery!)!;
        SetProp(mq, "Id", sym.Id);
        SetProp(mq, "Name", sym.Name);
        SetProp(mq, "ReadState", "ReadUncommitted");
        SetProp(mq, "QueryType", string.IsNullOrEmpty(sym.QueryType) ? "Normal" : sym.QueryType!);
        SetProp(mq, "TopNumberOfRowsToReturn", sym.TopNumberOfRowsToReturn);
        if (!string.IsNullOrEmpty(sym.Caption)) TrySetProp(mq, "Caption", sym.Caption);

        // Flatten the dataitem tree (root first, then nested) into the flat DataItems list.
        // resultColumnIndex is shared across all dataitems (filters do NOT consume a slot).
        int resultColumnIndex = 0;
        // (columnName/dataItemName → columnId) so OrderBy can map names → column ids.
        var columnIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        bool isRoot = true;
        foreach (var diSym in FlattenDataItems(sym.DataItems))
        {
            int tableNo = ResolveTableIdByName(diSym.RelatedTable);
            if (tableNo < 0)
            {
                QLog($"BuildMetaQueryDesign({queryId}): cannot resolve table '{diSym.RelatedTable}' for dataitem '{diSym.Name}' — abandoning build");
                return null;
            }
            var fieldNoByName = BuildFieldNameToNoMap(tableNo);

            var di = Activator.CreateInstance(_tMetaQueryDataItem!)!;
            SetProp(di, "DataItemName", diSym.Name);
            SetProp(di, "TableNo", tableNo);
            SetProp(di, "Id", diSym.Id);
            SetProp(di, "DataItemLinkType", isRoot ? "None" : MapSqlJoinType(diSym.SqlJoinType));
            SetProp(di, "Distinct", false);

            // Result columns.
            foreach (var col in diSym.Columns)
            {
                int fieldNo;
                if (string.IsNullOrEmpty(col.SourceColumn))
                {
                    // Issue #2137/#2150: real BC's compiler REJECTS a Method=Count column
                    // that names a source field at all (AL0353) -- the only valid AL is
                    // `column(X) { Method = Count; }`, with no field. NCLMetaQueryColumn.
                    // SourceTableField never reads FieldNo for a Count column anyway (BC's
                    // own Ncl.dll special-cases it to always return the table's OWN primary
                    // key field, regardless of what FieldNo says) -- the same fact
                    // ComputeAggregate's Count branch already relies on by never touching
                    // TableSlot. So a source-less column is legitimate ONLY when it is a
                    // Count column; abandoning the build for missing a field it was never
                    // going to use would silently make every Count-only query fail to
                    // build at all. FieldNo is a placeholder (0) that is never read for it.
                    if (col.Method != "Count")
                    {
                        QLog($"BuildMetaQueryDesign({queryId}): column '{col.Name}' has no SourceColumn and is not Method=Count — abandoning build");
                        return null;
                    }
                    fieldNo = 0;
                }
                else
                {
                    fieldNo = ResolveFieldNo(fieldNoByName, col.SourceColumn);
                    if (fieldNo < 0)
                    {
                        QLog($"BuildMetaQueryDesign({queryId}): field '{col.SourceColumn}' not found on table {tableNo} ('{diSym.RelatedTable}') — abandoning build");
                        return null;
                    }
                }
                AddColumn(di, id: col.Id, name: col.Name, fieldNo: fieldNo, index: resultColumnIndex++, caption: col.Caption, method: col.Method);
                columnIdByName[col.Name] = col.Id;
            }

            // Filter-only columns (dataitem filter(...) elements). They carry a real BC
            // column id and resolve to a source field, but FilterOnly=true and no result slot.
            foreach (var filt in diSym.Filters)
            {
                int fieldNo = ResolveFieldNo(fieldNoByName, filt.SourceColumn);
                if (fieldNo < 0)
                {
                    QLog($"BuildMetaQueryDesign({queryId}): filter field '{filt.SourceColumn}' not found on table {tableNo} — abandoning build");
                    return null;
                }
                AddFilterColumn(di, id: filt.Id, name: filt.Name, fieldNo: fieldNo);
                columnIdByName[filt.Name] = filt.Id;
            }

            // DataItemLink: "<thisField> = <SourceDataItem>.<sourceField>". The engine builds
            // CreateFieldEqualsField(SourceDataItemName, SourceFieldNo, DestinationFieldNo):
            //   DestinationFieldNo = field on THIS (child) table, SourceFieldNo = field on the
            //   referenced (parent) dataitem's table.
            if (!isRoot && !string.IsNullOrEmpty(diSym.DataItemLink))
            {
                var link = ParseDataItemLink(diSym.DataItemLink!, fieldNoByName);
                if (link == null)
                {
                    QLog($"BuildMetaQueryDesign({queryId}): could not parse DataItemLink '{diSym.DataItemLink}' for '{diSym.Name}' — abandoning build");
                    return null;
                }
                GetList(di, "DataItemLinks").Add(link);
            }

            GetList(mq, "DataItems").Add(di);
            isRoot = false;
        }

        // OrderBy: SymbolReference carries "ascending(Col1,Col2)" / "descending(...)". Map
        // each named column → its column id. Unknown columns are skipped (best-effort).
        AddOrderBys(mq, sym.OrderBy, columnIdByName);

        return mq;
    }

    // Depth-first flatten: root dataitem(s) then their nested children, preserving order so
    // the engine reconstructs the join tree (root=None, child join types follow).
    private static IEnumerable<BcAppSymbolCache.QueryDataItemSymbol> FlattenDataItems(
        IEnumerable<BcAppSymbolCache.QueryDataItemSymbol> items)
    {
        foreach (var di in items)
        {
            yield return di;
            foreach (var child in FlattenDataItems(di.DataItems))
                yield return child;
        }
    }

    private static string MapSqlJoinType(string? sqlJoinType) => (sqlJoinType ?? "InnerJoin") switch
    {
        "InnerJoin" => "InnerJoin",
        "LeftOuterJoin" => "LeftOuterJoin",
        "RightOuterJoin" => "RightOuterJoin",
        "FullOuterJoin" => "FullOuterJoin",
        "CrossJoin" => "CrossJoin",
        "CrossApply" => "CrossApply",
        "OuterApply" => "OuterApply",
        _ => "InnerJoin",
    };

    // Build a case-insensitive field-NAME → field-no map for a table from the parsed table
    // shape (populated from AL source or the BC .app SymbolReference.json).
    private static Dictionary<string, int> BuildFieldNameToNoMap(int tableNo)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_parsedTables.TryGetValue(tableNo, out var pt))
            foreach (var f in pt.Fields)
                map[f.FieldName] = f.FieldId;
        return map;
    }

    private static int ResolveFieldNo(Dictionary<string, int> fieldNoByName, string fieldName)
        => fieldNoByName.TryGetValue(fieldName, out var no) ? no : -1;

    // Parse `"<thisField>" = <SourceDataItem>."<sourceField>"` into a MetaQueryDataItemLink.
    private static object? ParseDataItemLink(string link, Dictionary<string, int> thisFieldNoByName)
    {
        var eq = link.IndexOf('=');
        if (eq < 0) return null;
        var lhs = Unquote(link[..eq].Trim());
        var rhs = link[(eq + 1)..].Trim();
        var dot = rhs.IndexOf('.');
        if (dot < 0) return null;
        var sourceDataItem = rhs[..dot].Trim();
        var sourceField = Unquote(rhs[(dot + 1)..].Trim());

        int destFieldNo = ResolveFieldNo(thisFieldNoByName, lhs);
        // Source field is on the referenced (parent) dataitem's table — resolve by following
        // that table. We don't know its table no here without the parent dataitem, so resolve
        // via the source data-item name → its RelatedTable from the query symbol map.
        int srcFieldNo = ResolveSourceFieldNo(sourceDataItem, sourceField);
        if (destFieldNo < 0 || srcFieldNo < 0) return null;

        var dl = Activator.CreateInstance(_tMetaQueryDataItemLink!)!;
        SetProp(dl, "SourceDataItemName", sourceDataItem);
        SetProp(dl, "SourceFieldNo", srcFieldNo);
        SetProp(dl, "DestinationFieldNo", destFieldNo);
        return dl;
    }

    // The source dataitem of a link is keyed by name; we stash each dataitem's
    // (name → tableNo) during the current build so the source field can be resolved.
    [ThreadStatic] private static Dictionary<string, int>? _dataItemTableNoByName;

    private static int ResolveSourceFieldNo(string sourceDataItemName, string sourceField)
    {
        if (_dataItemTableNoByName != null
            && _dataItemTableNoByName.TryGetValue(sourceDataItemName, out var srcTableNo))
        {
            var map = BuildFieldNameToNoMap(srcTableNo);
            return ResolveFieldNo(map, sourceField);
        }
        return -1;
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static void AddOrderBys(object mq, string? orderBy, Dictionary<string, int> columnIdByName)
    {
        if (string.IsNullOrWhiteSpace(orderBy)) return;
        // Format: "ascending(Col1,Col2)" or "descending(Col)". May contain multiple groups.
        var rx = new System.Text.RegularExpressions.Regex(
            @"(ascending|descending)\s*\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(orderBy))
        {
            var sorting = m.Groups[1].Value.StartsWith("desc", StringComparison.OrdinalIgnoreCase) ? "Descending" : "Ascending";
            foreach (var raw in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colName = Unquote(raw);
                if (!columnIdByName.TryGetValue(colName, out var colId)) continue;
                var ob = Activator.CreateInstance(_tMetaQueryOrderBy!)!;
                SetProp(ob, "QueryColumnId", colId);
                SetProp(ob, "Sorting", sorting);
                GetList(mq, "OrderBys").Add(ob);
            }
        }
    }

    private static void TrySetProp(object obj, string name, object? value)
    {
        try { SetProp(obj, name, value); } catch { /* optional prop absent on this Types version */ }
    }

    private static MethodInfo? _mMultiLanguageParse;

    private static void AddColumn(object dataItem, int id, string name, int fieldNo, int index, string? caption = null, string? method = null)
    {
        var col = Activator.CreateInstance(_tMetaQueryColumn!)!;
        SetProp(col, "Id", id);
        SetProp(col, "Name", name);
        SetProp(col, "FieldNo", fieldNo);
        SetProp(col, "QueryColumnIndex", index);
        SetProp(col, "FilterOnly", false);
        // Issue #2137: the AL `Method = Sum/Count/Average/Min/Max` property, carried verbatim
        // from the compiled column's SymbolReference.json Properties bag. MetaQueryColumn.
        // FieldTotalingMethod (Microsoft.Dynamics.Nav.Types.AggregationType) is what
        // NCLMetaQuery.CreateDynamicQuery reads to populate the real NCLMetaQueryColumn.
        // AggregationType this runner-synthesized reconstruction otherwise leaves at its
        // default (None) — RecordPatches.QueryProjection.cs's GROUP BY aggregation, and the
        // OOS guards for join+aggregate / HAVING-style filters, all key off that value, so a
        // query with an unset FieldTotalingMethod would silently behave as if it had no
        // Method column at all. Symbol property values are the AggregationType member names
        // verbatim ("Sum", "Count", "Average", "Min", "Max"), so SetProp's Enum.Parse needs no
        // translation. Left at the design object's own default (None) when absent.
        if (!string.IsNullOrEmpty(method) && !string.Equals(method, "None", StringComparison.OrdinalIgnoreCase))
            SetProp(col, "FieldTotalingMethod", method);
        if (caption != null)
        {
            // MetaQueryColumn.CaptionML (MultiLanguage) feeds NCLMetaQueryColumn.columnCaptions
            // via CreateFromDesignMetadata; the AL `Caption = '...'` is the ENU value.
            if (_mMultiLanguageParse == null)
            {
                var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
                var tMl = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MultiLanguage");
                _mMultiLanguageParse = tMl?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            }
            var ml = _mMultiLanguageParse?.Invoke(null, new object[] { "ENU=" + caption });
            if (ml != null)
                col.GetType().GetProperty("CaptionML", BindingFlags.Public | BindingFlags.Instance)?.SetValue(col, ml);
        }
        GetList(dataItem, "QueryColumns").Add(col);
    }

    // A filter-only query column: carries a real BC column id + resolved source field, but
    // FilterOnly=true and no result-slot QueryColumnIndex (it is never projected).
    private static void AddFilterColumn(object dataItem, int id, string name, int fieldNo)
    {
        var col = Activator.CreateInstance(_tMetaQueryColumn!)!;
        SetProp(col, "Id", id);
        SetProp(col, "Name", name);
        SetProp(col, "FieldNo", fieldNo);
        SetProp(col, "FilterOnly", true);
        GetList(dataItem, "QueryColumns").Add(col);
    }
}
