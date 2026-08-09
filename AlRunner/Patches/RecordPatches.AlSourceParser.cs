// RecordPatches.AlSourceParser — parses AL `table` / `tableextension` declarations into
// ParsedTable records keyed by table ID. The output is consumed by NclMetaTableBuilder to
// produce real NCLMetaTable instances at runtime.
//
// Syntax-level extraction runs on Microsoft.Dynamics.Nav.CodeAnalysis' own AL parser — the
// same front end BcCompiler.Emit already runs over the very same files (#1696). It replaced
// a set of regexes over raw .al text whose failure mode was a SILENT WRONG VALUE rather than
// a crash: `[^;]+` could not cross a semicolon inside a string literal (`InitValue =
// 'Open; pending review'` captured `'Open`), a comment mentioning a property name was read
// as that property (#1690), quoting was captured inconsistently (#1674), and object
// boundaries were guessed by slicing between regex matches. A syntax tree answers all four
// structurally: comments are trivia and simply are not in it.
//
// `SyntaxTree.ParseObjectText` needs only a ParseOptions — no Compilation, no reference
// closure — so this works on every input the parser takes: real files, AL extracted from
// dependency .app archives, and the table text NclMetaTableBuilder synthesizes.
//
// STILL REGEX: the CalcFormula sub-parse below. It is fed the formula's own syntax node text
// (so it no longer sees comments or neighbouring properties), but the structural mapping onto
// QualifiedNameSyntax / WhereExpressionSyntax is deliberately deferred — #1696 orders it last,
// and RecordPatches.TryParseCalcFormula has an external caller in BcAppSymbolCache that hands
// it synthesized text rather than a node.
using System.Text.RegularExpressions;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Matches BcCompiler.Emit's options so this parse sees the same source the emit does —
    // notably the CLEANSCHEMA1..25 preprocessor symbols, which gate real field declarations
    // in the BaseApp. DocumentationMode.None: doc comments are trivia we never read.
    private static readonly NavCA.ParseOptions AlParseOptions = new(
        runtimeVersion: null!,
        preprocessorSymbols: Enumerable.Range(1, 25).Select(n => $"CLEANSCHEMA{n}"),
        documentationMode: NavCA.DocumentationMode.None);

    // Field type text still yields its length by pattern (`Code[10]` → 10). The type is one
    // token's text with no nesting, so there is nothing structural for a tree to add here.
    private static readonly Regex RxTypeLength = new(@"\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Identifiers come off the tree with AL's quoting intact — <c>"Entry No."</c>, not
    /// <c>Entry No.</c>. Every consumer (key resolution, tableextension merge, metatable
    /// lookup) matches on the bare name, so the quotes come off exactly once, here.
    /// (<c>Unquote</c> itself lives in RecordPatches.NclMetaQueryBuilder.cs — same partial
    /// class, same rule.)
    /// </summary>
    private static string IdentText(NavSyntax.IdentifierNameSyntax? id) =>
        id == null ? "" : Unquote(id.Identifier.ValueText ?? id.Identifier.Text ?? "");

    /// <summary>
    /// The caption literal declared by <c>Caption = 'text'</c>, or null when the field
    /// declares none — in which case BC's own field-name fallback is the correct answer and
    /// must stand.
    /// <para>Only the LABEL LITERAL is the caption. A label may carry trailing parts
    /// (<c>Caption = 'It''s on', Comment='x';</c>) and the property value node's text spans
    /// all of them, so reading the node wholesale would append <c>, Comment='x'</c> to the
    /// caption. <see cref="NavSyntax.LabelSyntax.LabelText"/> is just the literal.
    /// Doubled single quotes are AL's escape for an embedded quote.</para>
    /// </summary>
    private static string? CaptionFrom(NavSyntax.PropertyValueSyntax? value)
    {
        if (value is not NavSyntax.LabelPropertyValueSyntax label) return null;
        var text = label.Value?.LabelText?.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') text = text[1..^1];
        return text.Replace("''", "'");
    }

    /// <summary>Property lookup by AL name, case-insensitive as AL itself is.</summary>
    private static NavSyntax.PropertyValueSyntax? PropValue(
        NavSyntax.PropertyListSyntax? list, string name)
    {
        if (list == null) return null;
        // Properties is a list of PropertySyntaxOrEmpty: a stray `;` in a property list is
        // legal AL and parses as an empty entry, which simply has no name to match.
        foreach (var entry in list.Properties)
        {
            if (entry is not NavSyntax.PropertySyntax p) continue;
            if (string.Equals(p.Name?.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        }
        return null;
    }

    private static bool PropIs(NavSyntax.PropertyListSyntax? list, string name, string expected) =>
        string.Equals(PropValue(list, name)?.ToString()?.Trim(), expected,
            StringComparison.OrdinalIgnoreCase);

    private static readonly Regex RxCalcFormula = new(
        @"\bCalcFormula\s*=\s*([^;]+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Captures: (type) table ["."field] [where(filters)]
    // Groups: 1=type, 2=table(quoted), 3=table(unquoted), 4=field(quoted), 5=field(unquoted), 6=where
    //
    // The table name alternation (quoted OR bare identifier) mirrors the field name
    // alternation next to it — a single-word AL object name (e.g. `PageworksDSFieldConfigLine`)
    // is legal WITHOUT quotes, exactly like a single-word field name already was handled.
    // Before this fix the table name was quoted-only: `lookup(PageworksDSFieldConfigLine.
    // TargetTableNo where(...))` (a genuine, common AL pattern — no spaces in the name, so no
    // quotes needed) silently failed this regex, TryParseCalcFormula returned null, and the
    // FlowField's NCLMetaField.CalculationFormula was left at EmptyFormula — CalcFields()
    // became a silent no-op, leaving the field at its type default (e.g. 0) instead of the
    // real looked-up value. Verified via ilspycmd/live trace: FlowFieldPatches.
    // RecordImpl_CalcFieldsAsync_3 logged "formula == EmptyFormula, skipping" for exactly this
    // field, and a runner-extras repro with a matching unquoted table name reproduced a
    // FlowField silently resolving to 0 instead of the seeded value.
    private static readonly Regex RxCalcFormulaParts = new(
        @"^\s*(count|sum|lookup|exist|average|min|max)\s*\(\s*(?:""([^""]+)""|([A-Za-z_]\w*))(?:\.(?:""([^""]+)""|([A-Za-z_]\w*)))?\s*(?:where\s*\((.+)\))?\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    // Captures field-reference filter: "SourceField"|Unquoted = field("ParentField"|Unquoted)
    // Groups: 1=srcField(quoted), 2=srcField(unquoted), 3=parentField(quoted), 4=parentField(unquoted)
    private static readonly Regex RxCalcFilter = new(
        @"(?:""([^""]+)""|([A-Za-z_]\w*))\s*=\s*field\s*\(\s*(?:""([^""]+)""|([A-Za-z_]\w*))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void ParseAllSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                TryParseTableFile(text);
                TryParseTableExtensionFile(text);
            }
        }
    }

    /// <summary>
    /// Parses every AL object in <paramref name="text"/> with BC's own parser and returns the
    /// object declarations. Never throws: this is fed arbitrary .al text — pages, codeunits,
    /// AL sliced out of dependency .app archives, and synthesized table text — and a parse it
    /// cannot make sense of must leave the caller's state untouched, not break the run.
    /// Diagnostics are ignored on purpose: a file that fails to compile for an unrelated
    /// reason still yields a usable table declaration, which is what the regexes did too.
    /// </summary>
    private static IReadOnlyList<NavCA.SyntaxNode> ParseAlObjects(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            var tree = NavSyntax.SyntaxTree.ParseObjectText(
                text, path: "", encoding: null!, AlParseOptions, default);
            if (tree.GetRoot() is not NavSyntax.CompilationUnitSyntax root) return [];
            return root.ChildNodes().ToList();
        }
        catch
        {
            // A malformed input is not a runner gap — the AL simply is not parseable, and the
            // caller's contract is "extract what you can". Callers that need a table and do
            // not get one already report that themselves ("AL source not parsed").
            return [];
        }
    }

    /// <summary>Builds a <see cref="ParsedField"/> from one <c>field(...)</c> declaration.</summary>
    private static ParsedField? ParseFieldSyntax(NavSyntax.FieldSyntax f, bool tableLevelExtras)
    {
        if (f.No.Value is not int fid) return null;
        var fname = IdentText(f.Name);
        var ftype = f.Type?.ToString()?.Trim() ?? "";
        int length = 0;
        var lm = RxTypeLength.Match(ftype);
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out length);

        var props = f.PropertyList;
        bool isFlowField = PropIs(props, "FieldClass", "FlowField");

        ParsedCalcFormula? calcFormula = null;
        if (isFlowField && PropValue(props, "CalcFormula") is { } formulaNode)
        {
            // Hand TryParseCalcFormula the formula's own node text in the shape its regex
            // expects. The text now comes from the tree, so comments and neighbouring
            // properties can no longer bleed into it.
            calcFormula = TryParseCalcFormula($"CalcFormula = {formulaNode};");
        }

        // Option-type fields: OptionMembers is the comma-separated list BC's
        // NCLOptionMetadata constructor expects. Tokens are trimmed; empty entries are kept
        // (BC allows blank members, and #1674 depends on that).
        string? optionMembers = null;
        if (ftype.Equals("Option", StringComparison.OrdinalIgnoreCase)
            && PropValue(props, "OptionMembers") is { } om)
        {
            optionMembers = string.Join(",", om.ToString().Split(',').Select(s => s.Trim()));
        }

        // InitValue is passed to MetaField.initValue as RAW AL TEXT, quotes and all, because
        // NclMetaTableBuilder does the type-aware unquoting downstream — that split is what
        // #1674's blank-enum fix depends on. Do not "clean" it here without deleting the
        // stripping there in the same change.
        string? initValueText = PropValue(props, "InitValue")?.ToString()?.Trim();

        bool isAutoIncrement = tableLevelExtras && PropIs(props, "AutoIncrement", "true");
        var caption = CaptionFrom(PropValue(props, "Caption"));

        return new ParsedField(fid, fname, ftype, length, isFlowField, calcFormula,
            tableLevelExtras ? optionMembers : null, initValueText, isAutoIncrement, caption);
    }

    private static void TryParseTableFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.TableSyntax table) continue;
            if (table.ObjectId?.Value.Value is not int tableId) continue;
            var tableName = IdentText(table.Name);

            var fields = new List<ParsedField>();
            if (table.Fields != null)
                foreach (var f in table.Fields.Fields)
                    if (ParseFieldSyntax(f, tableLevelExtras: true) is { } pf)
                        fields.Add(pf);

            // First key is the PK; all subsequent keys are secondary.
            var pkFieldIds = new List<int>();
            var secondaryKeys = new List<ParsedKey>();
            bool firstKey = true;
            if (table.Keys != null)
            {
                foreach (var k in table.Keys.Keys)
                {
                    var keyName = IdentText(k.Name);
                    var keyFieldIds = new List<int>();
                    foreach (var kf in k.Fields)
                    {
                        var kn = IdentText(kf as NavSyntax.IdentifierNameSyntax);
                        var f = fields.FirstOrDefault(x =>
                            string.Equals(x.FieldName, kn, StringComparison.OrdinalIgnoreCase));
                        if (f != null) keyFieldIds.Add(f.FieldId);
                    }
                    if (firstKey)
                    {
                        pkFieldIds.AddRange(keyFieldIds);
                        firstKey = false;
                    }
                    else if (keyFieldIds.Count > 0)
                    {
                        secondaryKeys.Add(new ParsedKey(keyName, keyFieldIds));
                    }
                }
            }
            // Fallback: first field is PK
            if (pkFieldIds.Count == 0 && fields.Count > 0)
                pkFieldIds.Add(fields[0].FieldId);

            // DataPerCompany: AL's default is TRUE, so only the explicit opt-out is parsed.
            // MetaTable's own ctor default for isDataPerCompany is false — the opposite of
            // AL's — and BC's RecordImplementation.ChangeCompany returns true immediately for
            // a table that is not per-company.
            var isTableTypeTemporary = PropIs(table.PropertyList, "TableType", "Temporary");
            var dataPerCompany = !PropIs(table.PropertyList, "DataPerCompany", "false");
            _parsedTables[tableId] = new ParsedTable(tableId, tableName, fields, pkFieldIds,
                secondaryKeys, isTableTypeTemporary, dataPerCompany);
        }
    }

    private static void TryParseTableExtensionFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.TableExtensionSyntax ext) continue;
            if (ext.ObjectId?.Value.Value is not int extId) continue;
            var extName = IdentText(ext.Name);
            var baseName = Unquote(ext.BaseObject?.ToString()?.Trim() ?? "");

            // tableLevelExtras: false keeps this path byte-identical to what it produced
            // before — extension fields carried neither OptionMembers nor AutoIncrement.
            // Both are plausibly wanted here, but adding them is a behaviour change that
            // needs its own test, not a silent rider on a parser swap.
            var fields = new List<ParsedField>();
            // OfType<FieldSyntax>: a tableextension's field list also holds `modify(...)`
            // entries, which declare no new field. The regex only ever matched
            // `field(N; Name; Type)` either, so this keeps the same set.
            if (ext.Fields != null)
                foreach (var f in ext.Fields.Fields.OfType<NavSyntax.FieldSyntax>())
                    if (ParseFieldSyntax(f, tableLevelExtras: false) is { } pf)
                        fields.Add(pf);

            Console.Error.WriteLine($"[TableExt] parsed extension {extId} '{extName}' extends '{baseName}' with {fields.Count} fields");

            var key = baseName.ToLowerInvariant();
            // De-dup by field id (mirrors the symbol-index merge in
            // RecordPatches.BcAppFallback.cs's EnsureBcSymbolExtensionIndex): the same
            // extension source file can legitimately be scanned more than once (e.g. a
            // dependency app's source dir registered both by its own suite AND by
            // BuildSiblingSourceDeps' sibling-source discovery — see #1686), and without
            // this guard the same field id lands twice in the merged list. A duplicated
            // NCLMetaField with the same FieldNo corrupts NCLMetaTable.AssignFromMetaTable's
            // positional field-count arithmetic, which crashes deep inside
            // NCLMetaTable.SetSystemFields() with a bare NullReferenceException — surfaced
            // to the caller as the misleading "no NCLMetaTable ... (AL source not parsed)".
            if (!_parsedExtensionFields.TryGetValue(key, out var existing))
                _parsedExtensionFields[key] = fields;
            else
            {
                var existingIds = new HashSet<int>(existing.Select(f => f.FieldId));
                foreach (var f in fields)
                    if (existingIds.Add(f.FieldId))
                        existing.Add(f);
            }

            // The base table's NCLMetaTable may ALREADY be built and cached at this point:
            // a table pulled in from a precompiled dependency .app is materialised lazily
            // the moment something references it, which happens during source parsing —
            // i.e. BEFORE this tableextension is parsed. BuildNCLMetaTable merges
            // _parsedExtensionFields at build time only, so that cached metatable is frozen
            // without the extension's fields, and every AL access to one of them dies in
            // NCLMetaTable_GetFieldByNoExt ("extension field N ... not found").
            // Evict it so the next access rebuilds WITH the merge. Safe here: source
            // parsing runs before any AL test code, so nothing holds the stale instance.
            EvictCachedMetaTableForBaseTable(baseName);

            // Record the extension object id so its emitted TableExtension{extId} CLR type
            // can be instantiated and registered on each record of the base table — this is
            // what makes the extension's record-level triggers (OnInsert/OnModify/OnDelete/
            // OnRename) and field-validate triggers fire. Preserve declaration order and
            // de-dup (the same extension file is scanned from multiple source dirs).
            if (!_extensionIdsByBaseTable.TryGetValue(key, out var extIds))
                _extensionIdsByBaseTable[key] = extIds = new List<int>();
            if (!extIds.Contains(extId))
                extIds.Add(extId);
        }
    }

    /// <summary>
    /// Drop any cached NCLMetaTable built for <paramref name="baseTableName"/> before its
    /// tableextension fields were known, so the next lookup rebuilds it with them merged.
    /// No-op when the table has not been built yet (the common, in-order case).
    /// </summary>
    private static void EvictCachedMetaTableForBaseTable(string baseTableName)
    {
        foreach (var kvp in _parsedTables)
        {
            if (!string.Equals(kvp.Value.TableName, baseTableName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_metaTableCache.TryRemove(kvp.Key, out _))
                Console.Error.WriteLine(
                    $"[TableExt] evicted stale NCLMetaTable {kvp.Key} '{baseTableName}' " +
                    $"(built before its tableextension fields were parsed)");
        }
    }

    internal static ParsedCalcFormula? TryParseCalcFormula(string fieldBody)
    {
        var m = RxCalcFormula.Match(fieldBody);
        if (!m.Success) return null;
        var formulaText = m.Groups[1].Value.Trim();
        var pm = RxCalcFormulaParts.Match(formulaText);
        if (!pm.Success) return null;

        var formulaType = pm.Groups[1].Value;
        var sourceTableName = pm.Groups[2].Success && pm.Groups[2].Length > 0 ? pm.Groups[2].Value
                            : pm.Groups[3].Value;
        var sourceFieldName = pm.Groups[4].Success && pm.Groups[4].Length > 0 ? pm.Groups[4].Value
                            : pm.Groups[5].Success && pm.Groups[5].Length > 0 ? pm.Groups[5].Value : null;
        var whereText = pm.Groups[6].Success ? pm.Groups[6].Value : "";

        var filters = new List<ParsedCalcFilter>();
        foreach (Match fm in RxCalcFilter.Matches(whereText))
            filters.Add(new ParsedCalcFilter(
                fm.Groups[1].Success && fm.Groups[1].Length > 0 ? fm.Groups[1].Value : fm.Groups[2].Value,
                fm.Groups[3].Success && fm.Groups[3].Length > 0 ? fm.Groups[3].Value : fm.Groups[4].Value));

        Console.Error.WriteLine($"[CalcFormula] parsed {sourceTableName}.{sourceFieldName ?? "*"} type={formulaType} filters={filters.Count}");
        return new ParsedCalcFormula(formulaType, sourceTableName, sourceFieldName, filters);
    }
}

// ─── Data holders ────────────────────────────────────────────────────────────

internal record ParsedCalcFilter(string SourceFieldName, string ParentFieldName);
internal record ParsedCalcFormula(string FormulaType, string SourceTableName, string? SourceFieldName, List<ParsedCalcFilter> Filters);

internal record ParsedField(int FieldId, string FieldName, string TypeName, int Length, bool IsFlowField = false, ParsedCalcFormula? CalcFormula = null, string? OptionMembers = null, string? InitValueText = null, bool IsAutoIncrement = false, string? Caption = null);
internal record ParsedKey(string Name, List<int> FieldIds);
internal record ParsedTable(int TableId, string TableName,
    List<ParsedField> Fields, List<int> PkFieldIds, List<ParsedKey>? SecondaryKeys = null,
    bool IsTableTypeTemporary = false, bool DataPerCompany = true);
