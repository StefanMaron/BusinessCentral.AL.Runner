using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlRunner.Patches;

internal static partial class BcAppSymbolCache
{
    // v3: added Queries to the parsed payload (generic NCLMetaQuery builder).
    // v4: added Objects — the flat (kind, id, name) inventory that feeds the AllObj
    //     system virtual table (2000000038). See RecordPatches.AllObjVirtualTable.cs.
    // v5: added Reports — caption / ProcessingOnly / UseRequestPage / data-item tree,
    //     feeding the Report Metadata (2000000139) and Report Data Items (2000000203)
    //     virtual tables. See RecordPatches.ReportMetadataVirtualTable.cs.
    // v6: report data items now have their #appId# module qualifier stripped from
    //     RelatedTable. Any parse CHANGE needs a bump, not just a shape change — the
    //     on-disk payload is keyed on this, so a v5 cache written by the buggy parse
    //     stays valid and silently replays the old result.
    // v7: Objects carry their Caption property, feeding the AllObjWithCaption system
    //     virtual table (2000000058). See RecordPatches.AllObjWithCaptionVirtualTable.cs.
    // v8: Reports carry their per-data-item Columns and their ReferenceSourceFileName,
    //     which together let DependencyReportMetadata synthesize the runtime metadata XML
    //     a precompiled dependency's report ships no compiled form of.
    // v9: ParsedTable gained LookupPageName / DrillDownPageName for the Table Metadata
    // (2000000136) virtual table. A v8 payload deserialises cleanly with both null, so
    // without this bump every cached dependency would report "declares no lookup page"
    // for tables that plainly declare one — a silent wrong answer, not a cache miss.
    private const int CacheVersion = 9;
    private static readonly ConcurrentDictionary<string, AppSymbols> ProcessCache = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record AppSymbols(List<ParsedTable> Tables, List<EnumSymbol> Enums, List<QuerySymbol> Queries,
        List<ObjectSymbol> Objects, List<ReportSymbol> Reports);

    /// <summary>
    /// A precompiled dependency's report, as far as SymbolReference.json states it. Feeds
    /// the Report Metadata / Report Data Items virtual tables for reports the runner never
    /// compiles (Base Application, System Application, ISV apps).
    /// </summary>
    internal sealed record ReportSymbol(
        int Id, string Name, string? Caption, bool ProcessingOnly, bool UseRequestPage,
        string? WordMergeDataItem, List<ReportDataItemSymbol> DataItems,
        // Path of the report's AL source INSIDE the .app's embedded src/ tree, as the
        // symbol file states it. The runtime metadata synthesizer uses it to read back
        // that ONE file for the column source expressions the symbol file omits — see
        // DependencyReportMetadata.cs.
        string? ReferenceSourceFileName = null);

    /// <summary>One entry of a report's data-item tree, flattened in declaration order.</summary>
    internal sealed record ReportDataItemSymbol(
        int Id, string Name, string RelatedTable, int Indentation,
        string? DataItemTableView, string? RequestFilterFields,
        List<ReportColumnSymbol>? Columns = null);

    /// <summary>
    /// One <c>column(Name; SourceExpr)</c> of a report data item, as SymbolReference.json
    /// states it. The symbol file carries the compiler-assigned <c>Id</c>, the column
    /// <c>Name</c> and its resolved <c>TypeName</c> — but NOT the source expression, which
    /// only the AL source has. That gap is why the synthesizer reads the report's own
    /// source file back out of the .app rather than inventing an expression.
    /// </summary>
    internal sealed record ReportColumnSymbol(int Id, string Name, string? TypeName);

    /// <summary>
    /// Flat (AL object kind, id, name, caption) tuple for one application object declared
    /// by a dependency .app. Read straight off the SymbolReference.json object arrays,
    /// which carry <c>Id</c> + <c>Name</c> for every kind — including the Codeunits /
    /// Pages / Reports / XmlPorts the typed parsing above deliberately ignores. Consumed
    /// by the AllObj (2000000038) and AllObjWithCaption (2000000058) virtual tables.
    ///
    /// <c>Caption</c> is null when the object declares no Caption property; AL's own
    /// default caption is then the object name, and applying that default is the
    /// consumer's job so the "not stated" and "stated as the name" cases stay distinct
    /// here.
    /// </summary>
    internal sealed record ObjectSymbol(string Kind, int Id, string Name, string? Caption = null);

