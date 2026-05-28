// RecordPatches.BcAppFallback — populate _parsedTables on demand from BC .app
// dependency packages when AL test source doesn't define the requested table.
//
// Why: tests under tests/spike-a-baseapp (and any integration test that touches
// a Base App / System App table such as Currency = table 4) fail with
//   "no NCLMetaTable for table N (AL source not parsed)"
// because BuildNCLMetaTable only consults _parsedTables, populated from the
// test suite's own src/ directory. The compiled Record{N} : NavRecord type IS
// loaded (Tier 2 R2R), but it doesn't carry table-shape attributes — field
// metadata in BC compiled apps lives in SymbolReference.json inside the .app
// NAVX zip (with AL source as a fallback for packages without symbols).
//
// Per .claude/rules/precompiled-dll-respect.md the fix is upstream from the
// AL business logic: when a table id is missing from _parsedTables, walk the
// list of dependency .app files (registered by Program.cs after dep load),
// read the matching table metadata from SymbolReference.json. If a package has
// no symbols, fall back to extracting the matching `*.Table.al` source via
// AppLoader.ExtractAl and feeding it through the existing parser.
//
// Performance: symbol index built lazily on first miss by reading each .app's
// SymbolReference.json (recursive namespaces). AL source extraction is only
// used as a fallback. Negative misses are cached so a non-existent table
// doesn't re-scan every .app on every Init().

using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlRunnerV2.Patches;

public static partial class RecordPatches
{
    // .app file paths registered by Program.cs after DependencyLoader.LoadAll.
    private static readonly List<string> _bcAppPaths = new();

    // Temp .app file extracted from Microsoft.BusinessCentral.SystemApp.dll's embedded
    // SystemPackage; persists for the lifetime of the runner process so the index can
    // re-read its source on demand.
    private static string? _systemAppTempPath;

    // Lazy fallback index: tableId → (appPath, alSource). Built only when symbols miss.
    private static Dictionary<int, (string AppPath, string Source)>? _bcTableIndex;
    private static Dictionary<int, (string AppPath, ParsedTable Table)>? _bcSymbolTableIndex;
    private static readonly object _bcTableIndexLock = new();

    // Negative cache: tableIds we've already tried and not found.
    private static readonly HashSet<int> _bcMissCache = new();

