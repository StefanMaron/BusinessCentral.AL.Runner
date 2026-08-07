// RecordPatches.AlSourceParser — parses AL `table` declarations into ParsedTable
// records keyed by table ID. The output is consumed by NclMetaTableBuilder to
// produce real NCLMetaTable instances at runtime.
//
// The parser uses regex over raw .al text rather than a real AL syntax tree —
// good enough for the spike since we only need table layout (IDs, fields, PK).
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly Regex RxTable = new(
        @"\btable\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxTableExtension = new(
        @"\btableextension\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s+extends\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RxField = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_]\w*))\s*;\s*([^)]+?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Group 1 = key name, group 2 = comma-separated field list.
    private static readonly Regex RxKey = new(
        @"\bkey\s*\(\s*([^;]+)\s*;\s*([^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxFieldClass = new(
        @"\bFieldClass\s*=\s*FlowField\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxTableTypeTemporary = new(
        @"\bTableType\s*=\s*Temporary\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // DataPerCompany = false; — AL's default is TRUE, so only the explicit opt-out is
    // parsed. MetaTable's own ctor default for isDataPerCompany is false, which is the
    // opposite of AL's, and BC's RecordImplementation.ChangeCompany returns true
    // immediately for a table that is not per-company.
    private static readonly Regex RxDataPerCompanyFalse = new(
        @"\bDataPerCompany\s*=\s*false\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // OptionMembers = A,B,C; — captures the comma-joined list (whitespace trimmed
    // per-token by the consumer). Used to populate MetaField.optionString so BC's
    // NCLOptionMetadataNavTypeField (Field.Type field 5 of table 2000000041 and
    // similar specialised subclasses) gets the right count.
    private static readonly Regex RxOptionMembers = new(
        @"\bOptionMembers\s*=\s*([^;]+);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // InitValue = <al-expression>; — captures the raw AL expression text up to the
    // terminating semicolon. Used to populate MetaField.initValue (string) which BC
    // stores as NCLMetaField.initialValueText and evaluates via
    // ALSystemVariable.EvaluateIntoNavValue inside the NCLMetaField.InitValue
    // getter at Init() time.
    // Caption = 'text'; — the field's declared caption. Without it BC's
    // NCLMetaField.CreateCaptionStrings falls back to the field NAME, so
    // `Rec.FieldCaption(n)` and the Field virtual table's "Field Caption" both answer
    // `NewColumnName` where AL declared `New Column Name`. AL escapes an embedded quote
    // by doubling it, so the literal runs to the last quote before the semicolon.
    private static readonly Regex RxCaption = new(
        @"\bCaption\s*=\s*'((?:[^']|'')*)'\s*(?:,[^;]*)?;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The <c>Caption = '...'</c> declared on a field body, or null when it declares none —
    /// in which case BC's own field-name fallback is the correct answer and must stand.
    /// Doubled single quotes are AL's escape for a literal quote.
    /// </summary>
    private static string? ParseCaption(string? fieldBody)
    {
        if (string.IsNullOrEmpty(fieldBody)) return null;
        var m = RxCaption.Match(fieldBody);
        return m.Success ? m.Groups[1].Value.Replace("''", "'") : null;
    }

    private static readonly Regex RxInitValue = new(
        @"\bInitValue\s*=\s*([^;]+);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // AutoIncrement = true; — detect on PK field bodies so we can wire up autoincrement
    // semantics in the NCLMetaTable.
    private static readonly Regex RxAutoIncrement = new(
        @"\bAutoIncrement\s*=\s*true\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    private static void TryParseTableFile(string text)
    {
        // Multiple `table N "Name" { ... }` declarations may live in one .al file.
        // Slice the text between consecutive RxTable matches so each table only sees
        // its own fields/keys.
        var tableMatches = RxTable.Matches(text);
        if (tableMatches.Count == 0) return;

        // Collect all tableextension start positions so we can use them as slice boundaries.
        var extPositions = RxTableExtension.Matches(text).Cast<Match>().Select(m => m.Index).ToArray();

        for (int i = 0; i < tableMatches.Count; i++)
        {
            var tableMatch = tableMatches[i];
            int sliceStart = tableMatch.Index;
            int nextTableIdx = (i + 1 < tableMatches.Count) ? tableMatches[i + 1].Index : text.Length;
            // Also stop at any tableextension that follows this table block.
            int nextExtIdx = extPositions.Where(p => p > sliceStart).Append(text.Length).Min();
            int sliceEnd = Math.Min(nextTableIdx, nextExtIdx);
            var slice = text.Substring(sliceStart, sliceEnd - sliceStart);

            if (!int.TryParse(tableMatch.Groups[1].Value, out int tableId)) continue;
            var tableName = tableMatch.Groups[2].Success ? tableMatch.Groups[2].Value : tableMatch.Groups[3].Value;

            var fields = new List<ParsedField>();
            foreach (Match fm in RxField.Matches(slice))
            {
                if (!int.TryParse(fm.Groups[1].Value, out int fid)) continue;
                var fname = fm.Groups[2].Success ? fm.Groups[2].Value : fm.Groups[3].Value;
                var ftype = fm.Groups[4].Value.Trim();
                int length = 0;
                var lm = Regex.Match(ftype, @"\[(\d+)\]");
                if (lm.Success) int.TryParse(lm.Groups[1].Value, out length);

                // Extract the field body block (e.g. { FieldClass = FlowField; CalcFormula = ...; })
                var fieldBody = ExtractFieldBody(slice, fm.Index + fm.Length);
                bool isFlowField = fieldBody != null && RxFieldClass.IsMatch(fieldBody);
                ParsedCalcFormula? calcFormula = null;
                if (isFlowField && fieldBody != null)
                    calcFormula = TryParseCalcFormula(fieldBody);

                // Option-type fields: capture OptionMembers if present. The comma-
                // separated list is what BC's NCLOptionMetadata constructor expects.
                string? optionMembers = null;
                if (fieldBody != null && ftype.Trim().Equals("Option", StringComparison.OrdinalIgnoreCase))
                {
                    var omMatch = RxOptionMembers.Match(fieldBody);
                    if (omMatch.Success)
                    {
                        // Trim each comma-separated token; keep empty entries (BC allows blanks).
                        optionMembers = string.Join(",",
                            omMatch.Groups[1].Value.Split(',').Select(s => s.Trim()));
                    }
                }

                // InitValue: capture raw AL expression text for fields with explicit
                // InitValue = X; — passed verbatim to MetaField.initValue (string), then
                // BC's NCLMetaField.InitValue getter calls ALSystemVariable.EvaluateIntoNavValue
                // on it at Init() time.
                string? initValueText = null;
                bool isAutoIncrement = false;
                if (fieldBody != null)
                {
                    var ivMatch = RxInitValue.Match(fieldBody);
                    if (ivMatch.Success)
                        initValueText = ivMatch.Groups[1].Value.Trim();
                    isAutoIncrement = RxAutoIncrement.IsMatch(fieldBody);
                }

                var caption = ParseCaption(fieldBody);

                fields.Add(new ParsedField(fid, fname, ftype, length, isFlowField, calcFormula, optionMembers, initValueText, isAutoIncrement, caption));
            }

            // Parse first key as PK; all subsequent keys are secondary.
            var pkFieldIds = new List<int>();
            var secondaryKeys = new List<ParsedKey>();
            var allKeyMatches = RxKey.Matches(slice);
            bool firstKey = true;
            foreach (Match keyMatch in allKeyMatches)
            {
                var keyName = keyMatch.Groups[1].Value.Trim().Trim('"');
                var keyFieldNames = keyMatch.Groups[2].Value
                    .Split(',')
                    .Select(s => s.Trim().Trim('"'))
                    .ToList();
                var keyFieldIds = new List<int>();
                foreach (var kn in keyFieldNames)
                {
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
            // Fallback: first field is PK
            if (pkFieldIds.Count == 0 && fields.Count > 0)
                pkFieldIds.Add(fields[0].FieldId);

            var isTableTypeTemporary = RxTableTypeTemporary.IsMatch(slice);
            var dataPerCompany = !RxDataPerCompanyFalse.IsMatch(slice);
            _parsedTables[tableId] = new ParsedTable(tableId, tableName, fields, pkFieldIds, secondaryKeys,
                isTableTypeTemporary, dataPerCompany);
        }
    }

    private static void TryParseTableExtensionFile(string text)
    {
        var extMatches = RxTableExtension.Matches(text);
        if (extMatches.Count == 0) return;

        for (int i = 0; i < extMatches.Count; i++)
        {
            var m = extMatches[i];
            if (!int.TryParse(m.Groups[1].Value, out int extId)) continue;
            var extName = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            var baseName = m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value;

            int sliceStart = m.Index;
            int sliceEnd = (i + 1 < extMatches.Count) ? extMatches[i + 1].Index : text.Length;
            var slice = text.Substring(sliceStart, sliceEnd - sliceStart);

            var fields = new List<ParsedField>();
            foreach (Match fm in RxField.Matches(slice))
            {
                if (!int.TryParse(fm.Groups[1].Value, out int fid)) continue;
                var fname = fm.Groups[2].Success ? fm.Groups[2].Value : fm.Groups[3].Value;
                var ftype = fm.Groups[4].Value.Trim();
                int length = 0;
                var lm = Regex.Match(ftype, @"\[(\d+)\]");
                if (lm.Success) int.TryParse(lm.Groups[1].Value, out length);

                var fieldBody = ExtractFieldBody(slice, fm.Index + fm.Length);
                bool isFlowField = fieldBody != null && RxFieldClass.IsMatch(fieldBody);
                ParsedCalcFormula? calcFormula = null;
                if (isFlowField && fieldBody != null)
                    calcFormula = TryParseCalcFormula(fieldBody);

                string? initValueText = null;
                if (fieldBody != null)
                {
                    var ivMatch = RxInitValue.Match(fieldBody);
                    if (ivMatch.Success)
                        initValueText = ivMatch.Groups[1].Value.Trim();
                }

                fields.Add(new ParsedField(fid, fname, ftype, length, isFlowField, calcFormula, OptionMembers: null, InitValueText: initValueText, Caption: ParseCaption(fieldBody)));
            }

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

    /// <summary>Extracts the brace-balanced body of a field block starting near <paramref name="pos"/> in <paramref name="slice"/>.</summary>
    private static string? ExtractFieldBody(string slice, int pos)
    {
        while (pos < slice.Length && char.IsWhiteSpace(slice[pos])) pos++;
        if (pos >= slice.Length || slice[pos] != '{') return null;
        int depth = 0, start = pos;
        while (pos < slice.Length)
        {
            if (slice[pos] == '{') depth++;
            else if (slice[pos] == '}') { depth--; if (depth == 0) return slice.Substring(start + 1, pos - start - 1); }
            pos++;
        }
        return null;
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
