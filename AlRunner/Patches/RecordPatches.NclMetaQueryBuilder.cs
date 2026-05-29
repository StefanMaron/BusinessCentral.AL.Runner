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

namespace AlRunnerV2.Patches;

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

    // SPIKE: hardcoded MetaQuery for corpus query 60022 "ALT Universal Query".
    //   dataitem Universal (table 60000) columns EntryNo=fld1, IntegerValue=fld3, TextValue=fld6
    //   OrderBy = ascending(EntryNo)
    private static object? BuildMetaQueryDesign(int queryId)
    {
        if (queryId != 60022) return null;  // spike scope

        var mq = Activator.CreateInstance(_tMetaQuery!)!;
        SetProp(mq, "Id", 60022);
        SetProp(mq, "Name", "ALT Universal Query");
        SetProp(mq, "ReadState", "ReadUncommitted");
        SetProp(mq, "QueryType", "Normal");
        SetProp(mq, "TopNumberOfRowsToReturn", 0);

        var di = Activator.CreateInstance(_tMetaQueryDataItem!)!;
        SetProp(di, "DataItemName", "Universal");
        SetProp(di, "TableNo", 60000);
        SetProp(di, "Id", 1);
        SetProp(di, "DataItemLinkType", "None");
        SetProp(di, "Distinct", false);

        // Column Ids are the BC-compiler-assigned ids baked into callers (from
        // SymbolReference.json) — they MUST match or GetColumnValueSafe/GetColumnByNo throws.
        // Caption is the query column's configured Caption (the AL `Caption = '...'`),
        // which NCLMetaQueryColumn.Caption returns when set (else it falls back to the
        // source table field caption). ColumnCaption(col) reads this.
        AddColumn(di, id: 1131353536, name: "EntryNo", fieldNo: 1, index: 0, caption: "Entry No.");
        AddColumn(di, id: 1455042288, name: "IntegerValue", fieldNo: 3, index: 1, caption: "Integer Value");
        AddColumn(di, id: 1432813677, name: "TextValue", fieldNo: 6, index: 2, caption: "Text Value");

        GetList(mq, "DataItems").Add(di);

        // OrderBy ascending on EntryNo (its real column id).
        var ob = Activator.CreateInstance(_tMetaQueryOrderBy!)!;
        SetProp(ob, "QueryColumnId", 1131353536);
        SetProp(ob, "Sorting", "Ascending");
        GetList(mq, "OrderBys").Add(ob);

        return mq;
    }

    private static MethodInfo? _mMultiLanguageParse;

    private static void AddColumn(object dataItem, int id, string name, int fieldNo, int index, string? caption = null)
    {
        var col = Activator.CreateInstance(_tMetaQueryColumn!)!;
        SetProp(col, "Id", id);
        SetProp(col, "Name", name);
        SetProp(col, "FieldNo", fieldNo);
        SetProp(col, "QueryColumnIndex", index);
        SetProp(col, "FilterOnly", false);
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
}
