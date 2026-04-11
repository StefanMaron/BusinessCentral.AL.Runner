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

    // tableId -> (fieldName -> fieldId)
    private static readonly Dictionary<int, Dictionary<string, int>> _byTable = new();

    public static void Clear() => _byTable.Clear();

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
        }
    }

    public static int? GetFieldId(int tableId, string fieldName)
    {
        if (_byTable.TryGetValue(tableId, out var fields) &&
            fields.TryGetValue(fieldName, out var id))
            return id;
        return null;
    }
}
