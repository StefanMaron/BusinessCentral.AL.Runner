using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

public class MockQueryHandle
{
    public int QueryId { get; }

    private List<Dictionary<int, NavValue>>? _resultSet;
    private int _cursor = -1;
    private readonly Dictionary<int, ColumnFilter> _columnFilters = new();
    private Dictionary<int, NavValue>? _currentRowValues;

    public MockQueryHandle() { }

    public MockQueryHandle(int queryId)
    {
        QueryId = queryId;
    }

    public MockQueryHandle(int queryId, SecurityFiltering securityFiltering)
    {
        QueryId = queryId;
        ALSecurityFiltering = securityFiltering;
    }

    /// <summary>
    /// Opens the query: loads all rows from the in-memory table, applies column filters,
    /// sorts by primary key, and optionally limits to TopNumberOfRows.
    /// Throws <see cref="NotSupportedException"/> if the query is not registered via
    /// <see cref="QueryFieldRegistry"/>.
    /// </summary>
    public bool ALOpen(DataError errorLevel = default)
    {
        var dataItems = QueryFieldRegistry.GetQueryDataItems(QueryId);
        if (dataItems == null || dataItems.Count == 0)
            throw new NotSupportedException(
                $"Query {QueryId} Open requires the BC service tier and is not supported by al-runner. " +
                "Use Record operations instead, or inject the query behind an AL interface.");

        var primaryDataItem = dataItems[0];
        var allRows = MockRecordHandle.GetTableRows(primaryDataItem.TableId);
        var primaryKeyFields = MockRecordHandle.GetPrimaryKeyFieldsForTable(primaryDataItem.TableId);

        var filteredRows = new List<Dictionary<int, NavValue>>();
        foreach (var row in allRows)
        {
            if (RowMatchesAllColumnFilters(row, primaryDataItem))
                filteredRows.Add(row);
        }

        if (primaryKeyFields.Length > 0)
        {
            filteredRows.Sort((rowA, rowB) =>
            {
                foreach (var fieldNo in primaryKeyFields)
                {
                    var valueA = rowA.TryGetValue(fieldNo, out var xA) ? NavValueToString(xA) : "";
                    var valueB = rowB.TryGetValue(fieldNo, out var xB) ? NavValueToString(xB) : "";
                    var comparison = string.Compare(valueA, valueB, StringComparison.OrdinalIgnoreCase);
                    if (comparison != 0)
                        return comparison;
                }
                return 0;
            });
        }

        if (ALTopNumberOfRowsToReturn > 0 && filteredRows.Count > ALTopNumberOfRowsToReturn)
            filteredRows = filteredRows.Take(ALTopNumberOfRowsToReturn).ToList();

        _resultSet = filteredRows;
        _cursor = -1;
        _currentRowValues = null;
        return true;
    }

    /// <summary>
    /// Advances the cursor to the next row. Returns true when a row is available;
    /// false when the result set is exhausted.
    /// Throws <see cref="NotSupportedException"/> if the query has not been opened.
    /// </summary>
    public bool ALRead(DataError errorLevel = default)
    {
        if (_resultSet == null)
            throw new NotSupportedException(
                $"Query {QueryId} Read requires the BC service tier and is not supported by al-runner. " +
                "Use Record operations instead, or inject the query behind an AL interface.");

        _cursor++;
        if (_cursor < _resultSet.Count)
        {
            var dataItems = QueryFieldRegistry.GetQueryDataItems(QueryId);
            if (dataItems != null && dataItems.Count > 0)
            {
                _currentRowValues = new Dictionary<int, NavValue>();
                var currentRow = _resultSet[_cursor];
                foreach (var column in dataItems[0].Columns)
                {
                    if (currentRow.TryGetValue(column.TableFieldNo, out var value))
                        _currentRowValues[column.ColumnHash] = value;
                }
            }
            return true;
        }

        _currentRowValues = null;
        return false;
    }

    /// <summary>Closes the query and releases the result set.</summary>
    public void ALClose()
    {
        _resultSet = null;
        _cursor = -1;
        _currentRowValues = null;
    }

    /// <summary>
    /// Sets a filter expression on the column identified by <paramref name="columnHash"/>.
    /// Percent placeholders (%1, %2, ...) in <paramref name="expr"/> are replaced with the
    /// corresponding values from <paramref name="vals"/>.
    /// </summary>
    public void ALSetFilter(int columnHash, string expr, params NavValue[] vals)
    {
        var resolved = expr;
        for (int i = 0; i < vals.Length; i++)
            resolved = resolved.Replace("%" + (i + 1), NavValueToString(vals[i]));

        _columnFilters[columnHash] = new ColumnFilter
        {
            ColumnHash = columnHash,
            FilterExpression = resolved,
            IsRangeFilter = false
        };
    }

