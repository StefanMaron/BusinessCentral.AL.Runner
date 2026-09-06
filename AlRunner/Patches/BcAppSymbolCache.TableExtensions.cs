// BcAppSymbolCache.TableExtensions — additive table-extension parsing for precompiled .app packages.
//
// CRITICAL DESIGN NOTE — DO NOT MERGE INTO BcAppSymbolCache.cs:
// Adding ANY new types, fields, or methods directly to BcAppSymbolCache.cs causes SIGSEGV in
// R2R-precompiled BC code. The root cause is a token-shift: BcAppSymbolCache.cs is a compilation
// unit whose method tokens are baked into the AL compiler's R2R code. Changing that compilation
// unit shifts later method tokens → wrong native jump targets → SIGSEGV.
// This file uses C# `partial class` so it shares access to BcAppSymbolCache's private helpers
// (ReadSymbolReferences, SymbolTypeName, SymbolTypeLength, SymbolProperties) without modifying
// the original compilation unit.
//
// See also: TableExtensionSymbol.cs (the data record, also in a separate file for the same reason).

using System.Collections.Concurrent;

namespace AlRunner.Patches;

/// <summary>
/// A tableextension parsed from a precompiled .app's SymbolReference.json.
/// Declared here (not in BcAppSymbolCache.cs) to avoid token-shift SIGSEGV — see file header.
/// <para>TargetTableName has the #appId# prefix stripped from TargetObject.</para>
/// </summary>
internal sealed record TableExtensionSymbol(
    int ExtensionId,
    string ExtensionName,
    string TargetTableName,
    List<ParsedField> Fields);

internal static partial class BcAppSymbolCache
{
    // Separate cache for table extensions: keyed by the same key string as ProcessCache,
    // valued by the parsed extensions for that .app. Never modifies AppSymbols.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<TableExtensionSymbol>> TableExtensionCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the parsed TableExtension objects for the given .app file.
    /// Uses a separate cache (TableExtensionCache) so that AppSymbols is never modified —
    /// changing AppSymbols also causes SIGSEGV via the same token-shift mechanism.
    /// Called eagerly from <c>RecordPatches.AddBcAppPath</c> at registration and again from
    /// <c>RecordPatches.BcAppFallback.EnsureBcSymbolExtensionIndex</c> (a cache hit by then,
    /// unless the file changed on disk).
    /// <para>Either the parse completes and its result is cached, or it throws
    /// <see cref="AlRunner.Infrastructure.BcAppSymbolReadException"/> and nothing is stored —
    /// the store below is only reached on a complete parse (#2712).</para>
    /// </summary>
    internal static IReadOnlyList<TableExtensionSymbol> GetTableExtensions(string appPath)
    {
        // Content, not a stat — issue #2846 case 2. This key used to read
        //     $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|v{CacheVersion}"
        // while `Get` in the same class, over the same files, keyed the same question on
        // ComputeAppContentHash. #1820 replaced the Length/LastWriteTimeUtc stat there for the
        // reason #1815 recorded one layer over: CI re-downloads every platform and test-toolkit
        // .app on every run, so the mtime is fresh even when the bytes are identical, and an
        // mtime-keyed entry MISSes unconditionally regardless of content. GetTableExtensions
        // never got that treatment, so a touched-but-unchanged package reparsed every
        // tableextension in it — the whole of Base Application's SymbolReference.json, the same
        // parse #2712 measured as worth 96 extensions and 47 test results when it went wrong.
        //
        // The second half is the invariant: two members of one class, describing one package,
        // disagreeing about which byte state of it they had read. ComputeAppContentHash is
        // memoized per full path (see its comment) and `Get` computes it for these same .app
        // files anyway, so aligning costs a dictionary lookup and makes the table index and the
        // table-extension index provably describe the same read.
        //
        // The full path stays in the key. It is what keeps two byte-identical packages in
        // different directories from sharing an entry, and dropping it is not part of this
        // change.
        var key = $"{System.IO.Path.GetFullPath(appPath)}|hash:{ComputeAppContentHash(appPath)}|v{CacheVersion}";
        if (TableExtensionCache.TryGetValue(key, out var cached))
            return cached;
        var parsed = ParseTableExtensions(appPath);
        TableExtensionCache[key] = parsed;
        return parsed;
    }