    // SymbolReference.json container name → the AllObj "Object Type" option name the
    // objects inside it map to. Matched against the live option string by name, so a
    // container whose kind this BC version's AllObj does not list is simply dropped.
    private static readonly (string Container, string Kind)[] ObjectContainers =
    {
        ("Tables", "Table"),
        ("Codeunits", "Codeunit"),
        ("Pages", "Page"),
        ("Reports", "Report"),
        ("XmlPorts", "XMLport"),
        ("Queries", "Query"),
        ("EnumTypes", "Enum"),
        ("TableExtensions", "TableExtension"),
        ("PageExtensions", "PageExtension"),
        ("ReportExtensions", "ReportExtension"),
        ("EnumExtensionTypes", "EnumExtension"),
        ("PermissionSets", "PermissionSet"),
        ("PermissionSetExtensions", "PermissionSetExtension"),
    };
    internal sealed record EnumSymbol(int Id, string Name, List<string> Options, List<int> Indexes, List<List<int>> Implementations);

    // Parsed query SymbolReference.json shape. A query is a tree of dataitems; the root
    // dataitem(s) live under the query's "Elements", nested dataitems under "DataItems".
    // Column/Filter Id is the BC-compiler-assigned column id baked into precompiled callers
    // (NavQuery.ValidateExpectedType(columnId,...)/GetColumnValueSafe) — it MUST be used verbatim.
    internal sealed record QuerySymbol(
        int Id, string Name, string? QueryType, string? Caption, string? OrderBy,
        int TopNumberOfRowsToReturn, List<QueryDataItemSymbol> DataItems);

    internal sealed record QueryDataItemSymbol(
        int Id, string Name, string RelatedTable, string? SqlJoinType, string? DataItemLink,
        List<QueryColumnSymbol> Columns, List<QueryColumnSymbol> Filters,
        List<QueryDataItemSymbol> DataItems);

    // SourceColumn is the field NAME on RelatedTable; Id is the BC column id; Caption optional.
    internal sealed record QueryColumnSymbol(int Id, string Name, string SourceColumn, string? Caption);

    private sealed record CachePayload(long Length, long LastWriteUtcTicks,
        List<ParsedTable> Tables, List<EnumSymbol> Enums, List<QuerySymbol> Queries,
        List<ObjectSymbol>? Objects, List<ReportSymbol>? Reports);

    /// <summary>
    /// Parse a loose <c>SymbolReference.json</c> file (the raw module JSON, NOT a .app
    /// zip) into <see cref="AppSymbols"/>. Used for the bundle's own freshly-compiled
    /// query symbols, written by <c>BcCompiler.Emit</c>. Mirrors <see cref="Parse"/> but
    /// reads the JSON directly. No on-disk cache: the file is overwritten every run, and
    /// parsing a single small module is cheap.
    /// </summary>
    internal static AppSymbols GetFromJson(string jsonPath)
    {
        var tables = new Dictionary<int, ParsedTable>();
        var enums = new Dictionary<int, EnumSymbol>();
        var queries = new Dictionary<int, QuerySymbol>();
        var objects = new Dictionary<(string, int), ObjectSymbol>();
        var reports = new Dictionary<int, ReportSymbol>();
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        VisitSymbolContainer(doc.RootElement, tables, enums, queries, objects, reports);
        return new AppSymbols(tables.Values.ToList(), enums.Values.ToList(), queries.Values.ToList(),
            objects.Values.ToList(), reports.Values.ToList());
    }

    internal static AppSymbols Get(string appPath)
    {
        var info = new FileInfo(appPath);
        var key = $"{Path.GetFullPath(appPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|v{CacheVersion}";
        if (ProcessCache.TryGetValue(key, out var cachedInProcess))
            return cachedInProcess;

        var sw = Stopwatch.StartNew();
        var cachePath = CachePath(key);
        var cached = TryRead(cachePath, info);
        if (cached != null)
        {
            PerfTrace.Log($"bc-symbols HIT {Path.GetFileName(appPath)} tables={cached.Tables.Count} enums={cached.Enums.Count} queries={cached.Queries.Count} {sw.ElapsedMilliseconds}ms");
            ProcessCache[key] = cached;
            return cached;
        }

        var parsed = Parse(appPath);
        TryWrite(cachePath, info, parsed);
        PerfTrace.Log($"bc-symbols MISS {Path.GetFileName(appPath)} tables={parsed.Tables.Count} enums={parsed.Enums.Count} queries={parsed.Queries.Count} {sw.ElapsedMilliseconds}ms");
        ProcessCache[key] = parsed;
        return parsed;
    }

