using System.Text;
using System.Text.RegularExpressions;

namespace AlRunner.Runtime;

public record QueryColumnMeta(string ColumnName, int ColumnHash, int TableFieldNo, string TableFieldName);
public record QueryDataItemMeta(string DataItemName, string SourceTableName, int TableId, List<QueryColumnMeta> Columns);

public static class QueryFieldRegistry
{
    private static readonly Regex QueryHeader = new(
        @"\bquery\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DataItemDecl = new(
        @"\bdataitem\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ColumnDecl = new(
        @"\bcolumn\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*;\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<int, List<QueryDataItemMeta>> _queries = new();

    /// <summary>
    /// Returns the FNV-1a member ID hash used by the BC compiler for the given member name.
    /// This matches how the transpiler generates column hash constants.
    /// </summary>
    public static int GetMemberId(string memberName)
    {
        int hash = GetFNVHashCode(Encoding.Unicode.GetBytes(memberName));
        return hash == int.MinValue ? int.MaxValue : Math.Abs(hash);
    }

    private static int GetFNVHashCode(byte[] data)
    {
        unchecked
        {
            int hash = (int)0x811c9dc5;
            for (int i = 0; i < data.Length; i++)
                hash = (hash ^ data[i]) * 16777619;
            return hash;
        }
    }

    /// <summary>
    /// Parses all query declarations in <paramref name="alSource"/> and registers
    /// their data items and columns in the registry for use by <see cref="MockQueryHandle"/>.
    /// </summary>
    public static void ParseAndRegister(string alSource)
    {
        foreach (Match queryMatch in QueryHeader.Matches(alSource))
        {
            if (!int.TryParse(queryMatch.Groups[1].Value, out var queryId))
                continue;

            int start = queryMatch.Index + queryMatch.Length;
            int depth = 1;
            int i = start;
            while (i < alSource.Length && depth > 0)
            {
                if (alSource[i] == '{') depth++;
                else if (alSource[i] == '}') depth--;
                i++;
            }

            if (depth != 0)
                continue;

            _queries[queryId] = ParseDataItems(alSource.Substring(start, i - start - 1));
        }
    }

    private static List<QueryDataItemMeta> ParseDataItems(string body)
    {
        var result = new List<QueryDataItemMeta>();

        foreach (Match dataItemMatch in DataItemDecl.Matches(body))
        {
            var dataItemName = dataItemMatch.Groups[1].Value;
            var sourceTableName = dataItemMatch.Groups[2].Success
                ? dataItemMatch.Groups[2].Value
                : dataItemMatch.Groups[3].Value;

            int tableId = TableFieldRegistry.GetTableIdByName(sourceTableName) ?? 0;

            // Find the body of this dataitem block
            int searchStart = dataItemMatch.Index + dataItemMatch.Length;
            int braceDepth = 0;
            int bodyStart = -1;
            int j = searchStart;

            while (j < body.Length)
            {
                if (body[j] == '{')
                {
                    if (braceDepth == 0)
                        bodyStart = j + 1;
                    braceDepth++;
                }
                else if (body[j] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                        break;
                }
                j++;
            }

            var columns = new List<QueryColumnMeta>();
            if (bodyStart >= 0 && j > bodyStart)
            {
                var dataItemBody = body.Substring(bodyStart, j - bodyStart);
                foreach (Match columnMatch in ColumnDecl.Matches(dataItemBody))
                {
                    var columnName = columnMatch.Groups[1].Value;
                    var fieldName = columnMatch.Groups[2].Success
                        ? columnMatch.Groups[2].Value
                        : columnMatch.Groups[3].Value;

                    columns.Add(new QueryColumnMeta(
                        columnName,
                        GetMemberId(columnName),
                        TableFieldRegistry.GetFieldId(tableId, fieldName) ?? 0,
                        fieldName));
                }
            }

            result.Add(new QueryDataItemMeta(dataItemName, sourceTableName, tableId, columns));
        }

        return result;
    }

    /// <summary>Returns the registered data items for <paramref name="queryId"/>, or null if not registered.</summary>
    public static List<QueryDataItemMeta>? GetQueryDataItems(int queryId)
        => _queries.TryGetValue(queryId, out var items) ? items : null;

    /// <summary>Clears all registered query metadata. Called between pipeline runs.</summary>
    public static void Clear() => _queries.Clear();
}
