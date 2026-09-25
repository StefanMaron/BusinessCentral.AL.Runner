// DependencyXmlPortPropertyEncoding — the xmlport properties BC's emitter does NOT copy
// through as AL text: a tableelement's SourceTableView, LinkFields, CalcFields and
// RequestFilterFields, and the object's Permissions (#4471, #4602). BC writes each in its own canonical form, field NAMES replaced by
// Field<n> ids and values re-encoded; the grammar below is exactly the set of shapes measured
// against BC's own emitted documents, and docs/xmlport-metadata-from-bc.md#table-views-link-fields-and-permissions
// has the measurement table.
//
// Anything outside that set THROWS XmlPortPropertyNotEncodableException, and
// BuildDependencyXmlPortMetadata turns that into the same refusal an unrecoverable node tree
// gets. Writing the AL text instead would state a view BC never wrote; omitting it would drop
// the port's sorting and filters from NavXmlPort.ApplyNodeSourceTableViewAndRequestFormFilters.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// An xmlport property whose AL text cannot be put into BC's canonical form — a field name
    /// that does not resolve, or a shape nobody has measured BC's encoding of.
    /// </summary>
    internal sealed class XmlPortPropertyNotEncodableException : Exception
    {
        public XmlPortPropertyNotEncodableException(string message) : base(message) { }
    }

    /// <summary>
    /// <c>sorting("Role ID", "App ID") order(ascending) where(Kind = const(B))</c> ->
    /// <c>SORTING(Field2,Field9) ORDER(0) WHERE(Field2=0(1))</c>, resolved against
    /// <paramref name="tableId"/>. ORDER is written only when the AL states it.
    /// </summary>
    internal static string XmlPortCanonicalTableView(string alText, int tableId, string nodeName)
    {
        string? sorting = null, order = null, where = null;
        foreach (var (keyword, inner) in XmlPortClauses(alText, "SourceTableView", nodeName))
        {
            switch (keyword.ToLowerInvariant())
            {
                case "sorting" when sorting == null:
                    sorting = "SORTING(" + string.Join(",", XmlPortSplitTopLevel(inner, ',')
                        .Select(f => "Field" + XmlPortField(tableId, f, "SourceTableView", nodeName).FieldId
                            .ToString(CultureInfo.InvariantCulture))) + ")";
                    break;
                case "order" when order == null:
                    order = inner.Trim().ToLowerInvariant() switch
                    {
                        "ascending" => "ORDER(0)",
                        "descending" => "ORDER(1)",
                        _ => throw NotEncodable("SourceTableView", nodeName, $"order({inner.Trim()})"),
                    };
                    break;
                case "where" when where == null:
                    var entries = new List<string>();
                    foreach (var entry in XmlPortSplitTopLevel(inner, ','))
                    {
                        var (name, rhs) = XmlPortSplitAssignment(entry, "SourceTableView", nodeName);
                        var field = XmlPortField(tableId, name, "SourceTableView", nodeName);
                        var clauses = XmlPortClauses(rhs, "SourceTableView", nodeName).ToList();
                        if (clauses.Count != 1)
                            throw NotEncodable("SourceTableView", nodeName, entry.Trim());
                        var (kind, value) = clauses[0];
                        var id = field.FieldId.ToString(CultureInfo.InvariantCulture);
                        entries.Add(kind.ToLowerInvariant() switch
                        {
                            "const" => $"Field{id}=0({XmlPortConstValue(field, value, nodeName)})",
                            "filter" => $"Field{id}=1({XmlPortFilterValue(field, value, nodeName)})",
                            _ => throw NotEncodable("SourceTableView", nodeName, entry.Trim()),
                        });
                    }
                    where = "WHERE(" + string.Join(",", entries) + ")";
                    break;
                default:
                    throw NotEncodable("SourceTableView", nodeName, $"{keyword}(...)");
            }
        }
        return string.Join(" ", new[] { sorting, order, where }.Where(p => p != null));
    }

    /// <summary>
    /// <c>"App ID" = field("App ID"), "Role ID" = field("Role ID")</c> ->
    /// <c>Field1=FIELD(Field1),Field2=FIELD(Field2)</c>. The LEFT name resolves against this
    /// tableelement's table, the RIGHT against the table of the node <c>LinkTable</c> names.
    /// </summary>
    internal static string XmlPortCanonicalLinkFields(
        string alText, int tableId, int linkTableId, string nodeName)
    {
        var entries = new List<string>();
        foreach (var entry in XmlPortSplitTopLevel(alText, ','))
        {
            var (name, rhs) = XmlPortSplitAssignment(entry, "LinkFields", nodeName);
            var left = XmlPortField(tableId, name, "LinkFields", nodeName);
            var clauses = XmlPortClauses(rhs, "LinkFields", nodeName).ToList();
            if (clauses.Count != 1 || !clauses[0].Keyword.Equals("field", StringComparison.OrdinalIgnoreCase))
                throw NotEncodable("LinkFields", nodeName, entry.Trim());
            var right = XmlPortField(linkTableId, clauses[0].Inner, "LinkFields", nodeName);
            entries.Add($"Field{left.FieldId.ToString(CultureInfo.InvariantCulture)}"
                + $"=FIELD(Field{right.FieldId.ToString(CultureInfo.InvariantCulture)})");
        }
        if (entries.Count == 0) throw NotEncodable("LinkFields", nodeName, alText);
        return string.Join(",", entries);
    }

    private static readonly Regex XmlPortFieldName = new(
        @"^\s*(""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>CalcFields</c> / <c>RequestFilterFields</c>: <c>"Line Sum", "Line Count"</c> ->
    /// <c>Field12,Field11</c>, in the AL's order, resolved against <paramref name="tableId"/>.
    /// Anything but a plain field name refuses.
    /// </summary>
    internal static string XmlPortCanonicalFieldList(string alText, int tableId, string property, string nodeName)
    {
        var entries = new List<string>();
        foreach (var entry in XmlPortSplitTopLevel(alText, ','))
        {
            if (!XmlPortFieldName.IsMatch(entry)) throw NotEncodable(property, nodeName, entry.Trim());
            entries.Add("Field" + XmlPortField(tableId, entry, property, nodeName).FieldId
                .ToString(CultureInfo.InvariantCulture));
        }
        if (entries.Count == 0) throw NotEncodable(property, nodeName, alText);
        return string.Join(",", entries);
    }

    // A namespace-qualified name keeps only its last segment: BC wrote
    // `tabledata Probe.Alpha.Data."NS Cap Entry" = rimd` as `TableData NS Cap Entry=rimd` (#4650).
    private static readonly Regex XmlPortPermissionEntry = new(
        @"^\s*tabledata\s+(?<name>(?:(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)*"
        + @"(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))\s*=\s*(?<perm>[RIMDrimd]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>tabledata "Security Group" = r</c> -> <c>TableData Security Group=r</c>. The letters
    /// are lower-cased: BC wrote <c>rimd</c> for <c>RIMD</c> and <c>rm</c> for <c>Rm</c>.
    /// An xmlport's Permissions accepts only <c>tabledata</c> entries (AL0104 otherwise).
    /// </summary>
    internal static string XmlPortCanonicalPermissions(string alText, string portName)
    {
        var entries = new List<string>();
        foreach (var entry in XmlPortSplitTopLevel(alText, ','))
        {
            var m = XmlPortPermissionEntry.Match(entry);
            if (!m.Success) throw NotEncodable("Permissions", portName, entry.Trim());
            entries.Add($"TableData {LastNameSegment(m.Groups["name"].Value)}="
                + m.Groups["perm"].Value.ToLowerInvariant());
        }
        if (entries.Count == 0) throw NotEncodable("Permissions", portName, alText);
        return string.Join(",", entries);
    }

    // ── value encodings ──────────────────────────────────────────────────────

    /// <summary>
    /// A <c>const(...)</c> value: an Option member becomes its 0-based position, a Boolean
    /// 1/0, and a Text/Code string literal its unquoted content. Integer and Date literals are
    /// written as AL states them.
    /// </summary>
    private static string XmlPortConstValue(ParsedField field, string value, string nodeName)
    {
        var v = value.Trim();
        switch (XmlPortFieldKind(field))
        {
            case "option":
                return XmlPortOptionOrdinal(field, v, nodeName);
            case "boolean":
                if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
                if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";
                break;
            case "integer":
                if (Regex.IsMatch(v, @"^-?[0-9]+$")) return v;
                break;
            case "code":
            case "text":
                if (AlStringLiteralContent(v) is { Length: > 0 } s) return s;
                break;
            case "date":
                if (Regex.IsMatch(v, @"^[0-9]{8}[Dd]$")) return v;
                break;
        }
        throw NotEncodable("SourceTableView", nodeName, $"{field.FieldName} = const({v})");
    }

    /// <summary>
    /// A <c>filter(...)</c> value: whitespace between tokens dropped (<c>1 .. 5</c> ->
    /// <c>1..5</c>), Option members to positions (<c>A | "C D"</c> -> <c>0|2</c>), string
    /// literals to their content, and the empty literal kept as <c>''</c>.
    /// </summary>
    private static string XmlPortFilterValue(ParsedField field, string value, string nodeName)
    {
        var kind = XmlPortFieldKind(field);
        var sb = new StringBuilder();
        int i = 0;
        string Fail() => throw NotEncodable("SourceTableView", nodeName, $"{field.FieldName} = filter({value.Trim()})");
        while (i < value.Length)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\'')
            {
                int end = XmlPortLiteralEnd(value, i);
                if (end < 0 || (kind != "text" && kind != "code")) Fail();
                var content = value.Substring(i + 1, end - i - 1).Replace("''", "'");
                sb.Append(content.Length == 0 ? "''" : content);
                i = end + 1;
                continue;
            }
            if (c == '"')
            {
                int end = value.IndexOf('"', i + 1);
                if (end < 0 || kind != "option") Fail();
                sb.Append(XmlPortOptionOrdinal(field, value.Substring(i, end - i + 1), nodeName));
                i = end + 1;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                int start = i;
                while (i < value.Length && (char.IsLetterOrDigit(value[i]) || value[i] == '_')) i++;
                var word = value.Substring(start, i - start);
                if (kind == "option") sb.Append(XmlPortOptionOrdinal(field, word, nodeName));
                else if ((kind == "integer" || kind == "decimal") && Regex.IsMatch(word, "^[0-9]+$")) sb.Append(word);
                else Fail();
                continue;
            }
            var op = new[] { "..", "<>", "<=", ">=", "<", ">", "|", "&", "=" }
                .FirstOrDefault(o => string.CompareOrdinal(value, i, o, 0, o.Length) == 0);
            if (op == null) Fail();
            sb.Append(op);
            i += op!.Length;
        }
        if (sb.Length == 0) Fail();
        return sb.ToString();
    }

    private static string XmlPortOptionOrdinal(ParsedField field, string token, string nodeName)
    {
        var name = XmlPortUnquoteIdentifier(token.Trim());
        var members = (field.OptionMembers ?? "").Split(',').Select(m => XmlPortUnquoteIdentifier(m.Trim())).ToList();
        int index = members.FindIndex(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (field.OptionMembers == null || index < 0)
            throw NotEncodable("SourceTableView", nodeName, $"{field.FieldName}: option member '{name}'");
        return index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The field's AL type, lower-cased and without its length; Enum refused.</summary>
    private static string XmlPortFieldKind(ParsedField field)
    {
        if (field.EnumTypeId > 0 || field.EnumTypeName != null) return "enum";
        var t = (field.TypeName ?? "").Trim();
        int bracket = t.IndexOf('[');
        if (bracket > 0) t = t.Substring(0, bracket);
        return t.Trim().ToLowerInvariant();
    }

    // ── AL text helpers ──────────────────────────────────────────────────────

    private static ParsedField XmlPortField(int tableId, string alName, string property, string nodeName)
    {
        var name = XmlPortUnquoteIdentifier(alName.Trim());
        if (tableId > 0 && _parsedTables.TryGetValue(tableId, out var table))
            foreach (var f in GetAllFieldsIncludingExtensions(table))
                if (string.Equals(f.FieldName, name, StringComparison.OrdinalIgnoreCase))
                    return f;
        throw NotEncodable(property, nodeName,
            $"field '{name}' does not resolve on table {tableId.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary><c>kw(inner) kw(inner) ...</c>, parentheses matched outside quotes.</summary>
    private static IEnumerable<(string Keyword, string Inner)> XmlPortClauses(
        string text, string property, string nodeName)
    {
        int i = 0;
        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;
            int start = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            var keyword = text.Substring(start, i - start);
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (keyword.Length == 0 || i >= text.Length || text[i] != '(')
                throw NotEncodable(property, nodeName, text.Trim());
            int close = XmlPortMatchingParen(text, i);
            if (close < 0) throw NotEncodable(property, nodeName, text.Trim());
            yield return (keyword, text.Substring(i + 1, close - i - 1));
            i = close + 1;
        }
    }

    private static int XmlPortMatchingParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'') { i = XmlPortLiteralEnd(s, i); if (i < 0) return -1; continue; }
            if (c == '"') { i = s.IndexOf('"', i + 1); if (i < 0) return -1; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Index of the quote closing the AL string literal opening at <paramref name="open"/>.</summary>
    private static int XmlPortLiteralEnd(string s, int open)
    {
        for (int i = open + 1; i < s.Length; i++)
        {
            if (s[i] != '\'') continue;
            if (i + 1 < s.Length && s[i + 1] == '\'') { i++; continue; }
            return i;
        }
        return -1;
    }

    private static List<string> XmlPortSplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'') { int e = XmlPortLiteralEnd(s, i); i = e < 0 ? s.Length : e; continue; }
            if (c == '"') { int e = s.IndexOf('"', i + 1); i = e < 0 ? s.Length : e; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        parts.Add(s.Substring(start));
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    private static (string Name, string Rhs) XmlPortSplitAssignment(string entry, string property, string nodeName)
    {
        var halves = XmlPortSplitTopLevel(entry, '=');
        if (halves.Count != 2) throw NotEncodable(property, nodeName, entry.Trim());
        return (halves[0], halves[1]);
    }

    private static string XmlPortUnquoteIdentifier(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    /// <summary>The content of an AL string literal, <c>''</c> collapsed; null if not one.</summary>
    private static string? AlStringLiteralContent(string v)
        => v.Length >= 2 && v[0] == '\'' && XmlPortLiteralEnd(v, 0) == v.Length - 1
            ? v.Substring(1, v.Length - 2).Replace("''", "'")
            : null;

    private static XmlPortPropertyNotEncodableException NotEncodable(string property, string where, string what)
        => new($"{property} on '{where}': cannot put '{what}' into BC's canonical form (#4471)");
}
