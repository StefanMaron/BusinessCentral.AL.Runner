using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Runtime;

/// <summary>
/// Transpile-time registry of FlowField <c>CalcFormula</c> declarations
/// parsed from AL table sources. Consulted by
/// <see cref="MockRecordHandle.ALCalcFields"/> so tests that exercise
/// <c>exist()</c>-style FlowFields can see real in-memory results.
///
/// Minimal surface: supports <c>exist</c> only, with <c>where(...)</c>
/// conditions keyed on <c>field(SelfField)</c> references or
/// <c>const(Literal)</c> values. Enough to cover the common
/// "Has Child" / "Is Empty" idioms seen in BC tests.
/// </summary>
public static class CalcFormulaRegistry
{
    private static readonly Regex TableHeader = new(
        @"\btable\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // field(id; name; type) { ... CalcFormula = ... ; FieldClass = FlowField; ... }
    private static readonly Regex FieldBlock = new(
        @"\bfield\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*;\s*([^)]+?)\)\s*\{([^}]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // CalcFormula = exist("Target" where(...))
    private static readonly Regex ExistFormula = new(
        @"\bCalcFormula\s*=\s*exist\s*\(\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*(?:where\s*\((.*?)\))?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Single condition: ChildField = field(SelfField)  | ChildField = const(Value)
    private static readonly Regex WhereCondition = new(
        @"(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*=\s*(field|const|filter)\s*\(\s*(?:""([^""]+)""|([^)]+))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public enum FormulaKind { Exist, Count, Sum, Lookup, Min, Max, Average }

    public record WhereClause(string ChildField, string OpKind, string Value);

    public record Formula(
        int OwnerTableId,
        int OwnerFieldId,
        FormulaKind Kind,
        string TargetTableName,
        string? TargetAggregateField,  // for sum/min/max/lookup
        List<WhereClause> Conditions);

    // tableId -> list of FlowField formulas registered on it.
    private static readonly Dictionary<int, List<Formula>> _byTable = new();
    // AL table name -> table id (to resolve TargetTableName at runtime).
    private static readonly Dictionary<string, int> _tableIdByName =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Clear()
    {
        _byTable.Clear();
        _tableIdByName.Clear();
    }

    public static void ParseAndRegister(string alSource)
    {
        foreach (Match tm in TableHeader.Matches(alSource))
        {
            if (!int.TryParse(tm.Groups[1].Value, out var tableId)) continue;
            var tableName = tm.Groups[2].Success ? tm.Groups[2].Value : tm.Groups[3].Value;
            _tableIdByName[tableName] = tableId;

            // Extract the table body by brace-counting so nested blocks don't trip us up.
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

            foreach (Match fm in FieldBlock.Matches(body))
            {
                if (!int.TryParse(fm.Groups[1].Value, out var fieldId)) continue;
                var fieldBody = fm.Groups[5].Value;
                var ef = ExistFormula.Match(fieldBody);
                if (!ef.Success) continue;
                var targetTable = ef.Groups[1].Success ? ef.Groups[1].Value : ef.Groups[2].Value;
                var whereText = ef.Groups[3].Success ? ef.Groups[3].Value : "";
                var clauses = new List<WhereClause>();
                foreach (Match wm in WhereCondition.Matches(whereText))
                {
                    var childField = wm.Groups[1].Success ? wm.Groups[1].Value : wm.Groups[2].Value;
                    var opKind = wm.Groups[3].Value.ToLowerInvariant();
                    var val = wm.Groups[4].Success ? wm.Groups[4].Value : wm.Groups[5].Value;
                    clauses.Add(new WhereClause(childField, opKind, val.Trim()));
                }
                var formula = new Formula(tableId, fieldId, FormulaKind.Exist, targetTable, null, clauses);
                if (!_byTable.TryGetValue(tableId, out var list))
                    _byTable[tableId] = list = new List<Formula>();
                list.Add(formula);
            }
        }
    }

    public static Formula? Find(int tableId, int fieldId)
    {
        if (!_byTable.TryGetValue(tableId, out var list)) return null;
        foreach (var f in list)
            if (f.OwnerFieldId == fieldId)
                return f;
        return null;
    }

    public static int? GetTableIdByName(string name)
    {
        return _tableIdByName.TryGetValue(name, out var id) ? id : null;
    }
}