    /// <summary>
    /// Register a BC dependency .app path so its AL table sources can be used
    /// as a fallback when a test's own src/ doesn't define a referenced table.
    /// Called from Program.cs after DependencyLoader.LoadAll.
    /// </summary>
    public static void AddBcAppPath(string appPath)
    {
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath)) return;
        lock (_bcTableIndexLock)
        {
            if (!_bcAppPaths.Contains(appPath, StringComparer.OrdinalIgnoreCase))
            {
                _bcAppPaths.Add(appPath);
                AlRunnerV2.AlEnumMetadataRegistry.RegisterFromAppPath(appPath);
                // Invalidate the index so newly-added .app gets picked up on next miss.
                _bcTableIndex = null;
                _bcSymbolTableIndex = null;
            }
        }
    }

    /// <summary>
    /// On _parsedTables miss for tableId, scan registered BC .app dependencies,
    /// find the matching `table <id>` declaration, and feed it through
    /// TryParseTableFile so _parsedTables gets populated. Returns true iff a
    /// matching table source was found and parsed.
    /// </summary>
    private static bool TryPopulateParsedTableFromBcApps(int tableId)
    {
        lock (_bcTableIndexLock)
        {
            if (_bcMissCache.Contains(tableId)) return false;
            EnsureBcSymbolTableIndex();
            if (_bcSymbolTableIndex != null && _bcSymbolTableIndex.TryGetValue(tableId, out var symbolEntry))
            {
                _parsedTables[tableId] = symbolEntry.Table;
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: parsed table {tableId} from symbols {Path.GetFileName(symbolEntry.AppPath)}");
                return true;
            }

            EnsureBcTableIndex();
            if (_bcTableIndex == null || !_bcTableIndex.TryGetValue(tableId, out var entry))
            {
                _bcMissCache.Add(tableId);
                return false;
            }
            // Parse the source slice that contains this table id.
            TryParseTableFile(entry.Source);
            if (_parsedTables.ContainsKey(tableId))
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: parsed table {tableId} from {Path.GetFileName(entry.AppPath)}");
                return true;
            }
            // Source had a `table N` regex match but TryParseTableFile didn't materialise
            // it — likely a non-table object reusing the keyword. Treat as miss.
            _bcMissCache.Add(tableId);
            return false;
        }
    }

    private static readonly Regex _rxAnyTableId = new(
        @"\btable\s+(\d+)\s+(?:""[^""]+""|[A-Za-z_]\w*)[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Microsoft.BusinessCentral.SystemApp.dll embeds the AL source for NCL-internal
    /// system tables (RecordLink=2000000068, Field=2000000041, Object=2000000038, …)
    /// inside a SystemPackage NAVX stream. Extract it to a temp .app, register the
    /// path with BcAppFallback, and eagerly parse every table the package contains so
    /// PopulateNclMetadataCache writes them to NCLMetadata's cache dict.
    ///
    /// Why eagerly: BC's own NCL code (e.g. `RecordLink.AddLinkAsync` →
    /// `new NavRecord(record, 2000000068)`) calls `NCLMetadata.GetMetaTableById`
    /// directly — bypassing our NavRecordHandle_CreateTarget hook — so lazy
    /// BcAppFallback never fires; the cache dict must be primed up front.
    /// </summary>
    internal static void RegisterSystemAppPackage()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.BusinessCentral.SystemApp");
            if (asm == null)
            {
                try { asm = Assembly.Load("Microsoft.BusinessCentral.SystemApp"); }
                catch { /* fall through */ }
            }
            if (asm == null)
            {
                Console.Error.WriteLine("[RecordPatches] BcAppFallback: SystemApp assembly not loadable; system tables (RecordLink etc.) will fail");
                return;
            }

            var tSystemPackage = asm.GetTypes().FirstOrDefault(t => t.Name == "SystemPackage");
            var mGetStream = tSystemPackage?.GetMethod("GetPackageStream",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (mGetStream == null)
            {
                Console.Error.WriteLine("[RecordPatches] BcAppFallback: SystemPackage.GetPackageStream not found in SystemApp DLL");
                return;
            }

            using var stream = (Stream)mGetStream.Invoke(null, null)!;
            var tempPath = Path.Combine(Path.GetTempPath(), $"al-runner-systemapp-{Guid.NewGuid():N}.app");
            using (var fs = File.Create(tempPath))
                stream.CopyTo(fs);

            _systemAppTempPath = tempPath;
            AddBcAppPath(tempPath);
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: registered SystemPackage → {Path.GetFileName(tempPath)} ({new FileInfo(tempPath).Length:N0} bytes)");

            EagerParseAllBcAppTables();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: SystemApp registration failed: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// Walk every table the BC .app indexes discovered and materialise it in
    /// _parsedTables. Symbols are preferred; AL source is only a fallback.
    /// </summary>
    internal static void EagerParseAllBcAppTables()
    {
        lock (_bcTableIndexLock)
        {
            int parsedNow = 0;
            EnsureBcSymbolTableIndex();
            if (_bcSymbolTableIndex != null)
            {
                foreach (var (id, entry) in _bcSymbolTableIndex)
                {
                    if (_parsedTables.ContainsKey(id)) continue;
                    _parsedTables[id] = entry.Table;
                    parsedNow++;
                }
            }

            if (_bcSymbolTableIndex == null || _bcSymbolTableIndex.Count == 0)
            {
                EnsureBcTableIndex();
                if (_bcTableIndex != null)
                {
                    var alreadySeenSources = new HashSet<string>(ReferenceEqualityComparer.Instance);
                    foreach (var (id, entry) in _bcTableIndex)
                    {
                        if (_parsedTables.ContainsKey(id)) continue;
                        if (!alreadySeenSources.Add(entry.Source)) continue;
                        TryParseTableFile(entry.Source);
                        if (_parsedTables.ContainsKey(id)) parsedNow++;
                    }
                }
            }

            if (parsedNow > 0)
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: eager-parsed {parsedNow} BC table(s) into _parsedTables");
        }
    }

    private static void EnsureBcTableIndex()
    {
        if (_bcTableIndex != null) return;
        var idx = new Dictionary<int, (string, string)>();
        foreach (var appPath in _bcAppPaths)
        {
            IReadOnlyList<(string Name, string Source)> sources;
            try { sources = AlRunnerV2.AppLoader.ExtractAl(appPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: ExtractAl failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var (_, source) in sources)
            {
                if (source.IndexOf("table", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match m in _rxAnyTableId.Matches(source))
                {
                    if (int.TryParse(m.Groups[1].Value, out int id) && !idx.ContainsKey(id))
                        idx[id] = (appPath, source);
                }
            }
        }
        _bcTableIndex = idx;
        if (idx.Count > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} AL-source table id(s) across {_bcAppPaths.Count} BC .app file(s)");
    }

    private static void EnsureBcSymbolTableIndex()
    {
        if (_bcSymbolTableIndex != null) return;
        var idx = new Dictionary<int, (string, ParsedTable)>();
        foreach (var appPath in _bcAppPaths)
        {
            try
            {
                foreach (var json in ReadSymbolReferences(appPath))
                {
                    using var doc = JsonDocument.Parse(json);
                    VisitSymbolTables(doc.RootElement, appPath, idx);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] BcAppFallback: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
            }
        }
        _bcSymbolTableIndex = idx;
        if (idx.Count > 0)
            Console.Error.WriteLine($"[RecordPatches] BcAppFallback: indexed {idx.Count} symbol table id(s) across {_bcAppPaths.Count} BC .app file(s)");
    }

    private static void VisitSymbolTables(JsonElement container, string appPath, Dictionary<int, (string, ParsedTable)> idx)
    {
        if (container.TryGetProperty("Tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
        {
            foreach (var table in tables.EnumerateArray())
            {
                var parsed = TryParseTableSymbol(table);
                if (parsed != null && !idx.ContainsKey(parsed.TableId))
                    idx[parsed.TableId] = (appPath, parsed);
            }
        }
        if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var ns in namespaces.EnumerateArray())
                VisitSymbolTables(ns, appPath, idx);
        }
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
                    calcFormula = TryParseCalcFormula($"CalcFormula = {calcFormulaText};");
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
        return new ParsedTable(tableId, tableName, fields, pkFieldIds, secondaryKeys, isTemporary);
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
        var m = Regex.Match(typeName, @"\[(\d+)\]");
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
}
