using System.Text.RegularExpressions;

namespace AlRunner.Runtime;

/// <summary>
/// Transpile-time registry of AL table field declarations so runtime code
/// that knows only a field name (e.g.
/// <see cref="MockRecordHandle.EvaluateExistFormula"/> resolving
/// <c>exist(... where(C1 = field(X)))</c>) can look up field IDs without
/// waiting for <see cref="MockRecordHandle.RegisterFieldName"/> to be
/// called from generated code.
/// </summary>
public static class TableFieldRegistry
{
    private static readonly Regex TableHeader = new(
        @"\btable\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // field(id; name; type)
    private static readonly Regex FieldDecl = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // key(Name; Field1, "Quoted Field 2", Field3)  — only the fields we need.
    private static readonly Regex KeyDecl = new(
        @"\bkey\s*\(\s*[^;]+;\s*([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // field(id; name; type) — extended regex capturing the type token after the second semicolon
    private static readonly Regex FieldDeclWithType = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;\s*(?:Enum\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*)))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // tableId -> (fieldName -> fieldId)
    private static readonly Dictionary<int, Dictionary<string, int>> _byTable = new();

    // (tableId, fieldNo) -> enum name (for fields declared as Enum "XYZ")
    private static readonly Dictionary<(int TableId, int FieldNo), string> _enumFields = new();

    public static void Clear()
    {
        _byTable.Clear();
        _enumFields.Clear();
    }

    public static void ParseAndRegister(string alSource)
    {
        foreach (Match tm in TableHeader.Matches(alSource))
        {
            if (!int.TryParse(tm.Groups[1].Value, out var tableId)) continue;

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

            if (!_byTable.TryGetValue(tableId, out var fields))
                _byTable[tableId] = fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (Match fm in FieldDecl.Matches(body))
            {
                if (!int.TryParse(fm.Groups[1].Value, out var fieldId)) continue;
                var name = fm.Groups[2].Success ? fm.Groups[2].Value : fm.Groups[3].Value;
                fields[name] = fieldId;
            }

            // Extract enum field type info: field(id; name; Enum "EnumName")
            foreach (Match em in FieldDeclWithType.Matches(body))
            {
                if (!int.TryParse(em.Groups[1].Value, out var fieldId)) continue;
                var enumName = em.Groups[4].Success ? em.Groups[4].Value : em.Groups[5].Value;
                _enumFields[(tableId, fieldId)] = enumName;
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

    /// <summary>
    /// Returns the AL enum name for a field declared as <c>Enum "X"</c>, or null
    /// if the field is not an enum type.
    /// </summary>
    public static string? GetEnumName(int tableId, int fieldNo)
    {
        return _enumFields.TryGetValue((tableId, fieldNo), out var name) ? name : null;
    }
}
