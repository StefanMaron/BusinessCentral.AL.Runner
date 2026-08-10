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
// CalcFormula is mapped structurally too: `sum/lookup/…` and `count/exist` are two different
// node types, and each filter condition carries its own type, so `X = field(Y)` filters are
// selected BY TYPE rather than by a pattern that happened not to match `const(...)`. The one
// regex left in this file extracts a length from a type's text (`Code[10]` → 10), which has no
// structure for a tree to add.
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

    /// <summary>
    /// The declared object id of any AL object that has one, or null. `interface`,
    /// `controladdin` and `profile` have no object id at all — they do not derive from
    /// <c>ApplicationObjectSyntax</c> — which is the same set the id-keyed parsers already
    /// excluded, for the same reason (AllObj is keyed by (type, id); a synthetic id would be
    /// a fabrication).
    /// </summary>
    private static int? ObjectIdOf(NavCA.SyntaxNode obj) =>
        obj is NavSyntax.ApplicationObjectSyntax ao && ao.ObjectId?.Value.Value is int id ? id : null;

    /// <summary>
    /// The AL object-kind name used as the first half of the `(Kind, Id)` keys and as AllObj's
    /// "Object Type". These strings are a data contract — `XMLport`'s casing in particular is
    /// what the virtual table emits. Objects not listed are not tracked by the id-keyed parsers.
    /// </summary>
    private static string? AlObjectKindName(NavCA.SyntaxNode obj) => obj switch
    {
        NavSyntax.TableSyntax => "Table",
        NavSyntax.TableExtensionSyntax => "TableExtension",
        NavSyntax.PageSyntax => "Page",
        NavSyntax.PageExtensionSyntax => "PageExtension",
        NavSyntax.ReportSyntax => "Report",
        NavSyntax.ReportExtensionSyntax => "ReportExtension",
        NavSyntax.CodeunitSyntax => "Codeunit",
        NavSyntax.QuerySyntax => "Query",
        NavSyntax.XmlPortSyntax => "XMLport",
        NavSyntax.EnumTypeSyntax => "Enum",
        NavSyntax.EnumExtensionTypeSyntax => "EnumExtension",
        NavSyntax.PermissionSetSyntax => "PermissionSet",
        NavSyntax.PermissionSetExtensionSyntax => "PermissionSetExtension",
        _ => null,
    };

    /// <summary>
    /// An OBJECT-level <c>Caption</c>, matching what the old brace-depth-scoped
    /// <c>ReadTopLevelProperty</c> returned: the unescaped literal for <c>'…'</c>, the trimmed
    /// text for a bare value, and null when the object declares no Caption at all. Null is
    /// meaningful — "declares none", which the consumer turns into AL's name fallback — and is
    /// not the same as an empty caption.
    /// <para>Differs from <see cref="CaptionFrom"/> (field-level), which answers null for a
    /// non-label value because the field-level regex required quotes.</para>
    /// </summary>
    private static string? PropertyTextFrom(NavSyntax.PropertyValueSyntax? value)
    {
        if (value == null) return null;
        return CaptionFrom(value) ?? value.ToString()?.Trim();
    }

    /// <summary>
    /// The last name segment of a possibly-namespaced object reference:
    /// <c>Microsoft.Sales.History."Sales Invoice Header"</c> → <c>Sales Invoice Header</c>,
    /// <c>Customer</c> → <c>Customer</c>. Quote-aware, so a quoted name that itself contains a
    /// dot (<c>"Doc. No."</c>) survives intact — the old dot-collapse ran over the unquoted
    /// text and would have truncated it to <c>No.</c>.
    /// </summary>
    private static string LastNameSegment(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length >= 2 && s[^1] == '"')
        {
            var open = s.LastIndexOf('"', s.Length - 2);
            if (open >= 0) return s[(open + 1)..^1];
        }
        int dot = s.LastIndexOf('.');
        return dot >= 0 && dot < s.Length - 1 ? s[(dot + 1)..] : Unquote(s);
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

    /// <summary>
    /// A page-valued table property (<c>LookupPageId</c> / <c>DrillDownPageId</c>) as written:
    /// the last name segment of a page reference (<c>Microsoft.Sales."Customer List"</c> →
    /// <c>Customer List</c>), or the digits when the AL declared a bare id. Null when the
    /// property is absent — "declares none", which the Table Metadata provider turns into 0
    /// rather than a guess.
    /// </summary>
    private static string? PageRefText(NavSyntax.PropertyValueSyntax? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var segment = LastNameSegment(text);
        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    private static bool PropIs(NavSyntax.PropertyListSyntax? list, string name, string expected) =>
        string.Equals(PropValue(list, name)?.ToString()?.Trim(), expected,
            StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Builds a <see cref="ParsedField"/> from one <c>field(...)</c> declaration.
    /// <para>Identical for a `table` field and a `tableextension` field: AL declares them the
    /// same way and BC gives them the same metadata. They used to differ — extension fields
    /// were parsed without OptionMembers and without AutoIncrement (#1711), which left an
    /// Option field added by a tableextension with no option string, so NCLOptionMetadata saw
    /// the wrong member count (#1674's defect class), and an AutoIncrement field added by a
    /// tableextension with no autoincrement semantics at all.</para>
    /// </summary>
    private static ParsedField? ParseFieldSyntax(NavSyntax.FieldSyntax f)
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
        if (isFlowField)
            calcFormula = CalcFormulaFrom(PropValue(props, "CalcFormula"));

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

        bool isAutoIncrement = PropIs(props, "AutoIncrement", "true");
        var caption = CaptionFrom(PropValue(props, "Caption"));

        return new ParsedField(fid, fname, ftype, length, isFlowField, calcFormula,
            optionMembers, initValueText, isAutoIncrement, caption);
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
                    if (ParseFieldSyntax(f) is { } pf)
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
            // LookupPageId / DrillDownPageId feed the Table Metadata (2000000136) virtual
            // table. Kept as the written reference and resolved later: a page declared after
            // this table in compile order is not in the page inventory yet.
            var lookupPage = PageRefText(PropValue(table.PropertyList, "LookupPageId"));
            var drillDownPage = PageRefText(PropValue(table.PropertyList, "DrillDownPageId"));
            _parsedTables[tableId] = new ParsedTable(tableId, tableName, fields, pkFieldIds,
                secondaryKeys, isTableTypeTemporary, dataPerCompany, lookupPage, drillDownPage);
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

            // Extension fields are parsed exactly like base-table fields — see
            // ParseFieldSyntax for what they used to lose (#1711).
            var fields = new List<ParsedField>();
            // OfType<FieldSyntax>: a tableextension's field list also holds `modify(...)`
            // entries, which declare no new field. The regex only ever matched
            // `field(N; Name; Type)` either, so this keeps the same set.
            if (ext.Fields != null)
                foreach (var f in ext.Fields.Fields.OfType<NavSyntax.FieldSyntax>())
                    if (ParseFieldSyntax(f) is { } pf)
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

    /// <summary>
    /// Builds a <see cref="ParsedCalcFormula"/> from a CalcFormula property value node.
    /// <para>AL has two shapes and the parser gives them two node types:
    /// <c>sum/average/min/max/lookup</c> carry a qualified <c>Table.Field</c>
    /// (<see cref="NavSyntax.FieldCalculationFormulaSyntax"/>), while <c>count/exist</c> carry a
    /// table alone (<see cref="NavSyntax.TableCalculationFormulaSyntax"/>) and no field.</para>
    /// </summary>
    private static ParsedCalcFormula? CalcFormulaFrom(NavSyntax.PropertyValueSyntax? value)
    {
        string formulaType;
        string sourceTableName;
        string? sourceFieldName;
        NavSyntax.WhereExpressionSyntax? where;
        string signText;

        switch (value)
        {
            case NavSyntax.FieldCalculationFormulaSyntax f:
                formulaType = f.FormulaKeywordToken.ValueText;
                sourceTableName = Unquote(f.Field?.Left?.ToString()?.Trim() ?? "");
                sourceFieldName = f.Field?.Right == null ? null : Unquote(f.Field.Right.ToString().Trim());
                where = f.WhereExpression;
                signText = f.Sign.ValueText ?? "";
                break;
            case NavSyntax.TableCalculationFormulaSyntax t:
                formulaType = t.FormulaKeywordToken.ValueText;
                sourceTableName = Unquote(t.Table?.ToString()?.Trim() ?? "");
                sourceFieldName = null; // count/exist have no field part
                where = t.WhereExpression;
                signText = t.Sign.ValueText ?? "";
                break;
            default:
                return null;
        }

        // #1708 — the sign. `-sum(...)` is a negated formula; AL also accepts the no-op `+`.
        // The sign is now carried on ParsedCalcFormula and honoured by NclMetaTableBuilder
        // (MetaCalcFormula.reverseSign) and FlowFieldPatches (NegateResult), so parsing it is
        // no longer a silent lie about the value. A sign token this code has never seen is
        // still refused rather than guessed at.
        bool negated;
        if (signText.Length == 0 || signText == "+") negated = false;
        else if (signText == "-") negated = true;
        else
        {
            Console.Error.WriteLine($"[CalcFormula] REFUSED {sourceTableName}: unrecognised sign '{signText}'");
            return null;
        }

        if (string.IsNullOrEmpty(sourceTableName)) return null;

        // #1709 — every condition shape, selected BY NODE TYPE. Dropping `const(...)` and
        // `filter(...)` made the FlowField aggregate rows AL had excluded: a plausible wrong
        // number, silently (the Base Application writes 1215 const and 285 filter conditions).
        var filters = new List<ParsedCalcFilter>();
        if (where?.Filter != null)
        {
            foreach (var cond in where.Filter.Conditions)
            {
                switch (cond)
                {
                    // "Document No." = field("Code")
                    case NavSyntax.SimpleFieldExpressionSyntax sfe:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(sfe.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Field,
                            ParentFieldName: Unquote(sfe.Identifier?.ToString()?.Trim() ?? "")));
                        break;

                    // Open = const(true)
                    case NavSyntax.ConstExpressionSyntax ce:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ce.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Const,
                            Value: ConstValueText(ce.Identifier?.ToString())));
                        break;

                    // Status = filter(Open|Released)
                    case NavSyntax.FilterExpressionSyntax fe:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(fe.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.Filter,
                            Value: fe.Filter?.ToString()?.Trim() ?? ""));
                        break;

                    // "Account No." = field(filter(Totaling))          → ValueIsFilter
                    // "Posting Date" = field(upperlimit("Date Filter")) → OnlyMaxLimit
                    //
                    // Carried as their own kind so nothing can read them as a plain `field(X)`
                    // link (which would apply an equality BC never wrote), but NOT applied —
                    // see BuildMetaCalcFormula. Leaving them unapplied is what the parser has
                    // always done; turning that into a refusal of the whole formula changes
                    // the value of the ~105 Base Application FlowFields that use these shapes
                    // and needs its own issue, test and service-tier validation.
                    case NavSyntax.FieldFilterExpressionSyntax ffe:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ffe.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.FlowFilter));
                        break;
                    case NavSyntax.FieldUpperLimitExpressionSyntax ule:
                        filters.Add(new ParsedCalcFilter(
                            Unquote(ule.LeftHandSide?.ToString()?.Trim() ?? ""),
                            ParsedCalcFilterKind.FlowFilter));
                        break;

                    default:
                        // A condition shape this code has never seen. Refuse the WHOLE formula:
                        // aggregating over only the conditions we did understand silently
                        // widens the row set.
                        Console.Error.WriteLine(
                            $"[CalcFormula] REFUSED {sourceTableName}: unsupported where-condition " +
                            $"{cond?.GetType().Name} ({cond})");
                        return null;
                }
            }
        }

        Console.Error.WriteLine($"[CalcFormula] parsed {sourceTableName}.{sourceFieldName ?? "*"} type={formulaType} negated={negated} filters={filters.Count}");
        return new ParsedCalcFormula(formulaType, sourceTableName, sourceFieldName, filters, negated);
    }

    /// <summary>
    /// The literal of a <c>const(...)</c> condition, as text.
    /// <para>Quotes come off for the same reason they do on InitValue (#1674):
    /// <c>NCLMetaFilterConst</c> evaluates this text against the SOURCE field's own type, and
    /// an option member named <c>On Hold</c> is never matched by the 9-character
    /// <c>"On Hold"</c>. AL's doubled-quote escape is resolved with it.</para>
    /// </summary>
    private static string ConstValueText(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1].Replace("\"\"", "\"");
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        return s;
    }

    /// <summary>
    /// Text overload, kept for <c>BcAppSymbolCache</c>, which reconstructs a CalcFormula from
    /// <c>SymbolReference.json</c> and so has text rather than a node. The text is wrapped in a
    /// minimal table and run through the same parser, so both callers share one implementation.
    /// </summary>
    internal static ParsedCalcFormula? TryParseCalcFormula(string fieldBody)
    {
        if (string.IsNullOrWhiteSpace(fieldBody)) return null;
        // The wrapper id is irrelevant — nothing is registered, the tree is read and dropped.
        var wrapped = "table 50000 __CalcFormulaProbe { fields { field(1; __F; Decimal) { "
                    + fieldBody + " } } }";
        foreach (var obj in ParseAlObjects(wrapped))
        {
            if (obj is not NavSyntax.TableSyntax table || table.Fields == null) continue;
            foreach (var f in table.Fields.Fields)
                if (CalcFormulaFrom(PropValue(f.PropertyList, "CalcFormula")) is { } parsed)
                    return parsed;
        }
        return null;
    }
}

