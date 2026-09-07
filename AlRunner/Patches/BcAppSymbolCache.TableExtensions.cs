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
/// <param name="CalcFormulaTexts">The raw <c>CalcFormula</c> property TEXT of every FlowField
/// in <paramref name="Fields"/> that declares one, keyed by field id. Carried as text rather
/// than as a parsed <c>ParsedCalcFormula</c> because this parse runs at .app REGISTRATION time
/// (<c>RecordPatches.AddBcAppPath</c>) and must not call into <c>RecordPatches</c>; the
/// consumer parses it once the runtime is up — see
/// <c>RecordPatches.BcAppFallback.EnsureBcSymbolExtensionIndex</c> (#3121).</param>
/// <param name="Keys">The keys the tableextension declares on the table it extends (#3216).
/// Every one is a SECONDARY key — a tableextension cannot restate the primary key — and each
/// carries FIELD NAMES rather than ids, exactly as SymbolReference.json states them
/// (<c>"Keys": [{ "Name": "Key12", "FieldNames": ["Service Item Group"] }]</c>) and exactly as
/// the AL-source path records them, so the two sources hand
/// <c>RecordPatches.MergeExtensionFields</c> the same shape. Empty for the great majority of
/// precompiled extensions: 6 of Base Application 28.1's 90 tableextensions declare any keys at
/// all.</param>
internal sealed record TableExtensionSymbol(
    int ExtensionId,
    string ExtensionName,
    string TargetTableName,
    List<ParsedField> Fields,
    IReadOnlyDictionary<int, string>? CalcFormulaTexts = null,
    List<ParsedExtensionKey>? Keys = null);

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
    /// do NOT refactor into a shared helper. A prior attempt caused SIGSEGV. Keeping it a copy
    /// is also why it drifts: every property the table loop learns to read has to be added here
    /// by hand, and both of the ones below were missed for as long as they existed.
    ///
    /// <para><b>The "must not call RecordPatches at startup" constraint is FALSE, and nothing in
    /// this file rests on it any more.</b> It is written out here because two separate changes
    /// (#3121/#3180 and #3177/#3197) were designed around it and a third would have been. What it
    /// used to say: parsing a property here calls into <c>RecordPatches</c>, which "may not yet be
    /// initialised → SIGSEGV". Measured, there is no point in the process at which that is
    /// reachable. <c>RecordPatches.AddBcAppPath</c> — the only registration-time caller — runs
    /// these two statements consecutively, inside one <c>lock (_bcTableIndexLock)</c>:
    /// <code>
    /// symbols = BcAppSymbolCache.Get(appPath);      // -> TryParseTableSymbol
    ///                                               //    -> RecordPatches.TryParseRelationArmsText (#2528)
    /// BcAppSymbolCache.GetTableExtensions(appPath); // -> this method
    /// </code>
    /// so the extension loop can never run earlier than the table loop that has already called
    /// into <c>RecordPatches</c>. The other caller (the lazy index rebuild in
    /// <c>RecordPatches.BcAppFallback</c>) is later still. The TableRelation read below is a
    /// direct <c>RecordPatches.TryParseRelationArmsText</c> call at parse time, and it is
    /// exercised on every corpus and runner-extras run, cold and warm — see the #3177 paragraph
    /// for the numbers.</para>
    ///
    /// <para><b>Consequence for the CalcFormula half.</b> <c>ParsedField.CalcFormula</c> is still
    /// null here and the raw property TEXT is carried on
    /// <see cref="TableExtensionSymbol.CalcFormulaTexts"/> for the consumer to parse once the
    /// runtime is up (#3121/#3180). That deferral WORKS and is not being changed here — but it is
    /// a design choice now, not a requirement: the startup hazard it was built to avoid does not
    /// exist. Anyone consolidating the two properties onto one mechanism should know that either
    /// direction is open, and should not re-derive the constraint from this comment.</para>
    ///
    /// <para>The other sentence that used to stand here — "Extension FlowFields with CalcFormulas
    /// don't exist in standard precompiled BC apps" — is also false, and separately measured.
    /// Against BC 28.1: <c>Customer."Outstanding Serv.Invoices(LCY)"</c> (Service) and
    /// <c>"Stockkeeping Unit"."Qty. on Prod. Order"</c> (Manufacturing) are exactly that shape,
    /// and dropping their formula made <c>CalcFields</c> refuse them with BC's own
    /// "You must define a CalcFormula for the {0} FlowField in the {1} table". Counted across the
    /// BC 28.4 platform packages, Base Application declares <b>36</b> FlowFields on
    /// tableextensions and all 36 carry a CalcFormula.</para>
    ///
    /// <para>#3177 — TableRelation is read below, directly. #2528 taught
    /// <see cref="TryParseTableSymbol"/> to re-parse a precompiled table field's TableRelation
    /// out of the symbol property text, because without it <c>FieldRef.Relation</c> answers 0
    /// and <c>Validate</c> skips the relation check, so a value real BC refuses is silently
    /// accepted — a wrong ANSWER, not a missing feature. The extension loop never got that
    /// change, so the same class, reading the same package, disagreed with itself about the
    /// same property.</para>
    ///
    /// <para><b>261</b> tableextension fields across the platform packages carry a
    /// TableRelation, of which <b>260</b> gain one here — the gate below excludes exactly one,
    /// <c>Customer."Ship-to Filter"</c> (5903, a FlowFilter). #3177 was filed with 154, which
    /// is the <b>Base Application share</b>, not an older count: the other 107 are in Business
    /// Foundation, an equally precompiled dependency read through this same loop. The total does
    /// NOT drift with the BC version — measured 154 + 107 = 261 identically on 28.1 and 28.4
    /// (259 on 27.5). Examples: <c>Item."Routing No."</c> → <c>"Routing Header"</c>,
    /// <c>Item."Production BOM No."</c> → <c>"Production BOM Header"</c>,
    /// <c>Customer."Service Zone Code"</c> → <c>"Service Zone"</c> (table 5957, contributed by
    /// tableextension 6450 "Serv. Customer" — the case
    /// <c>tests/runner-extras/precompiled-table-relation</c> asserts end to end).</para>
    ///
    /// <para>No <c>CacheVersion</c> bump: table extensions are not part of <c>CachePayload</c>
    /// and <see cref="TableExtensionCache"/> is process-only, so there is no persisted payload
    /// that could replay the pre-fix answer — unlike #2528, which needed one for exactly that
    /// reason. Everything downstream that IS disk-cached (AL output, install baseline) keys on
    /// <c>RunnerFingerprint</c>, which includes the runner assembly's content hash.</para>
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
        var calcFormulaTexts = new Dictionary<int, string>();
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
                // ParsedField.CalcFormula stays null here; the raw text rides along on the
                // extension symbol and is parsed by the consumer — see doc-comment above.
                if (isFlowField && props.TryGetValue("CalcFormula", out var calcFormulaText)
                    && !string.IsNullOrWhiteSpace(calcFormulaText))
                    calcFormulaTexts[fieldId] = calcFormulaText;
                props.TryGetValue("OptionMembers", out var optionMembers);
                props.TryGetValue("InitValue", out var initValue);
                var isAutoIncrement = props.TryGetValue("AutoIncrement", out var autoIncrement)
                    && (autoIncrement == "1" || autoIncrement.Equals("true", StringComparison.OrdinalIgnoreCase));
                props.TryGetValue("MinValue", out var minValue); // #2495
                props.TryGetValue("MaxValue", out var maxValue);
                // #3177 — TableRelation, read exactly as the table loop reads it (#2528/#2518).
                // Both properties, independently: ValidateTableRelation = 0 turns the CHECK off
                // while leaving the relation itself readable, so reading only the first would
                // switch validation on wholesale for fields BC does not validate.
                //
                // Gated on field class to match the table loop, whose reason is that a
                // FlowFilter's TableRelation is a lookup hint for the filter's own UI rather
                // than a stored value's referential constraint, and that RelationArms also feeds
                // the reverse index NCLMetaTable_ComputeReferencingRelations builds for rename
                // propagation, which filters on table id rather than field class. Note the
                // blast radius here is NOT the 204 fields #2528 cites on the table path: across
                // the platform packages this gate excludes exactly ONE extension field,
                // Customer."Ship-to Filter" (5903, FlowFilter), so 260 of the 261 gain relations.
                // It is kept anyway because the point of the change is that the two loops read
                // the property the same way; ungated they would disagree again, in the other
                // direction, over that one field.
                props.TryGetValue("TableRelation", out var tableRelation);
                var relationArms = (!isFlowField && !isFlowFilter)
                    ? RecordPatches.TryParseRelationArmsText(tableRelation, fieldName)
                    : null;
                var relationValidate = !(props.TryGetValue("ValidateTableRelation", out var vtr)
                    && (vtr == "0" || vtr.Equals("false", StringComparison.OrdinalIgnoreCase)));
                fields.Add(new ParsedField(fieldId, fieldName, typeName, SymbolTypeLength(typeName), isFlowField, null,
                    optionMembers, initValue, isAutoIncrement, IsFlowFilter: isFlowFilter,
                    RelationArms: relationArms, RelationValidate: relationValidate,
                    MinValue: minValue, MaxValue: maxValue));
            }
        }
        // #3216 — the extension's own keys. Same JSON shape the base-table reader consumes in
        // BcAppSymbolCache.cs (Keys[].Name + Keys[].FieldNames), minus the "first key is the
        // PK" split, which does not apply to a tableextension. Names are passed through
        // unresolved: the target table may be a source-parsed table in this bundle that has not
        // been read yet, so there is no field list to resolve against here.
        var keys = new List<ParsedExtensionKey>();
        if (ext.TryGetProperty("Keys", out var keysJson) && keysJson.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var key in keysJson.EnumerateArray())
            {
                var keyName = key.TryGetProperty("Name", out var keyNameProp)
                    ? keyNameProp.GetString() ?? "Key"
                    : "Key";
                var fieldNames = new List<string>();
                if (key.TryGetProperty("FieldNames", out var fieldNamesJson)
                    && fieldNamesJson.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var fieldNameJson in fieldNamesJson.EnumerateArray())
                    {
                        var fieldName = fieldNameJson.GetString();
                        if (!string.IsNullOrWhiteSpace(fieldName)) fieldNames.Add(fieldName);
                    }
                }
                if (fieldNames.Count > 0)
                    keys.Add(new ParsedExtensionKey(keyName, fieldNames));
            }
        }

        return new TableExtensionSymbol(extId, extName, targetTableName, fields,
            calcFormulaTexts.Count > 0 ? calcFormulaTexts : null, keys);
    }
}