    private static AppSymbols? TryRead(string cachePath, FileInfo appInfo)
    {
        if (!File.Exists(cachePath)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<CachePayload>(File.ReadAllText(cachePath));
            if (payload == null
                || payload.Length != appInfo.Length
                || payload.LastWriteUtcTicks != appInfo.LastWriteTimeUtc.Ticks)
                return null;
            return new AppSymbols(payload.Tables, payload.Enums, payload.Queries ?? new List<QuerySymbol>(),
                payload.Objects ?? new List<ObjectSymbol>(), payload.Reports ?? new List<ReportSymbol>());
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"bc-symbols cache read failed {Path.GetFileName(cachePath)}: {ex.Message}");
            return null;
        }
    }

    private static void TryWrite(string cachePath, FileInfo appInfo, AppSymbols symbols)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var payload = new CachePayload(appInfo.Length, appInfo.LastWriteTimeUtc.Ticks, symbols.Tables, symbols.Enums, symbols.Queries, symbols.Objects, symbols.Reports);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            PerfTrace.Log($"bc-symbols cache write failed {Path.GetFileName(cachePath)}: {ex.Message}");
        }
    }

    private static AppSymbols Parse(string appPath)
    {
        var tables = new Dictionary<int, ParsedTable>();
        var enums = new Dictionary<int, EnumSymbol>();
        var queries = new Dictionary<int, QuerySymbol>();
        var objects = new Dictionary<(string, int), ObjectSymbol>();
        var reports = new Dictionary<int, ReportSymbol>();
        foreach (var json in ReadSymbolReferences(appPath))
        {
            using var doc = JsonDocument.Parse(json);
            VisitSymbolContainer(doc.RootElement, tables, enums, queries, objects, reports);
        }
        return new AppSymbols(tables.Values.ToList(), enums.Values.ToList(), queries.Values.ToList(),
            objects.Values.ToList(), reports.Values.ToList());
    }

    private static void VisitSymbolContainer(JsonElement container, Dictionary<int, ParsedTable> tables, Dictionary<int, EnumSymbol> enums, Dictionary<int, QuerySymbol> queries, Dictionary<(string, int), ObjectSymbol> objects, Dictionary<int, ReportSymbol> reports)
    {
        // Flat (kind, id, name) sweep for AllObj. Independent of the typed parsing below
        // so a kind we do not model in depth still shows up as an existing object.
        foreach (var (containerName, kind) in ObjectContainers)
        {
            if (!container.TryGetProperty(containerName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var objId) || objId <= 0)
                    continue;
                var objName = el.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrEmpty(objName)) continue;
                SymbolProperties(el).TryGetValue("Caption", out var objCaption);
                objects.TryAdd((kind, objId), new ObjectSymbol(kind, objId, objName, objCaption));
            }
        }

        if (container.TryGetProperty("Tables", out var tableArray) && tableArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var table in tableArray.EnumerateArray())
            {
                var parsed = TryParseTableSymbol(table);
                if (parsed != null && !tables.ContainsKey(parsed.TableId))
                    tables[parsed.TableId] = parsed;
            }
        }

        if (container.TryGetProperty("EnumTypes", out var enumTypes) && enumTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var enumType in enumTypes.EnumerateArray())
            {
                var parsed = TryParseEnumSymbol(enumType);
                if (parsed != null)
                    enums[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("Queries", out var queryArray) && queryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in queryArray.EnumerateArray())
            {
                var parsed = TryParseQuerySymbol(q);
                if (parsed != null && !queries.ContainsKey(parsed.Id))
                    queries[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("Reports", out var reportArray) && reportArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reportArray.EnumerateArray())
            {
                var parsed = TryParseReportSymbol(r);
                if (parsed != null && !reports.ContainsKey(parsed.Id))
                    reports[parsed.Id] = parsed;
            }
        }

        if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var ns in namespaces.EnumerateArray())
                VisitSymbolContainer(ns, tables, enums, queries, objects, reports);
        }
    }

    /// <summary>
    /// Parse one entry of a SymbolReference.json <c>Reports</c> array into the subset the
    /// Report Metadata (2000000139) / Report Data Items (2000000203) virtual tables expose.
    /// This is the ONLY route to a precompiled dependency's report shape: an R2R app ships
    /// no metadata XML, and its 8000-file embedded <c>src/</c> is far too expensive to parse
    /// for this. The symbol file carries the data verbatim (Id, Name, Caption property, the
    /// full DataItems tree with per-item RelatedTable and Indentation), so nothing is
    /// inferred here — a shape the symbol file does not state is left null/absent for the
    /// caller to default, never invented.
    /// </summary>
    private static ReportSymbol? TryParseReportSymbol(JsonElement report)
    {
        if (!report.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var reportId) || reportId <= 0)
            return null;
        var name = report.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;

        var props = SymbolProperties(report);
        props.TryGetValue("Caption", out var caption);
        props.TryGetValue("WordMergeDataItem", out var wordMergeDataItem);
        // AL defaults: ProcessingOnly false, UseRequestPage true. The symbol file only
        // states a property when the AL source declared it.
        bool processingOnly = props.TryGetValue("ProcessingOnly", out var po)
            && (po == "1" || string.Equals(po, "true", StringComparison.OrdinalIgnoreCase));
        bool useRequestPage = !(props.TryGetValue("UseRequestPage", out var urp)
            && (urp == "0" || string.Equals(urp, "false", StringComparison.OrdinalIgnoreCase)));

        var dataItems = new List<ReportDataItemSymbol>();
        CollectReportDataItems(report, indentation: 0, dataItems);

        var referenceSourceFileName = report.TryGetProperty("ReferenceSourceFileName", out var rsf)
            ? rsf.GetString()
            : null;

        return new ReportSymbol(reportId, name, caption, processingOnly, useRequestPage,
            wordMergeDataItem, dataItems, referenceSourceFileName);
    }

    /// <summary>
    /// A SymbolReference.json object reference is <c>#&lt;appIdNoHyphens&gt;#&lt;Name&gt;</c>
    /// whenever it crosses a module boundary, and a plain name within one. A report data
    /// item bound to a table from another module (System's Integer / Company / AllObj,
    /// which is most of them) therefore arrives qualified; leaving the prefix on makes the
    /// table unresolvable and silently drops the report. Same rule as TargetObject on a
    /// tableextension — see BcAppSymbolCache.TableExtensions.cs.
    /// </summary>
    private static string? StripModuleQualifier(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference[0] != '#') return reference;
        var secondHash = reference.IndexOf('#', 1);
        return secondHash >= 0 ? reference.Substring(secondHash + 1) : reference;
    }

    /// <summary>
    /// Flatten a report's data-item tree in declaration order. Nested data items live under
    /// each item's own <c>DataItems</c>; the symbol file also carries an explicit
    /// <c>Indentation</c> on nested entries, which is preferred when present so our depth
    /// count can never disagree with the compiler's own.
    /// </summary>
    private static void CollectReportDataItems(JsonElement container, int indentation, List<ReportDataItemSymbol> into)
    {
        if (!container.TryGetProperty("DataItems", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var di in arr.EnumerateArray())
        {
            var name = di.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var relatedTable = StripModuleQualifier(
                di.TryGetProperty("RelatedTable", out var rt) ? rt.GetString() : null);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(relatedTable)) continue;

            int indent = di.TryGetProperty("Indentation", out var ind) && ind.TryGetInt32(out var iv)
                ? iv
                : indentation;
            int dataItemId = di.TryGetProperty("Id", out var diId) && diId.TryGetInt32(out var idv) ? idv : 0;

            var props = SymbolProperties(di);
            props.TryGetValue("DataItemTableView", out var tableView);
            props.TryGetValue("RequestFilterFields", out var filterFields);

            into.Add(new ReportDataItemSymbol(dataItemId, name, relatedTable, indent, tableView, filterFields,
                ParseReportColumns(di)));
            CollectReportDataItems(di, indent + 1, into);
        }
    }

    /// <summary>
    /// A report data item's <c>Columns</c> array. Each entry states the compiler-assigned
    /// <c>Id</c>, the AL column <c>Name</c> and a <c>TypeDefinition</c> — the resolved AL
    /// type of the column's expression (e.g. <c>Code[20]</c>, <c>Decimal</c>). Only the
    /// leading type name is kept: the length suffix is a property of the expression's
    /// result, not of the report metadata's FieldType vocabulary.
    /// </summary>
    private static List<ReportColumnSymbol> ParseReportColumns(JsonElement dataItem)
    {
        var result = new List<ReportColumnSymbol>();
        if (!dataItem.TryGetProperty("Columns", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var col in arr.EnumerateArray())
        {
            var name = col.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            int id = col.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;

            string? typeName = null;
            if (col.TryGetProperty("TypeDefinition", out var td)
                && td.TryGetProperty("Name", out var tn))
            {
                typeName = tn.GetString();
                var bracket = typeName?.IndexOf('[');
                if (bracket is > 0) typeName = typeName!.Substring(0, bracket.Value);
            }
            result.Add(new ReportColumnSymbol(id, name!, typeName));
        }
        return result;
    }

    private static QuerySymbol? TryParseQuerySymbol(JsonElement query)
    {
        if (!query.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var queryId))
            return null;
        var name = query.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? $"Query{queryId}" : $"Query{queryId}";
        var props = SymbolProperties(query);
        props.TryGetValue("QueryType", out var queryType);
        props.TryGetValue("Caption", out var caption);
        props.TryGetValue("OrderBy", out var orderBy);
        int top = 0;
        if (props.TryGetValue("TopNumberOfRows", out var topText) && int.TryParse(topText, out var t)) top = t;

        // Root dataitems live under "Elements"; nested ones under "DataItems".
        var dataItems = new List<QueryDataItemSymbol>();
        if (query.TryGetProperty("Elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            foreach (var el in elements.EnumerateArray())
            {
                var di = TryParseQueryDataItem(el);
                if (di != null) dataItems.Add(di);
            }
        return new QuerySymbol(queryId, name, queryType, caption, orderBy, top, dataItems);
    }

    private static QueryDataItemSymbol? TryParseQueryDataItem(JsonElement el)
    {
        var name = el.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        var relatedTable = el.TryGetProperty("RelatedTable", out var rtProp) ? rtProp.GetString() ?? string.Empty : string.Empty;
        int id = el.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;
        var props = SymbolProperties(el);
        props.TryGetValue("SqlJoinType", out var sqlJoinType);
        props.TryGetValue("DataItemLink", out var dataItemLink);

        var columns = ParseQueryColumns(el, "Columns");
        var filters = ParseQueryColumns(el, "Filters");

        var nested = new List<QueryDataItemSymbol>();
        if (el.TryGetProperty("DataItems", out var di) && di.ValueKind == JsonValueKind.Array)
            foreach (var child in di.EnumerateArray())
            {
                var c = TryParseQueryDataItem(child);
                if (c != null) nested.Add(c);
            }
        return new QueryDataItemSymbol(id, name, relatedTable, sqlJoinType, dataItemLink, columns, filters, nested);
    }

    private static List<QueryColumnSymbol> ParseQueryColumns(JsonElement dataItem, string arrayName)
    {
        var result = new List<QueryColumnSymbol>();
        if (!dataItem.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var col in arr.EnumerateArray())
        {
            int id = col.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out var i) ? i : 0;
            var name = col.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var sourceColumn = col.TryGetProperty("SourceColumn", out var scProp) ? scProp.GetString() ?? string.Empty : string.Empty;
            var props = SymbolProperties(col);
            props.TryGetValue("Caption", out var caption);
            result.Add(new QueryColumnSymbol(id, name, sourceColumn, caption));
        }
        return result;
    }

    private static ParsedTable? TryParseTableSymbol(JsonElement table)
    {
        if (!table.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var tableId))
            return null;
        var tableName = table.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? $"Table{tableId}"
            : $"Table{tableId}";

        var fields = new List<ParsedField>();
        if (table.TryGetProperty("Fields", out var fieldsJson) && fieldsJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fieldsJson.EnumerateArray())
            {
                if (!field.TryGetProperty("Id", out var fidProp) || !fidProp.TryGetInt32(out var fieldId))
                    continue;
                var fieldName = field.TryGetProperty("Name", out var fnameProp)
                    ? fnameProp.GetString() ?? $"Field{fieldId}"
                    : $"Field{fieldId}";
                var typeName = SymbolTypeName(field.TryGetProperty("TypeDefinition", out var td) ? td : default);
                var props = SymbolProperties(field);
                var isFlowField = props.TryGetValue("FieldClass", out var fieldClass)
                    && string.Equals(fieldClass, "FlowField", StringComparison.OrdinalIgnoreCase);
                ParsedCalcFormula? calcFormula = null;
                if (isFlowField && props.TryGetValue("CalcFormula", out var calcFormulaText))
                    calcFormula = RecordPatches.TryParseCalcFormula($"CalcFormula = {calcFormulaText};");
                props.TryGetValue("OptionMembers", out var optionMembers);
                props.TryGetValue("InitValue", out var initValue);
                var isAutoIncrement = props.TryGetValue("AutoIncrement", out var autoIncrement)
                    && (autoIncrement == "1" || autoIncrement.Equals("true", StringComparison.OrdinalIgnoreCase));
                fields.Add(new ParsedField(fieldId, fieldName, typeName, SymbolTypeLength(typeName), isFlowField, calcFormula,
                    optionMembers, initValue, isAutoIncrement));
            }
        }

        var pkFieldIds = new List<int>();
        var secondaryKeys = new List<ParsedKey>();
        if (table.TryGetProperty("Keys", out var keysJson) && keysJson.ValueKind == JsonValueKind.Array)
        {
            var first = true;
            foreach (var key in keysJson.EnumerateArray())
            {
                var keyName = key.TryGetProperty("Name", out var keyNameProp)
                    ? keyNameProp.GetString() ?? "Key"
                    : "Key";
                var ids = new List<int>();
                if (key.TryGetProperty("FieldNames", out var fieldNames) && fieldNames.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fieldNameJson in fieldNames.EnumerateArray())
                    {
                        var fieldName = fieldNameJson.GetString();
                        var field = fields.FirstOrDefault(f =>
                            string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));
                        if (field != null) ids.Add(field.FieldId);
                    }
                }
                if (first)
                {
                    pkFieldIds.AddRange(ids);
                    first = false;
                }
                else if (ids.Count > 0)
                {
                    secondaryKeys.Add(new ParsedKey(keyName, ids));
                }
            }
        }
        if (pkFieldIds.Count == 0 && fields.Count > 0)
            pkFieldIds.Add(fields[0].FieldId);

        var tableProps = SymbolProperties(table);
        var isTemporary = tableProps.TryGetValue("TableType", out var tableType)
            && string.Equals(tableType, "Temporary", StringComparison.OrdinalIgnoreCase);
        // Page-resolution properties for the Table Metadata (2000000136) virtual table. The
        // symbol file states these as the page's NAME, not its id, and is inconsistent about
        // the trailing casing — Base Application 28.1 carries both "LookupPageID" and
        // "LookupPageId" across different tables. SymbolProperties is case-insensitive, so
        // one lookup covers both spellings; the name is resolved to an id at row-build time.
        tableProps.TryGetValue("LookupPageId", out var lookupPageName);
        tableProps.TryGetValue("DrillDownPageId", out var drillDownPageName);
        return new ParsedTable(tableId, tableName, fields, pkFieldIds, secondaryKeys, isTemporary,
            DataPerCompany: true,
            LookupPageName: string.IsNullOrWhiteSpace(lookupPageName) ? null : lookupPageName,
            DrillDownPageName: string.IsNullOrWhiteSpace(drillDownPageName) ? null : drillDownPageName);
    }

    private static EnumSymbol? TryParseEnumSymbol(JsonElement enumType)
    {
        if (!enumType.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var id))
            return null;
        var name = enumType.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        if (!enumType.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        var options = new List<string>();
        var indexes = new List<int>();
        var implementations = new List<List<int>>();
        var nextOrdinal = 0;
        foreach (var value in values.EnumerateArray())
        {
            var optionName = value.TryGetProperty("Name", out var optionNameProp)
                ? optionNameProp.GetString() ?? string.Empty
                : string.Empty;
            var ordinal = value.TryGetProperty("Ordinal", out var ordinalProp) && ordinalProp.TryGetInt32(out var explicitOrdinal)
                ? explicitOrdinal
                : nextOrdinal;
            options.Add(optionName);
            indexes.Add(ordinal);
            var implementationIds = new List<int>();
            var props = SymbolProperties(value);
            if (props.TryGetValue("Implementation", out var implementationText))
            {
                foreach (var part in implementationText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out var implementationId))
                        implementationIds.Add(implementationId);
                }
            }
            implementations.Add(implementationIds);
            nextOrdinal = ordinal + 1;
        }
        return new EnumSymbol(id, name, options, indexes, implementations);
    }

    private static Dictionary<string, string> SymbolProperties(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("Properties", out var props) || props.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var prop in props.EnumerateArray())
        {
            if (!prop.TryGetProperty("Name", out var nameProp)) continue;
            var name = nameProp.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            if (prop.TryGetProperty("Value", out var valueProp))
                result[name] = valueProp.GetString() ?? string.Empty;
        }
        return result;
    }

    private static string SymbolTypeName(JsonElement typeDefinition)
    {
        if (typeDefinition.ValueKind != JsonValueKind.Object)
            return "Text";
        var name = typeDefinition.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? "Text"
            : "Text";
        if (string.Equals(name, "Enum", StringComparison.OrdinalIgnoreCase)
            && typeDefinition.TryGetProperty("Subtype", out var subtype)
            && subtype.ValueKind == JsonValueKind.Object
            && subtype.TryGetProperty("Name", out var enumNameProp))
            return $"Enum \"{enumNameProp.GetString() ?? string.Empty}\"";
        return name;
    }

    private static int SymbolTypeLength(string typeName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(typeName, @"\[(\d+)\]");
        return m.Success && int.TryParse(m.Groups[1].Value, out var length) ? length : 0;
    }

    private static IEnumerable<string> ReadSymbolReferences(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        foreach (var json in ReadSymbolReferencesFromBytes(bytes))
            yield return json;
    }

    private static IEnumerable<string> ReadSymbolReferencesFromBytes(byte[] bytes)
    {
        using var zip = OpenZipFromNavx(bytes);
        var symbol = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        if (symbol != null)
        {
            using var s = symbol.Open();
            using var reader = new StreamReader(s);
            yield return reader.ReadToEnd();
        }

        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (nested != null)
        {
            using var ns = nested.Open();
            using var ms = new MemoryStream();
            ns.CopyTo(ms);
            foreach (var json in ReadSymbolReferencesFromBytes(ms.ToArray()))
                yield return json;
        }
    }

    /// <summary>
    /// Read ONE AL source file out of a dependency .app's embedded <c>src/</c> tree.
    ///
    /// A published .app carries the app's full AL source, but a Base-Application-sized one
    /// holds ~8000 files — reading the tree to answer a question about a single object is
    /// why the report symbol parsing deliberately never did it. This is the targeted form:
    /// SymbolReference.json states each report's own <c>ReferenceSourceFileName</c>, so the
    /// caller already knows the one entry it wants and this opens exactly that.
    ///
    /// The stated path is app-root-relative (<c>src/Foo/Bar.Report.al</c>) while the zip
    /// nests it under its own prefix (<c>src/src/Foo/Bar.Report.al</c>), so entries are
    /// matched on suffix. Returns null when the app ships no source for it — a symbols-only
    /// .app is a legitimate shape, not an error.
    /// </summary>
    internal static string? TryReadSourceFile(string appPath, string referenceSourceFileName)
    {
        if (string.IsNullOrEmpty(referenceSourceFileName)) return null;
        var wanted = referenceSourceFileName.Replace('\\', '/').TrimStart('/');
        try
        {
            return TryReadSourceFromBytes(File.ReadAllBytes(appPath), wanted);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[BcAppSymbolCache] source read failed for {Path.GetFileName(appPath)}!{wanted}: {ex.Message}");
            return null;
        }
    }

    private static string? TryReadSourceFromBytes(byte[] bytes, string wanted)
    {
        using var zip = OpenZipFromNavx(bytes);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        // R2R wrapper .app: the real package (with its src/ tree) is nested one level in.
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (nested == null) return null;
        using var ns = nested.Open();
        using var ms = new MemoryStream();
        ns.CopyTo(ms);
        return TryReadSourceFromBytes(ms.ToArray(), wanted);
    }

    private static ZipArchive OpenZipFromNavx(byte[] bytes)
    {
        var offset = bytes.Length >= 8
            && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
            && bytes[2] == (byte)'V' && bytes[3] == (byte)'X'
                ? (int)BitConverter.ToUInt32(bytes, 4)
                : 0;
        var ms = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static string CachePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "al-runner", "bc-symbols", hash + ".json");
    }

}