    /// <summary>Clears the range filter for <paramref name="columnHash"/>.</summary>
    public void ALSetRangeSafe(int columnHash, NavType expectedType)
    {
        _columnFilters.Remove(columnHash);
    }

    /// <summary>Sets an exact-match range filter: from == to == <paramref name="value"/>.</summary>
    public void ALSetRangeSafe(int columnHash, NavType expectedType, NavValue value)
    {
        _columnFilters[columnHash] = new ColumnFilter
        {
            ColumnHash = columnHash,
            FromValue = value,
            ToValue = value,
            IsRangeFilter = true
        };
    }

    /// <summary>Sets a range filter: rows where column value is between <paramref name="fromValue"/> and <paramref name="toValue"/>.</summary>
    public void ALSetRangeSafe(int columnHash, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        _columnFilters[columnHash] = new ColumnFilter
        {
            ColumnHash = columnHash,
            FromValue = fromValue,
            ToValue = toValue,
            IsRangeFilter = true
        };
    }

    /// <summary>Returns the value for the column identified by <paramref name="columnId"/>, or the default for <paramref name="expectedType"/> if not available.</summary>
    public NavValue GetColumnValueSafe(int columnId, NavType expectedType)
        => (_currentRowValues != null && _currentRowValues.TryGetValue(columnId, out var value))
            ? value
            : DefaultNavValue(expectedType);

    /// <summary>Returns the option value for the column identified by <paramref name="columnId"/>.</summary>
    public NavValue GetColumnOptionSafe(int columnId, NavType expectedType)
        => GetColumnValueSafe(columnId, expectedType);

    public string ALColumnCaption(int columnNo) => "Column" + columnNo;
    public string ALColumnName(int columnNo) => "Column" + columnNo;
    public int ALColumnNo(int columnNo) => columnNo;
    public string ALGetFilter(int columnNo) => "";

    public int ALTopNumberOfRowsToReturn { get; set; }
    public int ALSkipNumberOfRows { get; set; }
    public SecurityFiltering ALSecurityFiltering { get; set; } = SecurityFiltering.Filtered;
    public string ALGetFilters => "";
    public string Caption => "Query" + QueryId;

    public void ALAssign(MockQueryHandle other) { }

    public MockQueryHandle ALByValue(object? parameter = null) => new MockQueryHandle(QueryId);

    public void Clear()
    {
        ALClose();
        _columnFilters.Clear();
    }

    public object? Invoke(int methodId, object[] args) => null;

    // SaveAs* methods -- not supported in standalone mode
    public static bool ALSaveAsCsv(DataError errorLevel, int queryId, string filePath)
        => throw new NotSupportedException($"Query.SaveAsCsv (Query {queryId}) is not supported in al-runner standalone mode.");

    public static bool ALSaveAsXml(DataError errorLevel, int queryId, string filePath)
        => throw new NotSupportedException($"Query.SaveAsXml (Query {queryId}) is not supported in al-runner standalone mode.");

    public static bool ALSaveAsXml(DataError errorLevel, int queryId, MockOutStream stream)
        => throw new NotSupportedException($"Query.SaveAsXml (Query {queryId}) is not supported in al-runner standalone mode.");

    /// <summary>
    /// BC emits <c>q.ALSaveAsCsv(errorLevel, filePath, maxLines, fieldSeparator)</c> for
    /// <c>QueryInstance.SaveAsCsv(FilePath, MaxLines, FieldSeparator)</c>.
    /// No-op in standalone mode — no real file I/O is performed; returns true to indicate success.
    /// </summary>
    public bool ALSaveAsCsv(DataError errorLevel, string filePath, int maxLines = 0, string fieldSeparator = ",") => true;

    /// <summary>
    /// BC emits <c>q.ALSaveAsXml(errorLevel, filePath)</c> for
    /// <c>QueryInstance.SaveAsXml(FilePath)</c>.
    /// No-op in standalone mode — no real file I/O is performed; returns true to indicate success.
    /// </summary>
    public bool ALSaveAsXml(DataError errorLevel, string filePath) => true;

    public bool ALSaveAsXml(DataError errorLevel, MockOutStream stream)
        => throw new NotSupportedException($"Query.SaveAsXml (Query {QueryId}) is not supported in al-runner standalone mode.");

