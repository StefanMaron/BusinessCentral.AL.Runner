using System.Text.RegularExpressions;

namespace AlRunner.Runtime;

/// <summary>
/// Metadata for a single AL table field (name, caption, type, length).
/// </summary>
public record FieldMeta(int FieldId, string Name, string? Caption, string TypeName, int? Length);

/// <summary>
/// Transpile-time registry of AL table field declarations so runtime code
/// that knows only a field name (e.g.
/// <see cref="MockRecordHandle.EvaluateExistFormula"/> resolving
/// <c>exist(... where(C1 = field(X)))</c>) can look up field IDs without
/// waiting for <see cref="MockRecordHandle.RegisterFieldName"/> to be
/// called from generated code.
///
/// Also stores field metadata (name, caption, type, length) and table-level
/// metadata (name, caption) so that runtime properties like
/// <c>ALFieldCaption</c>, <c>ALTableName</c>, <c>FieldRef.Name</c>, etc.
/// return real values instead of stub defaults.
/// </summary>
public static class TableFieldRegistry
{
    private static readonly Regex TableHeader = new(
        @"\btable\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // page Id "Name" { ... SourceTable = "TableName"; ... }
    private static readonly Regex PageHeader = new(
        @"\bpage\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // SourceTable = "TableName"  or  SourceTable = TableName
    private static readonly Regex SourceTableProp = new(
        @"\bSourceTable\s*=\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // tableextension Id "Name" extends "BaseTableName" { fields { ... } }
    private static readonly Regex TableExtHeader = new(
        @"\btableextension\s+\d+\s+(?:""[^""]*""|[A-Za-z_][A-Za-z0-9_]*)\s+extends\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // field(id; name; type) — captures field ID, name (quoted or bare), and type
    private static readonly Regex FieldDecl = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;\s*([^)]+?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // key(Name; Field1, "Quoted Field 2", Field3)  — only the fields we need.
    private static readonly Regex KeyDecl = new(
        @"\bkey\s*\(\s*[^;]+;\s*([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // AL escapes embedded apostrophes by doubling them, e.g. 'Vendor''s Name'.
    private static readonly Regex CaptionProp = new(
        @"\bCaption\s*=\s*'((?:''|[^'])*)'\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // field(id; name; type) — extended regex capturing Enum fields specifically
    private static readonly Regex FieldDeclWithType = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;\s*(?:Enum\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*)))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // tableId -> (fieldName -> fieldId)
    private static readonly Dictionary<int, Dictionary<string, int>> _byTable = new();

    // tableId -> tableName
    private static readonly Dictionary<int, string> _tableNames = new();
    // tableId -> tableCaption
    private static readonly Dictionary<int, string> _tableCaptions = new();
    // tableId -> (fieldId -> FieldMeta)
    private static readonly Dictionary<int, Dictionary<int, FieldMeta>> _fieldMeta = new();

    // (tableId, fieldNo) -> enum name (for fields declared as Enum "XYZ")
    private static readonly Dictionary<(int TableId, int FieldNo), string> _enumFields = new();

    // (tableId, fieldNo) -> comma-separated option members (for inline Option fields with OptionMembers = A,B,C)
    private static readonly Dictionary<(int TableId, int FieldNo), string> _optionMembersFields = new();

    // pageId -> source tableId (parsed from page SourceTable declarations)
    private static readonly Dictionary<int, int> _pageSourceTable = new();

    private static readonly Regex OptionMembersProp = new(
        @"\bOptionMembers\s*=\s*([^;]+);",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Directly captures field ID and OptionMembers from Option field declarations.
    // Matches: field(id; <name>; Option) { [^{}]* OptionMembers = <value>; }
    private static readonly Regex OptionFieldMembersRx = new(
        @"\bfield\s*\(\s*(\d+)\s*;[^;]+;\s*Option\s*\)\s*\{[^{}]*\bOptionMembers\s*=\s*([^;]+);",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Clear()
    {
        _byTable.Clear();
        _tableNames.Clear();
        _tableCaptions.Clear();
        _fieldMeta.Clear();
        _enumFields.Clear();
        _optionMembersFields.Clear();
        _pageSourceTable.Clear();
    }

    public static void ParseAndRegister(string alSource)
    {
        foreach (Match tm in TableHeader.Matches(alSource))
        {
            if (!int.TryParse(tm.Groups[1].Value, out var tableId)) continue;
            var tableName = tm.Groups[2].Success ? tm.Groups[2].Value : tm.Groups[3].Value;

            // Store table name
            _tableNames[tableId] = tableName;

            // Extract the table body via brace counting.
            int start = tm.Index + tm.Length;
            int depth = 1;
            int i = start;
            while (i < alSource.Length && depth > 0)
            {
                if (alSource[i] == '{') depth++;
                else if (alSource[i] == '}') depth--;
                i++;
            }
            if (depth != 0) continue;
            var body = alSource.Substring(start, i - start - 1);

            // Parse table-level Caption before the fields block
            var fieldsIdx = body.IndexOf("fields", StringComparison.OrdinalIgnoreCase);
            var tablePreamble = fieldsIdx >= 0 ? body.Substring(0, fieldsIdx) : body;
            var tableCapMatch = CaptionProp.Match(tablePreamble);
            if (tableCapMatch.Success)
                _tableCaptions[tableId] = DecodeAlSingleQuotedString(tableCapMatch.Groups[1].Value);

            if (!_byTable.TryGetValue(tableId, out var fields))
                _byTable[tableId] = fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (!_fieldMeta.TryGetValue(tableId, out var meta))
                _fieldMeta[tableId] = meta = new Dictionary<int, FieldMeta>();

            foreach (Match fm in FieldDecl.Matches(body))
            {
                if (!int.TryParse(fm.Groups[1].Value, out var fieldId)) continue;
                var name = fm.Groups[2].Success ? fm.Groups[2].Value : fm.Groups[3].Value;
                var fieldTypeRaw = fm.Groups[4].Value.Trim();
                fields[name] = fieldId;

                // Parse type and optional length (e.g. Text[100], Code[20])
                string typeName = fieldTypeRaw;
                int? length = null;
                var bracketIdx = fieldTypeRaw.IndexOf('[');
                if (bracketIdx >= 0)
                {
                    typeName = fieldTypeRaw.Substring(0, bracketIdx).Trim();
                    var closeBracket = fieldTypeRaw.IndexOf(']', bracketIdx);
                    if (closeBracket > bracketIdx + 1)
                    {
                        var lenStr = fieldTypeRaw.Substring(bracketIdx + 1, closeBracket - bracketIdx - 1);
                        if (int.TryParse(lenStr, out var len))
                            length = len;
                    }
                }

                // Parse field-level Caption from the field body block
                string? fieldCaption = null;
                int fieldBodyStart = fm.Index + fm.Length;
                // Look for `{ ... }` block following the field declaration
                int searchPos = fieldBodyStart;
                while (searchPos < body.Length && char.IsWhiteSpace(body[searchPos]))
                    searchPos++;
                if (searchPos < body.Length && body[searchPos] == '{')
                {
                    int fDepth = 1;
                    int fStart = searchPos + 1;
                    int fEnd = fStart;
                    while (fEnd < body.Length && fDepth > 0)
                    {
                        if (body[fEnd] == '{') fDepth++;
                        else if (body[fEnd] == '}') fDepth--;
                        fEnd++;
                    }
                    if (fDepth == 0)
                    {
                        var fieldBody = body.Substring(fStart, fEnd - fStart - 1);
                        var capMatch = CaptionProp.Match(fieldBody);
                        if (capMatch.Success)
                            fieldCaption = DecodeAlSingleQuotedString(capMatch.Groups[1].Value);
                    }
                }

                meta[fieldId] = new FieldMeta(fieldId, name, fieldCaption, typeName, length);
            }

            // Extract enum field type info: field(id; name; Enum "EnumName")
            foreach (Match em in FieldDeclWithType.Matches(body))
            {
                if (!int.TryParse(em.Groups[1].Value, out var fieldId)) continue;
                var enumName = em.Groups[4].Success ? em.Groups[4].Value : em.Groups[5].Value;
                _enumFields[(tableId, fieldId)] = enumName;
            }

            // Extract OptionMembers for inline Option fields via a dedicated pass over the body.
            // This is more reliable than using field body extraction and avoids edge cases
            // with field body position tracking.
            foreach (Match om in OptionFieldMembersRx.Matches(body))
            {
                if (!int.TryParse(om.Groups[1].Value, out var fieldId)) continue;
                _optionMembersFields[(tableId, fieldId)] = om.Groups[2].Value.Trim();
            }

            // Extract the first declared key (typically Clustered PK) and
            // register its field numbers so MockRecordHandle.ALInsert can
            // enforce uniqueness. BC's registration path only fires for
            // tables whose generated code references ALFieldNo / a runtime
            // PK helper; synthetic test fixtures often skip that path.
            var firstKey = KeyDecl.Match(body);
            if (firstKey.Success)
            {
                var keyList = firstKey.Groups[1].Value;
                var pkFieldIds = new List<int>();
                foreach (var rawPart in keyList.Split(','))
                {
                    var part = rawPart.Trim().Trim('"').Trim();
                    if (part.Length == 0) continue;
                    if (fields.TryGetValue(part, out var fid))
                        pkFieldIds.Add(fid);
                }
                if (pkFieldIds.Count > 0)
                    MockRecordHandle.RegisterPrimaryKey(tableId, pkFieldIds.ToArray());
            }
        }

        // Parse tableextension and page declarations: both need a name→ID reverse map.
        // Build it once here so both sections can share it.
        // The base table must have been registered (from a prior table declaration in this or a
        // previously-processed source file) so we can resolve the name → ID mapping.
        var nameToId = new Dictionary<string, int>(_tableNames.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _tableNames) nameToId[kv.Value] = kv.Key;

        // Parse page declarations to record page → source table mappings.
        // This lets MockTestPageHandle.EnsureFieldMapping() discover which table a page
        // is backed by without relying on the rewritten Rec property (which loses the table ID).
        foreach (Match pm in PageHeader.Matches(alSource))
        {
            if (!int.TryParse(pm.Groups[1].Value, out var pageId)) continue;

            int pStart = pm.Index + pm.Length;
            int pDepth = 1, pI = pStart;
            while (pI < alSource.Length && pDepth > 0)
            {
                if (alSource[pI] == '{') pDepth++;
                else if (alSource[pI] == '}') pDepth--;
                pI++;
            }
            if (pDepth != 0) continue;
            var pageBody = alSource.Substring(pStart, pI - pStart - 1);

            var stMatch = SourceTableProp.Match(pageBody);
            if (!stMatch.Success) continue;

            var tableName = stMatch.Groups[1].Success ? stMatch.Groups[1].Value : stMatch.Groups[2].Value;
            if (nameToId.TryGetValue(tableName, out var sourceTableId))
                _pageSourceTable[pageId] = sourceTableId;
        }

        // Parse tableextension declarations: register extension fields under the base table ID.

        foreach (Match em in TableExtHeader.Matches(alSource))
        {
            var baseName = em.Groups[1].Success ? em.Groups[1].Value : em.Groups[2].Value;
            if (!nameToId.TryGetValue(baseName, out var baseTableId)) continue;

            int extStart = em.Index + em.Length;
            int extDepth = 1, extI = extStart;
            while (extI < alSource.Length && extDepth > 0)
            {
                if (alSource[extI] == '{') extDepth++;
                else if (alSource[extI] == '}') extDepth--;
                extI++;
            }
            if (extDepth != 0) continue;
            var extBody = alSource.Substring(extStart, extI - extStart - 1);

            if (!_byTable.TryGetValue(baseTableId, out var extFields))
                _byTable[baseTableId] = extFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!_fieldMeta.TryGetValue(baseTableId, out var extMeta))
                _fieldMeta[baseTableId] = extMeta = new Dictionary<int, FieldMeta>();

            foreach (Match fm in FieldDecl.Matches(extBody))
            {
                if (!int.TryParse(fm.Groups[1].Value, out var fieldId)) continue;
                var name = fm.Groups[2].Success ? fm.Groups[2].Value : fm.Groups[3].Value;
                var fieldTypeRaw = fm.Groups[4].Value.Trim();
                extFields[name] = fieldId;

                string typeName = fieldTypeRaw;
                int? length = null;
                var bracketIdx = fieldTypeRaw.IndexOf('[');
                if (bracketIdx >= 0)
                {
                    typeName = fieldTypeRaw.Substring(0, bracketIdx).Trim();
                    var closeBracket = fieldTypeRaw.IndexOf(']', bracketIdx);
                    if (closeBracket > bracketIdx + 1 &&
                        int.TryParse(fieldTypeRaw.Substring(bracketIdx + 1, closeBracket - bracketIdx - 1), out var len))
                        length = len;
                }

                string? fieldCaption = null;
                int searchPos = fm.Index + fm.Length;
                while (searchPos < extBody.Length && char.IsWhiteSpace(extBody[searchPos]))
                    searchPos++;
                if (searchPos < extBody.Length && extBody[searchPos] == '{')
                {
                    int fDepth = 1, fStart = searchPos + 1, fEnd = fStart;
                    while (fEnd < extBody.Length && fDepth > 0)
                    {
                        if (extBody[fEnd] == '{') fDepth++;
                        else if (extBody[fEnd] == '}') fDepth--;
                        fEnd++;
                    }
                    if (fDepth == 0)
                    {
                        var capMatch = CaptionProp.Match(extBody.Substring(fStart, fEnd - fStart - 1));
                        if (capMatch.Success)
                            fieldCaption = DecodeAlSingleQuotedString(capMatch.Groups[1].Value);
                    }
                }

                extMeta[fieldId] = new FieldMeta(fieldId, name, fieldCaption, typeName, length);
            }

            // Enum fields in extensions
            foreach (Match efm in FieldDeclWithType.Matches(extBody))
            {
                if (!int.TryParse(efm.Groups[1].Value, out var fieldId)) continue;
                var enumName = efm.Groups[4].Success ? efm.Groups[4].Value : efm.Groups[5].Value;
                _enumFields[(baseTableId, fieldId)] = enumName;
            }

            // Option fields in extensions
            foreach (Match om in OptionFieldMembersRx.Matches(extBody))
            {
                if (!int.TryParse(om.Groups[1].Value, out var fieldId)) continue;
                _optionMembersFields[(baseTableId, fieldId)] = om.Groups[2].Value.Trim();
            }
        }
    }

    public static int? GetFieldId(int tableId, string fieldName)
    {
        if (_byTable.TryGetValue(tableId, out var fields) &&
            fields.TryGetValue(fieldName, out var id))
            return id;
        return null;
    }

    /// <summary>
    /// Returns all (fieldId, fieldName) pairs registered for the given table.
    /// Used by <see cref="AlRunner.Runtime.MockTestPageHandle"/> to build
    /// hash→fieldId mappings for GoToKey/GoToRecord field population.
    /// </summary>
    public static IEnumerable<(int FieldId, string FieldName)> GetAllFields(int tableId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta))
            foreach (var kv in meta)
                if (kv.Value.Name != null)
                    yield return (kv.Key, kv.Value.Name);
    }

    /// <summary>
    /// Returns the source table ID for the given page, as declared by
    /// <c>SourceTable = "TableName"</c> in the page definition, or <c>null</c> if
    /// not registered.  Populated by <see cref="ParseAndRegister"/> when it
    /// processes page declarations.
    /// </summary>
    public static int? GetSourceTableId(int pageId)
        => _pageSourceTable.TryGetValue(pageId, out var id) ? id : null;

    /// <summary>Returns the table name or null if not registered.</summary>
    public static string? GetTableName(int tableId)
        => _tableNames.TryGetValue(tableId, out var name) ? name : null;

    /// <summary>
    /// Looks up a table ID by a normalised name (spaces and non-alphanumeric
    /// characters stripped, comparison is case-insensitive).  Used by
    /// <see cref="MockReportHandle"/> to map a data-item field name (e.g.
    /// <c>RDTRecord</c>) back to the original table ID (e.g. <c>84500</c>
    /// for table "RDT Record").
    /// </summary>
    public static int? GetTableIdByNormalizedName(string normalizedName)
    {
        foreach (var kv in _tableNames)
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(kv.Value, @"[^A-Za-z0-9]", "");
            if (string.Equals(stripped, normalizedName, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    /// <summary>Returns the table caption or null if not registered.</summary>
    public static string? GetTableCaption(int tableId)
        => _tableCaptions.TryGetValue(tableId, out var caption) ? caption : null;

    /// <summary>Returns the field name or null if not registered.</summary>
    public static string? GetFieldName(int tableId, int fieldId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta) &&
            meta.TryGetValue(fieldId, out var fm))
            return fm.Name;
        return null;
    }

    /// <summary>
    /// Returns the field caption. Falls back to field name if no explicit
    /// Caption property was declared, or null if the field is unknown.
    /// </summary>
    public static string? GetFieldCaption(int tableId, int fieldId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta) &&
            meta.TryGetValue(fieldId, out var fm))
            return fm.Caption ?? fm.Name;
        return null;
    }

    /// <summary>Returns the field type name or null if not registered.</summary>
    public static string? GetFieldTypeName(int tableId, int fieldId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta) &&
            meta.TryGetValue(fieldId, out var fm))
            return fm.TypeName;
        return null;
    }