    /// <summary>
    /// Parse every tableextension the .app's SymbolReference.json declares — all of them or
    /// none. This used to catch every exception, log it only to PerfTrace (off unless
    /// AL_RUNNER_PERF=1) and return the PARTIAL dictionary collected so far, which
    /// <see cref="GetTableExtensions"/> then cached for the life of the process. Reported as
    /// #2712: an OutOfMemoryException part-way through Base Application's symbols dropped
    /// 90 of 96 extensions, and the run reported 47 extra ordinary-looking test failures
    /// ("field 5912 cannot be found in the 'Customer' table") with an unchanged exit code.
    /// Mirrors <see cref="Parse"/>, which has never caught here either.
    /// </summary>
    private static IReadOnlyList<TableExtensionSymbol> ParseTableExtensions(string appPath)
    {
        var result = new Dictionary<int, TableExtensionSymbol>();
        try
        {
            foreach (var json in ReadSymbolReferences(appPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                VisitTableExtensions(doc.RootElement, result);
            }
        }
        catch (Exception ex)
        {
            // Typed and loud: the caller (RecordPatches.AddBcAppPath at registration, or the
            // lazy index rebuild) lets this propagate, and Program.cs turns it into a FATAL
            // exit — a run that cannot see a dependency's table extensions cannot produce
            // meaningful results (.claude/rules/loud-failures.md).
            throw new AlRunner.Infrastructure.BcAppSymbolReadException(appPath, "table extensions", ex);
        }
        return result.Values.ToList();
    }

    private static void VisitTableExtensions(System.Text.Json.JsonElement container, Dictionary<int, TableExtensionSymbol> tableExts)
    {
        if (container.TryGetProperty("TableExtensions", out var extArray) && extArray.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var ext in extArray.EnumerateArray())
            {
                var parsed = TryParseTableExtensionSymbol(ext);
                if (parsed != null && !tableExts.ContainsKey(parsed.ExtensionId))
                    tableExts[parsed.ExtensionId] = parsed;
            }
        }
        if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var ns in namespaces.EnumerateArray())
                VisitTableExtensions(ns, tableExts);
        }
    }

    /// <summary>
    /// Parse a single TableExtension entry from SymbolReference.json.
    /// TargetObject: <c>#&lt;appIdNoHyphens&gt;#&lt;TableName&gt;</c> or plain <c>&lt;TableName&gt;</c>.
    ///
    /// Field-parse loop is an intentional copy of the loop in <see cref="TryParseTableSymbol"/> —
    /// do NOT refactor into a shared helper. A prior attempt caused SIGSEGV.
    /// CalcFormula is intentionally null: parsing it calls RecordPatches.TryParseCalcFormula,
    /// which must not be called at startup (RecordPatches may not yet be initialised → SIGSEGV).
    /// Extension FlowFields with CalcFormulas don't exist in standard precompiled BC apps.
    /// </summary>
    private static TableExtensionSymbol? TryParseTableExtensionSymbol(System.Text.Json.JsonElement ext)
    {
        if (!ext.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var extId))
            return null;
        var extName = ext.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? $"TableExt{extId}"
            : $"TableExt{extId}";

        var targetRaw = ext.TryGetProperty("TargetObject", out var targetProp)
            ? targetProp.GetString() ?? string.Empty
            : string.Empty;
        string targetTableName;
        if (targetRaw.StartsWith('#'))
        {
            var secondHash = targetRaw.IndexOf('#', 1);
            targetTableName = secondHash >= 0 ? targetRaw.Substring(secondHash + 1) : targetRaw;
        }
        else
        {
            targetTableName = targetRaw;
        }
        if (string.IsNullOrEmpty(targetTableName)) return null;

        var fields = new List<ParsedField>();
        if (ext.TryGetProperty("Fields", out var fieldsJson) && fieldsJson.ValueKind == System.Text.Json.JsonValueKind.Array)
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
                // #1716 — a tableextension may add the FlowFilter field a FlowField reads.
                var isFlowFilter = props.TryGetValue("FieldClass", out var fieldClass2)
                    && string.Equals(fieldClass2, "FlowFilter", StringComparison.OrdinalIgnoreCase);
                // CalcFormula intentionally null — see doc-comment above.
                props.TryGetValue("OptionMembers", out var optionMembers);
                props.TryGetValue("InitValue", out var initValue);
                var isAutoIncrement = props.TryGetValue("AutoIncrement", out var autoIncrement)
                    && (autoIncrement == "1" || autoIncrement.Equals("true", StringComparison.OrdinalIgnoreCase));
                props.TryGetValue("MinValue", out var minValue); // #2495
                props.TryGetValue("MaxValue", out var maxValue);
                fields.Add(new ParsedField(fieldId, fieldName, typeName, SymbolTypeLength(typeName), isFlowField, null,
                    optionMembers, initValue, isAutoIncrement, IsFlowFilter: isFlowFilter,
                    MinValue: minValue, MaxValue: maxValue));
            }
        }
        return new TableExtensionSymbol(extId, extName, targetTableName, fields);
    }
}