    public static bool ALSaveAsJson(DataError errorLevel, int queryId, MockOutStream stream)
        => throw new NotSupportedException($"Query.SaveAsJson (Query {queryId}) is not supported in al-runner standalone mode.");

    public bool ALSaveAsJson(DataError errorLevel, MockOutStream stream)
        => throw new NotSupportedException($"Query.SaveAsJson (Query {QueryId}) is not supported in al-runner standalone mode.");

    public static bool ALSaveAsExcel(DataError errorLevel, int queryId, string filePath)
        => throw new NotSupportedException($"Query.SaveAsExcel (Query {queryId}) is not supported in al-runner standalone mode.");

    public bool ALSaveAsExcel(DataError errorLevel, string filePath)
        => throw new NotSupportedException($"Query.SaveAsExcel (Query {QueryId}) is not supported in al-runner standalone mode.");

    // -- Private helpers -----------------------------------------------------

    private class ColumnFilter
    {
        public int ColumnHash;
        public NavValue? FromValue;
        public NavValue? ToValue;
        public string? FilterExpression;
        public bool IsRangeFilter;
    }

    private bool RowMatchesAllColumnFilters(Dictionary<int, NavValue> row, QueryDataItemMeta dataItem)
    {
        foreach (var filter in _columnFilters.Values)
        {
            var column = dataItem.Columns.Find(c => c.ColumnHash == filter.ColumnHash);
            if (column == null)
                continue;

            var fieldValue = row.TryGetValue(column.TableFieldNo, out var rawValue) ? rawValue : null;
            var fieldString = fieldValue != null ? NavValueToString(fieldValue) : "";

            if (filter.IsRangeFilter)
            {
                if (filter.FromValue == null || filter.ToValue == null)
                    continue;

                var fromString = NavValueToString(filter.FromValue);
                var toString = NavValueToString(filter.ToValue);

                if (string.Compare(fieldString, fromString, StringComparison.OrdinalIgnoreCase) < 0
                    || string.Compare(fieldString, toString, StringComparison.OrdinalIgnoreCase) > 0)
                    return false;
            }
            else if (filter.FilterExpression != null)
            {
                if (!MatchesFilterExpression(fieldString, filter.FilterExpression))
                    return false;
            }
        }
        return true;
    }

    private static bool MatchesFilterExpression(string fieldString, string expr)
    {
        foreach (var part in expr.Split('|'))
        {
            var trimmedPart = part.Trim();

            if (trimmedPart.Contains(".."))
            {
                var rangeParts = trimmedPart.Split(new[] { ".." }, 2, StringSplitOptions.None);
                if (rangeParts.Length == 2)
                {
                    var lowerOk = string.IsNullOrEmpty(rangeParts[0].Trim())
                        || string.Compare(fieldString, rangeParts[0].Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
                    var upperOk = string.IsNullOrEmpty(rangeParts[1].Trim())
                        || string.Compare(fieldString, rangeParts[1].Trim(), StringComparison.OrdinalIgnoreCase) <= 0;
                    if (lowerOk && upperOk)
                        return true;
                }
            }
            else if (trimmedPart.Contains('*') || trimmedPart.Contains('?'))
            {
                var pattern = "^" + Regex.Escape(trimmedPart).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                if (Regex.IsMatch(fieldString, pattern, RegexOptions.IgnoreCase))
                    return true;
            }
            else if (string.Equals(fieldString, trimmedPart, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string NavValueToString(NavValue value)
    {
        if (value is NavText text) return text.ToString();
        if (value is NavCode code) return code.ToString();
        if (value is NavInteger navInt) return navInt.Value.ToString();
        if (value is NavDecimal navDec) return navDec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is NavBoolean navBool) return navBool.Value ? "true" : "false";
        if (value is NavBigInteger navBigInt) return navBigInt.Value.ToString();
        return value?.ToString() ?? "";
    }

    private static NavValue DefaultNavValue(NavType type)
        => type switch
        {
            NavType.Integer    => NavInteger.Default,
            NavType.Decimal    => NavDecimal.Default,
            NavType.Text       => NavText.Default(0),
            NavType.Code       => new NavCode(20, ""),
            NavType.Boolean    => NavBoolean.Default,
            NavType.Date       => NavDate.Default,
            NavType.Time       => NavTime.Default,
            NavType.DateTime   => NavDateTime.Default,
            NavType.BigInteger => NavBigInteger.Default,
            NavType.GUID       => new NavGuid(Guid.Empty),
            NavType.Option     => NavInteger.Default,
            _                  => NavText.Default(0)
        };
}