    /// <summary>Returns the field length (for Text[N]/Code[N]) or null.</summary>
    public static int? GetFieldLength(int tableId, int fieldId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta) &&
            meta.TryGetValue(fieldId, out var fm))
            return fm.Length;
        return null;
    }

    /// <summary>Returns the number of fields declared in the table schema.</summary>
    public static int GetFieldCount(int tableId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta))
            return meta.Count;
        return 0;
    }

    /// <summary>
    /// Returns all declared field IDs for a table in ascending numeric order.
    /// Used by <c>RecordRef.FieldIndex(n)</c> to enumerate fields by ordinal position.
    /// Returns empty list if the table is not in the registry.
    /// </summary>
    public static IReadOnlyList<int> GetFieldIds(int tableId)
    {
        if (_fieldMeta.TryGetValue(tableId, out var meta))
            return meta.Keys.OrderBy(k => k).ToList();
        return Array.Empty<int>();
    }

    /// <summary>
    /// Returns the AL enum name for a field declared as <c>Enum "X"</c>, or null
    /// if the field is not an enum type.
    /// </summary>
    public static string? GetEnumName(int tableId, int fieldNo)
    {
        return _enumFields.TryGetValue((tableId, fieldNo), out var name) ? name : null;
    }

    /// <summary>Returns the comma-separated OptionMembers string for an inline Option field, or null.</summary>
    public static string? GetOptionMembers(int tableId, int fieldNo)
    {
        return _optionMembersFields.TryGetValue((tableId, fieldNo), out var members) ? members : null;
    }

    private static string DecodeAlSingleQuotedString(string value)
    {
        return value.Replace("''", "'");
    }
}
