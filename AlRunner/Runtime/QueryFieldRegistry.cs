using System.Text;
using System.Text.RegularExpressions;

namespace AlRunner.Runtime;

public record QueryColumnMeta(string ColumnName, int ColumnHash, int TableFieldNo, string TableFieldName);
public record QueryDataItemMeta(string DataItemName, string SourceTableName, int TableId, List<QueryColumnMeta> Columns);

public static class QueryFieldRegistry
{
    private static readonly Regex QueryHeader = new(@"\bquery\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DataItemDecl = new(@"\bdataitem\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColumnDecl = new(@"\bcolumn\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Dictionary<int, List<QueryDataItemMeta>> _queries = new();
    public static int GetMemberId(string memberName) { int hash = GetFNVHashCode(Encoding.Unicode.GetBytes(memberName)); return hash == int.MinValue ? int.MaxValue : Math.Abs(hash); }
    private static int GetFNVHashCode(byte[] data) { unchecked { int h = (int)0x811c9dc5; for (int i = 0; i < data.Length; i++) h = (h ^ data[i]) * 16777619; return h; } }
    public static void ParseAndRegister(string alSource) { foreach (Match qm in QueryHeader.Matches(alSource)) { if (!int.TryParse(qm.Groups[1].Value, out var queryId)) continue; int start = qm.Index + qm.Length; int depth = 1; int i = start; while (i < alSource.Length && depth > 0) { if (alSource[i] == '{') depth++; else if (alSource[i] == '}') depth--; i++; } if (depth != 0) continue; _queries[queryId] = ParseDataItems(alSource.Substring(start, i - start - 1)); } }
    private static List<QueryDataItemMeta> ParseDataItems(string body) { var result = new List<QueryDataItemMeta>(); foreach (Match dim in DataItemDecl.Matches(body)) { var dn = dim.Groups[1].Value; var stn = dim.Groups[2].Success ? dim.Groups[2].Value : dim.Groups[3].Value; int tid = TableFieldRegistry.GetTableIdByName(stn) ?? 0; int ds = dim.Index + dim.Length; int d = 0; int dbs = -1; int j = ds; while (j < body.Length) { if (body[j] == '{') { if (d == 0) dbs = j + 1; d++; } else if (body[j] == '}') { d--; if (d == 0) break; } j++; } var cols = new List<QueryColumnMeta>(); if (dbs >= 0 && j > dbs) { var db = body.Substring(dbs, j - dbs); foreach (Match cm in ColumnDecl.Matches(db)) { var cn = cm.Groups[1].Value; var fn = cm.Groups[2].Success ? cm.Groups[2].Value : cm.Groups[3].Value; cols.Add(new QueryColumnMeta(cn, GetMemberId(cn), TableFieldRegistry.GetFieldId(tid, fn) ?? 0, fn)); } } result.Add(new QueryDataItemMeta(dn, stn, tid, cols)); } return result; }
    public static List<QueryDataItemMeta>? GetQueryDataItems(int queryId) => _queries.TryGetValue(queryId, out var items) ? items : null;
    public static void Clear() => _queries.Clear();
}
