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
using AlRunner.Infrastructure;

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
    private static Type? _tMetaQueryColumnFilter; // #2418
    private static Type? _tMetaQueryFieldFilter;  // #3571
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
        _tMetaQueryFieldFilter = typesAsm?.GetType(md + "MetaQueryFieldFilter");
        _tMetaQueryColumnFilter = typesAsm?.GetType(md + "MetaQueryColumnFilter"); // #2418

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
        => (IList)BcShape.Property(
            obj.GetType(), name, BindingFlags.Public | BindingFlags.Instance, "AL query metadata construction").GetValue(obj)!;

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
        // #3776 — the reflection-availability check above sits OUTSIDE this try, so everything
        // reaching here is a failure to build a query the runner had already established it
        // could build. The absorbed null flows to CodeunitPatches.cs's NavQuery ctor and to
        // EnsureQueryInMetadataCache, where it is dereferenced inside BC's own ALSetFilter /
        // ValidateExpectedType — #3499's shape.
        //
        // The visibility half differs from the form/report builders and is WORSE: their write
        // was merely tag-filtered at default verbosity, while QLog writes nothing at all unless
        // AL_RUNNER_QDIAG=1, so an absorbed failure here produced no output anywhere. The
        // absorbed case now writes the same unfiltered stderr line the others do; QLog keeps
        // the stack-trace detail for a diagnostic run.
        catch (Exception ex) when (BuildRealNclMetaQueryCatchMayAbsorb(ex))
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            QLog($"BuildRealNCLMetaQuery({queryId}) FAILED: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            Console.Error.WriteLine(BuildRealNclMetaQueryFailureLine(queryId, ex));
            return null;
        }
    }

    private static bool BuildRealNclMetaQueryCatchMayAbsorb(Exception ex) => MetaObjectCatchMayAbsorb(ex);

    private static string BuildRealNclMetaQueryFailureLine(int queryId, Exception ex)
        => MetaObjectFailureLine("query", queryId, ex);

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
        // BC's own document first, when the runner compiled this query (#3608). Chosen on
        // AVAILABILITY, before any parse is attempted, and a parse failure throws rather than
        // dropping back here — see RecordPatches.MetaQueryFromBcDocument.cs for what the
        // derivation below could not state and why falling back on error would be wrong.
        if (HasBcQueryMetadataDocument(queryId))
        {
            var fromDocument = BuildMetaQueryDesignFromBcDocument(queryId);
            TraceQueryMetadataSource(queryId, "bc-document", fromDocument);
            return fromDocument;
        }

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
        ApplyDerivableQueryProperties(mq, sym);

        // Flatten the dataitem tree (root first, then nested) into the flat DataItems list.
        // resultColumnIndex is shared across all dataitems (filters do NOT consume a slot).
        int resultColumnIndex = 0;
        // (columnName/dataItemName → columnId) so OrderBy can map names → column ids.
        var columnIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // #2418: raw ColumnFilter property text, collected per-column while columns are built.
        // Resolution (query-column NAME → id) happens in a SEPARATE pass after every dataitem's
        // columns are in columnIdByName, because a ColumnFilter condition may name any query
        // column of the query — including one defined later in dataitem/column order than the
        // column that declares the property.
        var pendingColumnFilters = new List<string>();

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
                AddColumn(di, id: col.Id, name: col.Name, fieldNo: fieldNo, index: resultColumnIndex++, caption: col.Caption, method: col.Method, reverseSign: col.ReverseSign);
                columnIdByName[col.Name] = col.Id;
                if (!string.IsNullOrEmpty(col.ColumnFilter))
                    pendingColumnFilters.Add(col.ColumnFilter!);
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
                // #3572: AL allows a COMMA-SEPARATED list of equalities here, and BC states one
                // <DataItemLink> element per equality in its own emitted document (verified on
                // BC 28.1: a two-field link parses to DataItemLinks count=2). The design object's
                // DataItemLinks is a List for exactly that reason. Splitting on top-level commas
                // only — a quoted field name may itself contain one.
                var links = ParseDataItemLinks(diSym.DataItemLink!, fieldNoByName);
                if (links == null)
                {
                    QLog($"BuildMetaQueryDesign({queryId}): could not parse DataItemLink '{diSym.DataItemLink}' for '{diSym.Name}' — abandoning build");
                    return null;
                }
                foreach (var link in links)
                    GetList(di, "DataItemLinks").Add(link);
            }

            // #3571: the AL `DataItemTableFilter` property restricts this dataitem's own table
            // rows. It resolves to a TABLE field number (not a query column id, which is what
            // ColumnFilter uses) and needs no projected column, so it lands in the dataitem's
            // FieldFilters — the list BC's own
            // NCLMetaQuery.CreateTableFiltersAndMarksFromDataItemFieldFilters reads to build the
            // dataitem's TableFiltersAndMarks. An unparseable property or an unknown field
            // abandons the WHOLE build, matching the ColumnFilter discipline below: running the
            // query unrestricted would return rows real BC excludes.
            if (!string.IsNullOrEmpty(diSym.DataItemTableFilter))
            {
                if (_tMetaQueryFieldFilter == null)
                {
                    QLog($"BuildMetaQueryDesign({queryId}): DataItemTableFilter present but MetaQueryFieldFilter reflection unavailable — abandoning build");
                    return null;
                }
                var parsed = TryParseColumnFilterText(diSym.DataItemTableFilter);
                if (parsed == null)
                {
                    QLog($"BuildMetaQueryDesign({queryId}): DataItemTableFilter '{diSym.DataItemTableFilter}' could not be parsed — abandoning build");
                    return null;
                }
                foreach (var cond in parsed)
                {
                    int filterFieldNo = ResolveFieldNo(fieldNoByName, cond.FieldName);
                    if (filterFieldNo < 0)
                    {
                        QLog($"BuildMetaQueryDesign({queryId}): DataItemTableFilter names unknown field '{cond.FieldName}' on table {tableNo} — abandoning build");
                        return null;
                    }
                    var ff = Activator.CreateInstance(_tMetaQueryFieldFilter)!;
                    SetProp(ff, "FieldNo", filterFieldNo);
                    SetProp(ff, "TypeOfFilter", cond.Kind == ParsedColumnFilterKind.Const ? "CONST" : "FILTER");
                    SetProp(ff, "Value", cond.Value);
                    GetList(di, "FieldFilters").Add(ff);
                }
            }

            GetList(mq, "DataItems").Add(di);
            isRoot = false;
        }

        // #2418: resolve every ColumnFilter condition collected above, now that every dataitem's
        // columns are in columnIdByName. An unresolvable field name or an unparseable condition
        // abandons the WHOLE build (loud, matching the other "abandoning build" guards above) —
        // silently dropping just that condition would make the query return groups/rows real BC
        // excludes, the exact class of bug #2418 reports.
        if (_tMetaQueryColumnFilter != null)
        {
            foreach (var raw in pendingColumnFilters)
            {
                var parsed = TryParseColumnFilterText(raw);
                if (parsed == null)
                {
                    QLog($"BuildMetaQueryDesign({queryId}): ColumnFilter '{raw}' could not be parsed — abandoning build");
                    return null;
                }
                foreach (var cond in parsed)
                {
                    if (!columnIdByName.TryGetValue(cond.FieldName, out var targetColumnId))
                    {
                        QLog($"BuildMetaQueryDesign({queryId}): ColumnFilter '{raw}' names unknown query column '{cond.FieldName}' — abandoning build");
                        return null;
                    }
                    var cf = Activator.CreateInstance(_tMetaQueryColumnFilter)!;
                    SetProp(cf, "QueryColumnId", targetColumnId);
                    SetProp(cf, "TypeOfFilter", cond.Kind == ParsedColumnFilterKind.Const ? "CONST" : "FILTER");
                    SetProp(cf, "Value", cond.Value);
                    GetList(mq, "ColumnFilters").Add(cf);
                }
            }
        }
        else if (pendingColumnFilters.Count > 0)
        {
            QLog($"BuildMetaQueryDesign({queryId}): ColumnFilter present but MetaQueryColumnFilter reflection unavailable — abandoning build");
            return null;
        }

        // OrderBy: SymbolReference carries "ascending(Col1,Col2)" / "descending(...)". Map
        // each named column → its column id. Unknown columns are skipped (best-effort).
        AddOrderBys(mq, sym.OrderBy, columnIdByName);

        TraceQueryMetadataSource(queryId, "symbol-reference", mq);
        return mq;
    }

    /// <summary>
    /// The query-level properties BC's emitter states that the SymbolReference derivation was
    /// leaving at their design-object defaults (#3798). Three sources, and the difference
    /// matters when reading the assertions:
    ///
    /// <list type="bullet">
    /// <item><b>Caption tracks Name</b>, never the declared caption. BC's emitter writes no
    /// <c>&lt;Caption&gt;</c> element at all — 0 of 1,218 documents in the System Application
    /// ground-truth bundle carry one, against 113 carrying <c>&lt;CaptionML&gt;</c> — and
    /// feeding BC's own reader a synthesised document shows it setting Caption from
    /// <c>&lt;Name&gt;</c> while ignoring both a <c>&lt;Caption&gt;</c> element and a
    /// disagreeing <c>&lt;CaptionML&gt;</c>. Query 777 is the case that settles it: the symbol
    /// file says "RoleCenter from Plans", BC answers "Role Center from Plans" (the name).</item>
    /// <item><b>The two inherent masks</b> come from the symbol file's own letter string,
    /// decoded by the shared <see cref="TryDecodePermissionMaskLetters"/> so the case-sensitive
    /// spelling cannot drift from the codeunit direction.</item>
    /// <item><b>HelpLink and the three empty strings</b> are constants BC's emitter writes
    /// unconditionally — identical on all 7 queries of the bundle.</item>
    /// </list>
    ///
    /// <para>Deliberately NOT set here: <c>APIVersion</c>, which BC answers "beta" for 4 of the
    /// 7 and null for the 3 declaring <c>Access = Internal</c>, with nothing stated in the
    /// symbol file either way — the correlation is real on a population of 7 and the mechanism
    /// is unmeasured, so it stays on #3798 rather than being guessed. <c>CaptionML</c> needs a
    /// MultiLanguage whose language id would be invented, and <c>RuntimeInfo</c> is read-only on
    /// the design object.</para>
    ///
    /// <para>See docs/metadata-equivalence.md#queries.</para>
    /// </summary>
    private static void ApplyDerivableQueryProperties(object mq, BcAppSymbolCache.QuerySymbol sym)
    {
        // BC's reader sets Caption from the document's <Name>, so the runner states the name
        // too. The declared caption reaches AL through CaptionML, which this design object does
        // not carry — writing it into Caption would answer something BC never answers.
        if (!string.IsNullOrEmpty(sym.Name)) TrySetProp(mq, "Caption", sym.Name);

        // SetProp, not TrySetProp: these two are what AL observes as the query's inherent
        // permission, and both are measured present on every BC build a leg runs. TrySetProp
        // swallows a missing property, which would turn a Types.dll shape change back into the
        // silent None this fixes rather than a loud failure (loud-failures.md).
        if (TryDecodePermissionMaskLetters(sym.InherentEntitlements, out var entitlements))
            SetProp(mq, "InherentEntitlements", entitlements);
        if (TryDecodePermissionMaskLetters(sym.InherentPermissions, out var permissions))
            SetProp(mq, "InherentPermissions", permissions);

        TrySetProp(mq, "HelpLink", QueryHelpLink);
        // BC writes <QueryCategory/>, <APIGroup/> and <APIPublisher/> — an EMPTY element, which
        // its reader turns into "" rather than null. The design object's own default is already
        // "", so these are stated for the same reason the others are: the property is set from
        // one place, and a future default change cannot silently reopen the difference.
        TrySetProp(mq, "QueryCategory", string.Empty);
        TrySetProp(mq, "APIGroup", string.Empty);
        TrySetProp(mq, "APIPublisher", string.Empty);
    }

    /// <summary>
    /// The documentation URL BC's emitter writes into every query document unconditionally —
    /// identical on all 7 queries of the System Application ground-truth bundle, and not read
    /// from anything the AL declares.
    /// </summary>
    private const string QueryHelpLink = "https://learn.microsoft.com/dynamics365/business-central/";

    /// <summary>
    /// The operator BC states on every <c>&lt;DataItemLink&gt;</c> it emits. AL has no syntax
    /// for anything but equality today, so the constant is faithful rather than a guess — and
    /// leaving it null made the runner's link disagree with BC's on all 4 links in the bundle.
    /// </summary>
    private const string DataItemLinkEqualsOperator = "=";

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

    // AL's default for an undeclared SqlJoinType is LeftOuterJoin, NOT InnerJoin — measured on
    // BC 28.1 from the query metadata document BC's own emitter produces for a nested dataitem
    // that declares no SqlJoinType (<DataItemLinkType>Left Outer Join</DataItemLinkType>), and
    // adjudicated on a real service tier by corpus PR #297. Defaulting to InnerJoin dropped
    // every parent row with no matching child. This arm now serves only queries with no BC
    // document — a precompiled dependency's — since a compiled query takes the document route;
    // both routes must agree on the default or the same query answers differently by provenance.
    private static string MapSqlJoinType(string? sqlJoinType) => (sqlJoinType ?? "LeftOuterJoin") switch
    {
        "InnerJoin" => "InnerJoin",
        "LeftOuterJoin" => "LeftOuterJoin",
        "RightOuterJoin" => "RightOuterJoin",
        "FullOuterJoin" => "FullOuterJoin",
        "CrossJoin" => "CrossJoin",
        "CrossApply" => "CrossApply",
        "OuterApply" => "OuterApply",
        _ => "LeftOuterJoin",
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

    // Parse a DataItemLink property — one or more comma-separated
    // `"<thisField>" = <SourceDataItem>."<sourceField>"` equalities — into one
    // MetaQueryDataItemLink each (#3572). Null iff ANY equality fails to parse or resolve:
    // applying a subset would join on fewer fields than AL declares, widening the result
    // silently, which is the failure this returns null to prevent.
    private static List<object>? ParseDataItemLinks(string link, Dictionary<string, int> thisFieldNoByName)
    {
        var parts = SplitTopLevelCommas(link.Trim());
        if (parts.Count == 0) return null;
        var result = new List<object>();
        foreach (var part in parts)
        {
            if (part.Trim().Length == 0) continue;
            var one = ParseDataItemLink(part, thisFieldNoByName);
            if (one == null) return null;
            result.Add(one);
        }
        return result.Count == 0 ? null : result;
    }

    // Parse `"<thisField>" = <SourceDataItem>."<sourceField>"` into a MetaQueryDataItemLink.
    // ONE equality only — ParseDataItemLinks splits a multi-field property before calling this.
    private static object? ParseDataItemLink(string link, Dictionary<string, int> thisFieldNoByName)
    {
        // Top-level: a quoted field name may contain '=' or '.', so neither may be located by
        // a bare IndexOf (`"A=B" = Hdr."C.D"` is one legal equality).
        var eq = TopLevelIndexOf(link, '=');
        if (eq < 0) return null;
        var lhs = Unquote(link[..eq].Trim());
        var rhs = link[(eq + 1)..].Trim();
        var dot = TopLevelIndexOf(rhs, '.');
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
        // #3798 — the equality this method just parsed, stated the way BC states it. The
        // property was left null while BC answered "=" on every link it emits.
        TrySetProp(dl, "LinkOperator", DataItemLinkEqualsOperator);
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

    private static void AddColumn(object dataItem, int id, string name, int fieldNo, int index, string? caption = null, string? method = null, bool reverseSign = false)
    {
        var col = Activator.CreateInstance(_tMetaQueryColumn!)!;
        SetProp(col, "Id", id);
        SetProp(col, "Name", name);
        SetProp(col, "FieldNo", fieldNo);
        SetProp(col, "QueryColumnIndex", index);
        SetProp(col, "FilterOnly", false);
        // Issue #2575: the AL `ReverseSign = true` property. NCLMetaQueryColumn.
        // CreateFromDesignMetadata (RecordPatches.NclMetaQueryBuilder's runtime counterpart)
        // reads MetaQueryColumn.ReverseSign verbatim into the real NCLMetaQueryColumn's own
        // ReverseSign property — the value QueryProjection/JoinExecutor read to negate the
        // column's projected value. Left at the design object's own default (false) when absent,
        // same convention as FieldTotalingMethod above.
        SetProp(col, "ReverseSign", reverseSign);
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
        // -1, not the design object's default 0, which is a REAL result slot: BC's own document
        // states QueryColumnIndex -1 for a filter-only column (measured on BC 28.1), and this
        // arm must agree with the document route or a dependency query's filter column claims
        // the first result column's slot.
        SetProp(col, "QueryColumnIndex", -1);
        GetList(dataItem, "QueryColumns").Add(col);
    }
}