// ─── Data holders ────────────────────────────────────────────────────────────

/// <summary>
/// Which shape of <c>where(...)</c> condition a <see cref="ParsedCalcFilter"/> carries. AL
/// writes four, they are NOT interchangeable, and reading one as another is a silent wrong
/// value (#1709).
/// </summary>
internal enum ParsedCalcFilterKind
{
    /// <summary><c>"Document No." = field("No.")</c> — link to a field of the PARENT record.
    /// Becomes a <c>MetaFilter</c> of FilterType FIELD whose filterValue is the parent field
    /// id.</summary>
    Field,
    /// <summary><c>Open = const(true)</c> — compare against a literal. Becomes FilterType
    /// CONST, filterValue = the literal's text, which <c>NCLMetaFilterConst</c> evaluates
    /// against the SOURCE field's own type.</summary>
    Const,
    /// <summary><c>Status = filter(Open|Released)</c> — a filter EXPRESSION. Becomes
    /// FilterType FILTER, filterValue = the expression text, parsed by BC's own filter parser
    /// (<c>NCLMetaFilterExpression</c>).</summary>
    Filter,
    /// <summary><c>"Account No." = field(filter(Totaling))</c> and <c>"Posting Date" =
    /// field(upperlimit("Date Filter"))</c> — the FlowFilter forms
    /// (<c>NCLMetaFilterModes.ValueIsFilter</c> / <c>.OnlyMaxLimit</c>). Parsed so nothing can
    /// mistake them for a plain <see cref="Field"/> link; not applied — see
    /// <c>BuildMetaCalcFormula</c>.</summary>
    FlowFilter,
}

