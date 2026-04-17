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

        // Parse tableextension declarations: register extension fields under the base table ID.
        // tableextension N "Name" extends "BaseTableName" { fields { field(...) ... } }
        // The base table must have been registered (from a prior table declaration in this or a
        // previously-processed source file) so we can resolve the name → ID mapping.
        // Build a reverse map from table name → table ID for the lookup.
        var nameToId = new Dictionary<string, int>(_tableNames.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _tableNames) nameToId[kv.Value] = kv.Key;

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

    /// <summary>Returns the table name or null if not registered.</summary>
    public static string? GetTableName(int tableId)
        => _tableNames.TryGetValue(tableId, out var name) ? name : null;

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
