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

    // field(id; name; type) — captures field ID, name (quoted or bare), and type
    private static readonly Regex FieldDecl = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;\s*([^)]+?)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // key(Name; Field1, "Quoted Field 2", Field3)  — only the fields we need.
    private static readonly Regex KeyDecl = new(
        @"\bkey\s*\(\s*[^;]+;\s*([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Caption = 'value';  inside a field or table body
    private static readonly Regex CaptionProp = new(
        @"\bCaption\s*=\s*'([^']*)'\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // tableId -> (fieldName -> fieldId)
    private static readonly Dictionary<int, Dictionary<string, int>> _byTable = new();

    // tableId -> tableName
    private static readonly Dictionary<int, string> _tableNames = new();
    // tableId -> tableCaption
    private static readonly Dictionary<int, string> _tableCaptions = new();
    // tableId -> (fieldId -> FieldMeta)
    private static readonly Dictionary<int, Dictionary<int, FieldMeta>> _fieldMeta = new();

    public static void Clear()
    {
        _byTable.Clear();
        _tableNames.Clear();
        _tableCaptions.Clear();
        _fieldMeta.Clear();
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
                _tableCaptions[tableId] = tableCapMatch.Groups[1].Value;

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
                            fieldCaption = capMatch.Groups[1].Value;
                    }
                }

                meta[fieldId] = new FieldMeta(fieldId, name, fieldCaption, typeName, length);
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
}