/// <param name="SourceFieldName">Field of the FlowField's SOURCE table being constrained.</param>
/// <param name="Kind">Which of AL's condition shapes this is.</param>
/// <param name="ParentFieldName">Set only for <see cref="ParsedCalcFilterKind.Field"/>.</param>
/// <param name="Value">Const literal / filter expression text — set for
/// <see cref="ParsedCalcFilterKind.Const"/> and <see cref="ParsedCalcFilterKind.Filter"/>.</param>
internal record ParsedCalcFilter(
    string SourceFieldName,
    ParsedCalcFilterKind Kind = ParsedCalcFilterKind.Field,
    string? ParentFieldName = null,
    string? Value = null);

/// <param name="Negated">The formula's leading <c>-</c> (#1708), carried through to
/// <c>MetaCalcFormula.reverseSign</c> → <c>NCLMetaCalculationFormula.NegateResult</c>.</param>
internal record ParsedCalcFormula(string FormulaType, string SourceTableName, string? SourceFieldName, List<ParsedCalcFilter> Filters, bool Negated = false);

internal record ParsedField(int FieldId, string FieldName, string TypeName, int Length, bool IsFlowField = false, ParsedCalcFormula? CalcFormula = null, string? OptionMembers = null, string? InitValueText = null, bool IsAutoIncrement = false, string? Caption = null);
internal record ParsedKey(string Name, List<int> FieldIds);
/// <param name="LookupPageName">The table's declared <c>LookupPageId</c> as WRITTEN — a page
/// name (<c>"Customer List"</c>) or a bare id in text form. Both sources state it by name:
/// AL source writes the reference, and a dependency's SymbolReference.json records
/// <c>LookupPageID</c>/<c>LookupPageId</c> as the page's NAME, never its number (measured
/// against Base Application 28.1). Resolution to an id is therefore deferred to row-build
/// time, where the full page inventory is known. Null means the table declares none, which
/// is not the same as 0 — see <c>RecordPatches.TableMetadataVirtualTable.cs</c>.</param>
/// <param name="DrillDownPageName">Same, for <c>DrillDownPageId</c>.</param>
internal record ParsedTable(int TableId, string TableName,
    List<ParsedField> Fields, List<int> PkFieldIds, List<ParsedKey>? SecondaryKeys = null,
    bool IsTableTypeTemporary = false, bool DataPerCompany = true,
    string? LookupPageName = null, string? DrillDownPageName = null);
