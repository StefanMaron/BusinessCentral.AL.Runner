using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Runtime;

/// <summary>
/// Transpile-time registry of FlowField <c>CalcFormula</c> declarations
/// parsed from AL table sources. Consulted by
/// <see cref="MockRecordHandle.ALCalcFields"/> so tests that exercise
/// FlowFields can see real in-memory results.
///
/// Supported formula kinds: <c>exist</c>, <c>count</c>, <c>sum</c>,
/// <c>lookup</c>. <c>min</c>, <c>max</c>, and <c>average</c> are parsed
/// but remain no-ops at evaluation time.
/// Where-clause conditions support <c>field(SelfField)</c> references and
/// <c>const(Literal)</c> values. Sum and Lookup also parse a
/// <c>"Table"."Field"</c> dot-notation aggregate target.
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

    // `CalcFormula = exist|count|sum|lookup(...)` — the argument list can
    // contain nested parens (e.g. `where(C1 = field(Code1))`), so regex
    // alone can't capture the full body. We locate the keyword and then
    // walk parens manually.
    private static readonly Regex FormulaKeyword = new(
        @"\bCalcFormula\s*=\s*(exist|count|sum|lookup|min|max|average)\s*\(",
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
                var fk = FormulaKeyword.Match(fieldBody);
                if (!fk.Success) continue;

                var kindText = fk.Groups[1].Value.ToLowerInvariant();
                var kind = kindText switch
                {
                    "exist"   => FormulaKind.Exist,
                    "count"   => FormulaKind.Count,
                    "sum"     => FormulaKind.Sum,
                    "lookup"  => FormulaKind.Lookup,
                    "min"     => FormulaKind.Min,
                    "max"     => FormulaKind.Max,
                    "average" => FormulaKind.Average,
                    _         => FormulaKind.Exist,
                };

                // Walk parens from the `(` of `keyword(` to the matching `)`.
                int openParen = fk.Index + fk.Length - 1;
                int close = FindMatchingParen(fieldBody, openParen);
                if (close < 0) continue;
                var formulaBody = fieldBody.Substring(openParen + 1, close - openParen - 1);

                // For sum/lookup/min/max/average the body is:
                //   "Table"."Field" where(...)
                // For exist/count it's just:
                //   "Table" where(...)
                string targetTable;
                string? targetField = null;
                string whereText = "";

                // Split off a trailing `where(...)` clause first.
                string preWhere;
                var whereIdx = FindTopLevelKeyword(formulaBody, "where");
                if (whereIdx >= 0)
                {
                    preWhere = formulaBody.Substring(0, whereIdx).Trim();
                    int whereOpen = formulaBody.IndexOf('(', whereIdx);
                    if (whereOpen < 0) continue;
                    int whereClose = FindMatchingParen(formulaBody, whereOpen);
                    if (whereClose < 0) continue;
                    whereText = formulaBody.Substring(whereOpen + 1, whereClose - whereOpen - 1);
                }
                else
                {
                    preWhere = formulaBody.Trim();
                }

                // Parse "Table"."Field" or "Table" from preWhere.
                ParseTableAndField(preWhere, out targetTable, out targetField);

                var clauses = new List<WhereClause>();
                foreach (var cond in SplitTopLevelCommas(whereText))
                {
                    var wm = WhereCondition.Match(cond);
                    if (!wm.Success) continue;
                    var childField = wm.Groups[1].Success ? wm.Groups[1].Value : wm.Groups[2].Value;
                    var opKind = wm.Groups[3].Value.ToLowerInvariant();
                    var val = wm.Groups[4].Success ? wm.Groups[4].Value : wm.Groups[5].Value;
                    clauses.Add(new WhereClause(childField, opKind, val.Trim()));
                }

                var formula = new Formula(tableId, fieldId, kind, targetTable, targetField, clauses);
                if (!_byTable.TryGetValue(tableId, out var list))
                    _byTable[tableId] = list = new List<Formula>();
                list.Add(formula);
            }
        }
    }

    /// <summary>
    /// Parse "Table"."Field" or "Table" from the pre-where portion of a formula body.
    /// Handles quoted and unquoted identifiers.
    /// </summary>
    private static void ParseTableAndField(string text, out string tableName, out string? fieldName)
    {
        fieldName = null;
        // Pattern: "Table"."Field" or Table."Field" etc.
        // Split on a dot that separates two quoted or unquoted identifiers.
        var dotMatch = Regex.Match(text,
            @"^\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_ ]*))\s*\.\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_ ]*))\s*$");
        if (dotMatch.Success)
        {
            tableName = (dotMatch.Groups[1].Success ? dotMatch.Groups[1].Value : dotMatch.Groups[2].Value).Trim();
            fieldName = (dotMatch.Groups[3].Success ? dotMatch.Groups[3].Value : dotMatch.Groups[4].Value).Trim();
        }
        else
        {
            tableName = text.Trim().Trim('"');
        }
    }

    private static int FindMatchingParen(string text, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int FindTopLevelKeyword(string text, string keyword)
    {
        int depth = 0;
        for (int i = 0; i <= text.Length - keyword.Length; i++)
        {
            if (text[i] == '(') { depth++; continue; }
            if (text[i] == ')') { depth--; continue; }
            if (depth != 0) continue;
            if (string.Compare(text, i, keyword, 0, keyword.Length,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Word boundary check — previous char must not be alpha.
                if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue;
                int after = i + keyword.Length;
                if (after < text.Length && char.IsLetterOrDigit(text[after])) continue;
                return i;
            }
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevelCommas(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                yield return text.Substring(start, i - start).Trim();
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            var tail = text.Substring(start).Trim();
            if (tail.Length > 0) yield return tail;
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
