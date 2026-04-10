namespace AlRunner.Runtime;

using System.Text.RegularExpressions;
using Microsoft.Dynamics.Nav.Runtime;  // NavValue, NavCode, NavText, NavDecimal, NavInteger, NavBoolean, Decimal18
using Microsoft.Dynamics.Nav.Types;     // NavType, DataError

/// <summary>
/// In-memory record store replacing INavRecordHandle + NavRecordHandle.
/// Each instance tracks field values for a single record cursor.
/// A static dictionary acts as the "database" for all tables.
/// Supports SetRange/SetFilter filtering with AND-combination across fields.
/// </summary>
public class MockRecordHandle
{
    private readonly int _tableId;
    private Dictionary<int, NavValue> _fields = new();

    // Global in-memory table store: tableId -> list of rows (each row = dict of fieldNo -> NavValue)
    private static readonly Dictionary<int, List<Dictionary<int, NavValue>>> _tables = new();

    // Current cursor position for iteration
    private int _cursorPosition = -1;
    private List<Dictionary<int, NavValue>>? _currentResultSet;

    // Per-field filters: fieldNo -> filter definition. Multiple fields are AND-combined.
    private readonly Dictionary<int, FieldFilter> _filters = new();

    // Current sort key fields (for ordering)
    private int[]? _currentKeyFields;
    private readonly Dictionary<int, bool> _ascending = new();

    public MockRecordHandle(int tableId)
    {
        _tableId = tableId;
        if (!_tables.ContainsKey(tableId))
            _tables[tableId] = new List<Dictionary<int, NavValue>>();
    }

    /// <summary>Reset all tables between test runs.</summary>
    public static void ResetAll() => _tables.Clear();

    public void ALInit()
    {
        _fields = new Dictionary<int, NavValue>();
    }

    public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        _fields[fieldNo] = value;
    }

    /// <summary>
    /// SetFieldValueSafe with validate flag — 4-arg overload.
    /// The 4th parameter indicates whether to fire OnValidate triggers.
    /// In standalone mode, we just set the value.
    /// </summary>
    public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value, bool validate)
    {
        _fields[fieldNo] = value;
    }

    public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType)
    {
        if (_fields.TryGetValue(fieldNo, out var val))
            return val;
        // For Media/MediaSet types, auto-generate and persist a unique value
        // so that repeated reads return the same value, and different records
        // (or re-inserted records) get different MediaIds.
        if (expectedType == NavType.Media)
        {
            var media = new NavMedia(Guid.NewGuid());
            _fields[fieldNo] = media;
            return media;
        }
        if (expectedType == NavType.MediaSet)
        {
            var mediaSet = new NavMediaSet(Guid.NewGuid());
            _fields[fieldNo] = mediaSet;
            return mediaSet;
        }
        return DefaultForType(expectedType);
    }

    /// <summary>
    /// GetFieldValueSafe with extra bool parameter — 3-arg overload.
    /// The third parameter typically indicates whether to use locale formatting.
    /// In standalone mode, we ignore it and return the field value.
    /// </summary>
    public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType, bool useLocale)
    {
        return GetFieldValueSafe(fieldNo, expectedType);
    }

    /// <summary>For Error() formatting - returns the field value as NavValue.</summary>
    public NavValue GetFieldRefSafe(int fieldNo, NavType expectedType)
    {
        return GetFieldValueSafe(fieldNo, expectedType);
    }

    public void Clear()
    {
        _fields = new Dictionary<int, NavValue>();
        _cursorPosition = -1;
        _currentResultSet = null;
    }

    public bool ALInsert(DataError errorLevel) => ALInsert(errorLevel, false);

    public bool ALInsert(DataError errorLevel, bool runTrigger)
    {
        var table = _tables[_tableId];
        var row = new Dictionary<int, NavValue>(_fields);
        table.Add(row);
        return true;
    }

    public bool ALModify(DataError errorLevel) => ALModify(errorLevel, false);

    public bool ALModify(DataError errorLevel, bool runTrigger)
    {
        var table = _tables[_tableId];
        if (!_fields.ContainsKey(1)) return false;
        var pk = _fields[1].ToString();
        for (int i = 0; i < table.Count; i++)
        {
            if (table[i].ContainsKey(1) && table[i][1].ToString() == pk)
            {
                table[i] = new Dictionary<int, NavValue>(_fields);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
            throw new Exception($"Record not found for Modify in table {_tableId}");
        return false;
    }

    public bool ALGet(DataError errorLevel, params NavValue[] keyValues)
    {
        var table = _tables[_tableId];
        var keyStr = keyValues.Length > 0 ? keyValues[0].ToString() : "";
        foreach (var row in table)
        {
            if (row.ContainsKey(1) && row[1].ToString() == keyStr)
            {
                _fields = new Dictionary<int, NavValue>(row);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
            throw new Exception($"Record not found in table {_tableId} for key '{keyStr}'");
        return false;
    }

    // -----------------------------------------------------------------------
    // Find / iterate methods — all respect active filters
    // -----------------------------------------------------------------------

    public bool ALFind(DataError errorLevel, string searchMethod = "-")
    {
        var filtered = GetFilteredRecords();
        if (filtered.Count == 0)
        {
            _currentResultSet = filtered;
            if (errorLevel == DataError.ThrowError)
                throw new Exception($"No records in table {_tableId}");
            return false;
        }
        _currentResultSet = filtered;

        if (searchMethod == "+" || searchMethod == ">")
        {
            // Position at last record
            _cursorPosition = filtered.Count - 1;
            _fields = new Dictionary<int, NavValue>(filtered[^1]);
        }
        else
        {
            // Default ("-", "=", "<"): position at first record
            _cursorPosition = 0;
            _fields = new Dictionary<int, NavValue>(filtered[0]);
        }
        return true;
    }

    public bool ALFindSet(DataError errorLevel = DataError.ThrowError, bool forUpdate = false)
    {
        return ALFind(errorLevel, "-");
    }

    public bool ALFindFirst(DataError errorLevel = DataError.ThrowError)
    {
        return ALFind(errorLevel, "-");
    }

    public bool ALFindLast(DataError errorLevel = DataError.ThrowError)
    {
        var filtered = GetFilteredRecords();
        if (filtered.Count == 0)
        {
            _currentResultSet = filtered;
            if (errorLevel == DataError.ThrowError)
                throw new Exception($"No records in table {_tableId}");
            return false;
        }
        _currentResultSet = filtered;
        _cursorPosition = filtered.Count - 1;
        _fields = new Dictionary<int, NavValue>(filtered[^1]);
        return true;
    }

    public int ALNext()
    {
        if (_currentResultSet == null || _cursorPosition >= _currentResultSet.Count - 1)
            return 0;
        _cursorPosition++;
        _fields = new Dictionary<int, NavValue>(_currentResultSet[_cursorPosition]);
        return 1;
    }

    public bool ALDelete(DataError errorLevel, bool runTrigger = false)
    {
        var table = _tables[_tableId];
        if (!_fields.ContainsKey(1)) return false;
        var pk = _fields[1].ToString();
        for (int i = 0; i < table.Count; i++)
        {
            if (table[i].ContainsKey(1) && table[i][1].ToString() == pk)
            {
                table.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// ALDeleteAll(bool runTrigger) — overload for when the transpiler emits a bool directly.
    /// In BC, ALDeleteAll(bool) is valid: it means "delete all, with/without triggers".
    /// </summary>
    public void ALDeleteAll(bool runTrigger)
    {
        ALDeleteAll(DataError.ThrowError, runTrigger);
    }

    public void ALDeleteAll(DataError errorLevel = DataError.ThrowError, bool runTrigger = false)
    {
        if (_filters.Count == 0)
        {
            // No filters: delete all records in the table
            _tables[_tableId] = new List<Dictionary<int, NavValue>>();
        }
        else
        {
            // Filters active: only delete matching records
            var table = _tables[_tableId];
            var toRemove = GetFilteredRecords();
            var toRemoveKeys = new HashSet<string>(
                toRemove.Select(r => RowKey(r)));
            table.RemoveAll(r => toRemoveKeys.Contains(RowKey(r)));
        }
    }

    // -----------------------------------------------------------------------
    // SetRange — range-based filtering
    // -----------------------------------------------------------------------

    /// <summary>
    /// SetRange with from/to values. If both are default/empty for the type,
    /// this clears the filter for that field (AL behavior for SETRANGE(field)).
    /// </summary>
    public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        // Check if this is a "clear filter" call: both values are the default for the type
        // In AL, SETRANGE(FieldNo) with no value args clears the filter.
        // The transpiler emits ALSetRange with default values in that case.
        var fromStr = NavValueToString(fromValue);
        var toStr = NavValueToString(toValue);
        var defaultStr = NavValueToString(DefaultForType(expectedType));

        if (fromStr == defaultStr && toStr == defaultStr)
        {
            // Clear filter for this field
            _filters.Remove(fieldNo);
            return;
        }

        _filters[fieldNo] = new FieldFilter
        {
            FieldNo = fieldNo,
            FromValue = fromValue,
            ToValue = toValue,
            IsRangeFilter = true,
        };
    }

    /// <summary>Clear filter for this field (AL: SETRANGE(FieldNo) with no value).</summary>
    public void ALSetRangeSafe(int fieldNo, NavType expectedType)
    {
        _filters.Remove(fieldNo);
    }

    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        // Single value = equality filter (from == to)
        _filters[fieldNo] = new FieldFilter
        {
            FieldNo = fieldNo,
            FromValue = value,
            ToValue = value,
            IsRangeFilter = true,
        };
    }

    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
    {
        ALSetRange(fieldNo, expectedType, fromValue, toValue);
    }

    // -----------------------------------------------------------------------
    // SetFilter — expression-based filtering
    // -----------------------------------------------------------------------

    /// <summary>
    /// SetFilter with a filter expression string and optional substitution args.
    /// Supports: equality ('VALUE'), range ('FROM..TO'), or-list ('V1|V2|V3'),
    /// wildcards ('*text*', 'text*', '*text'), case-insensitive prefix ('@'),
    /// not-equal ('&lt;&gt;VALUE'), and placeholder substitution (%1, %2, etc).
    /// Empty expression clears the filter for that field.
    /// </summary>
    /// <summary>Overload without NavType (transpiler emits this form).</summary>
    public void ALSetFilter(int fieldNo, string filterExpression, params NavValue[] args)
    {
        ALSetFilter(fieldNo, NavType.Text, filterExpression, args);
    }

    public void ALSetFilter(int fieldNo, NavType expectedType, string filterExpression, params NavValue[] args)
    {
        // Substitute %1, %2, ... with args
        var expr = SubstitutePlaceholders(filterExpression, args);

        // Empty expression clears filter
        if (string.IsNullOrEmpty(expr))
        {
            _filters.Remove(fieldNo);
            return;
        }

        _filters[fieldNo] = new FieldFilter
        {
            FieldNo = fieldNo,
            FilterExpression = expr,
            IsRangeFilter = false,
        };
    }

    // -----------------------------------------------------------------------
    // SetCurrentKey — set sort key (with DataError first param)
    // -----------------------------------------------------------------------

    public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos)
    {
        _currentKeyFields = fieldNos;
    }

    /// <summary>Legacy overload without DataError (kept for backward compat).</summary>
    public void ALSetCurrentKey(params int[] fieldNos)
    {
        _currentKeyFields = fieldNos;
    }

    public void ALSetAscending(int fieldNo, bool ascending)
    {
        _ascending[fieldNo] = ascending;
    }

    /// <summary>
    /// AL's FILTERGROUP — sets the filter group for subsequent filter operations.
    /// In BC, filter groups isolate filters. In standalone mode, this is a no-op
    /// since we don't track filter groups.
    /// Exposed as a property so the transpiler can emit both method-call and assignment forms:
    ///   rec.ALFilterGroup(2)   → method call (via the method overload below)
    ///   rec.ALFilterGroup = 2  → property setter
    /// </summary>
    public int ALFilterGroup
    {
        get => _filterGroup;
        set { /* No-op: filter groups not implemented in standalone mode */ }
    }
    private int _filterGroup;

    /// <summary>Method overload for ALFilterGroup(int) call syntax.</summary>
    public void SetALFilterGroup(int groupId)
    {
        // No-op: filter groups not implemented in standalone mode
    }

    // -----------------------------------------------------------------------
    // Reset — clears filters, fields, and cursor
    // -----------------------------------------------------------------------

    public void ALReset()
    {
        _fields = new Dictionary<int, NavValue>();
        _cursorPosition = -1;
        _currentResultSet = null;
        _filters.Clear();
        _currentKeyFields = null;
        _ascending.Clear();
    }

    // -----------------------------------------------------------------------
    // Count / IsEmpty — respect active filters
    // -----------------------------------------------------------------------

    public int ALCount => GetFilteredRecords().Count;

    public bool ALIsEmpty => GetFilteredRecords().Count == 0;

    public int ALFieldNo(string fieldName)
    {
        // Simplified: return 0 (unknown) -- real impl would look up field metadata
        return 0;
    }

    /// <summary>
    /// AL's FIELDNO(fieldNo) — returns the field number for a given field number.
    /// In BC, this overload accepts a field enum value (int). Returns it directly.
    /// </summary>
    public int ALFieldNo(int fieldNo)
    {
        return fieldNo;
    }

    // -----------------------------------------------------------------------
    // Validate — sets field value (triggers are not implemented)
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's VALIDATE(FieldNo, Value) — sets the field and would fire OnValidate trigger.
    /// In standalone mode we just set the value since we don't have trigger infrastructure.
    /// </summary>
    public void ALValidateSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        _fields[fieldNo] = value;
    }

    /// <summary>
    /// ALValidate overload matching transpiler output pattern: ALValidate(DataError, fieldNo, NavType, value)
    /// </summary>
    public void ALValidate(DataError errorLevel, int fieldNo, NavType expectedType, NavValue value)
    {
        _fields[fieldNo] = value;
    }

    // -----------------------------------------------------------------------
    // TestField — asserts field has expected value
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's TESTFIELD(FieldNo) — asserts the field is non-empty/non-default (Safe variant).
    /// </summary>
    public void ALTestFieldSafe(int fieldNo, NavType expectedType)
    {
        var actual = GetFieldValueSafe(fieldNo, expectedType);
        var actualStr = NavValueToString(actual);
        var defaultStr = NavValueToString(DefaultForType(expectedType));
        if (actualStr == defaultStr)
            throw new Exception($"TestField failed: field {fieldNo} in table {_tableId} must have a value");
    }

    /// <summary>
    /// AL's TESTFIELD(FieldNo, Value) — asserts the field equals the expected value.
    /// </summary>
    public void ALTestFieldSafe(int fieldNo, NavType expectedType, NavValue expectedValue)
    {
        var actual = GetFieldValueSafe(fieldNo, expectedType);
        var actualStr = NavValueToString(actual);
        var expectedStr = NavValueToString(expectedValue);
        if (actualStr != expectedStr)
            throw new Exception($"TestField failed: field {fieldNo} in table {_tableId} expected '{expectedStr}' but was '{actualStr}'");
    }

    /// <summary>AL's TestField for NavValue comparisons (used in some transpiler patterns).</summary>
    public void ALTestFieldNavValueSafe(int fieldNo, NavType expectedType, NavValue expectedValue)
    {
        ALTestFieldSafe(fieldNo, expectedType, expectedValue);
    }

    /// <summary>Overload: TestField with DataError level.</summary>
    public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType, NavValue expectedValue)
    {
        ALTestFieldSafe(fieldNo, expectedType, expectedValue);
    }

    /// <summary>
    /// AL's TESTFIELD(FieldNo) — asserts the field is non-empty/non-default.
    /// </summary>
    public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType)
    {
        var actual = GetFieldValueSafe(fieldNo, expectedType);
        var actualStr = NavValueToString(actual);
        var defaultStr = NavValueToString(DefaultForType(expectedType));
        if (actualStr == defaultStr)
            throw new Exception($"TestField failed: field {fieldNo} in table {_tableId} must have a value");
    }

    // -----------------------------------------------------------------------
    // CalcFields / CalcSums — stubs (no SQL aggregation available)
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's CALCFIELDS — calculates FlowFields. No-op in standalone mode since we don't
    /// have the underlying SQL/CalcFormula infrastructure. The field retains its current value.
    /// </summary>
    public void ALCalcFields(DataError errorLevel, params int[] fieldNos)
    {
        // No-op: FlowFields not supported in standalone mode
    }

    /// <summary>
    /// AL's CALCSUMS — calculates sum of specified fields across filtered records.
    /// Returns true; the actual sum is not computed (would need field metadata to know types).
    /// </summary>
    public bool ALCalcSums(DataError errorLevel, params int[] fieldNos)
    {
        // No-op stub: real implementation would sum over filtered records
        return true;
    }

    // -----------------------------------------------------------------------
    // SetLoadFields — performance hint, no-op in standalone mode
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's SETLOADFIELDS — tells the runtime to only load specified fields from SQL.
    /// No-op in standalone mode since all fields are always in memory.
    /// </summary>
    public void ALSetLoadFields(params int[] fieldNos)
    {
        // No-op: all fields always loaded in in-memory store
    }

    // -----------------------------------------------------------------------
    // FieldCaption — returns field name (stubbed)
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's FIELDCAPTION — returns the caption of a field. Returns a placeholder
    /// since we don't have metadata infrastructure.
    /// </summary>
    public NavText ALFieldCaption(int fieldNo)
    {
        return new NavText($"Field{fieldNo}");
    }

    // -----------------------------------------------------------------------
    // LockTable — no-op in standalone mode
    // -----------------------------------------------------------------------

    public void ALLockTable(DataError errorLevel = DataError.ThrowError)
    {
        // No-op: no SQL transaction isolation needed
    }

    /// <summary>
    /// AL's READISOLATION — sets read isolation level for the record.
    /// No-op in standalone mode since there's no SQL transaction.
    /// Uses object type to accept ALIsolationLevel enum without direct dependency.
    /// </summary>
    public object ALReadIsolation
    {
        get => 0;
        set { /* No-op */ }
    }

    // -----------------------------------------------------------------------
    // Copy — copies field values and filters from another record handle
    // -----------------------------------------------------------------------

    /// <summary>
    /// ALAssign — assigns the record from another handle (used in ByRef patterns).
    /// </summary>
    public void ALAssign(MockRecordHandle other)
    {
        _fields = new Dictionary<int, NavValue>(other._fields);
    }

    public void ALCopy(MockRecordHandle source, bool shareFilters = false)
    {
        _fields = new Dictionary<int, NavValue>(source._fields);
        if (shareFilters)
        {
            // Copy filters too
            _filters.Clear();
            foreach (var kv in source._filters)
                _filters[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// AL's COPYFILTER(fromFieldNo, toRecord, toFieldNo) — copies the filter from one field
    /// on this record to a field on another record.
    /// </summary>
    public void ALCopyFilter(int fromFieldNo, MockRecordHandle target, int toFieldNo)
    {
        if (_filters.TryGetValue(fromFieldNo, out var filter))
        {
            target._filters[toFieldNo] = new FieldFilter
            {
                FieldNo = toFieldNo,
                FromValue = filter.FromValue,
                ToValue = filter.ToValue,
                FilterExpression = filter.FilterExpression,
                IsRangeFilter = filter.IsRangeFilter,
            };
        }
        else
        {
            target._filters.Remove(toFieldNo);
        }
    }

    /// <summary>
    /// AL's COPYFILTERS(fromRecord) — copies all filters from the source record.
    /// </summary>
    public void ALCopyFilters(MockRecordHandle source)
    {
        _filters.Clear();
        foreach (var kv in source._filters)
            _filters[kv.Key] = new FieldFilter
            {
                FieldNo = kv.Key,
                FromValue = kv.Value.FromValue,
                ToValue = kv.Value.ToValue,
                FilterExpression = kv.Value.FilterExpression,
                IsRangeFilter = kv.Value.IsRangeFilter,
            };
    }

    // -----------------------------------------------------------------------
    // Rename — stub
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's SETRECFILTER — sets a filter based on the current record's primary key values.
    /// In standalone mode, sets a range filter on field 1 using the current PK value.
    /// </summary>
    public void ALSetRecFilter()
    {
        if (_fields.TryGetValue(1, out var pkValue))
        {
            _filters[1] = new FieldFilter
            {
                FieldNo = 1,
                FromValue = pkValue,
                ToValue = pkValue,
                IsRangeFilter = true,
            };
        }
    }

    /// <summary>
    /// AL's TABLECAPTION — returns the caption of the table.
    /// Returns a placeholder since we don't have metadata.
    /// </summary>
    public string ALTableCaption => $"Table{_tableId}";

    /// <summary>
    /// AL's ISTEMPORARY — returns whether this is a temporary record.
    /// In standalone mode, all records are effectively in-memory, returns false.
    /// </summary>
    public bool ALIsTemporary => false;

    /// <summary>
    /// AL's TABLENAME — returns the name of the table.
    /// </summary>
    public string ALTableName => $"Table{_tableId}";

    public bool ALRename(DataError errorLevel, params NavValue[] newKeyValues)
    {
        // Stub: just update field 1 with new key
        if (newKeyValues.Length > 0)
            _fields[1] = newKeyValues[0];
        return true;
    }

    // =======================================================================
    // Filter infrastructure
    // =======================================================================

    /// <summary>
    /// Per-field filter definition. Either a range (from/to) or a parsed expression.
    /// </summary>
    private class FieldFilter
    {
        public int FieldNo;
        public NavValue? FromValue;   // For range filters
        public NavValue? ToValue;     // For range filters
        public string? FilterExpression; // For SetFilter expression filters
        public bool IsRangeFilter;    // true = SetRange, false = SetFilter
    }

    /// <summary>
    /// Returns the subset of table rows matching all active filters.
    /// When no filters are set, returns all rows (preserving existing behavior).
    /// </summary>
    private List<Dictionary<int, NavValue>> GetFilteredRecords()
    {
        var table = _tables[_tableId];
        if (_filters.Count == 0)
            return table;

        var result = new List<Dictionary<int, NavValue>>();
        foreach (var row in table)
        {
            if (RowMatchesAllFilters(row))
                result.Add(row);
        }
        return result;
    }

    /// <summary>Check if a single row matches ALL active filters (AND-combined).</summary>
    private bool RowMatchesAllFilters(Dictionary<int, NavValue> row)
    {
        foreach (var filter in _filters.Values)
        {
            if (!RowMatchesFilter(row, filter))
                return false;
        }
        return true;
    }

    /// <summary>Check if a single row matches a specific field filter.</summary>
    private bool RowMatchesFilter(Dictionary<int, NavValue> row, FieldFilter filter)
    {
        var fieldValue = row.TryGetValue(filter.FieldNo, out var val) ? val : null;
        var fieldStr = fieldValue != null ? NavValueToString(fieldValue) : "";

        if (filter.IsRangeFilter)
        {
            return MatchesRange(fieldValue, fieldStr, filter.FromValue!, filter.ToValue!);
        }
        else
        {
            return MatchesFilterExpression(fieldValue, fieldStr, filter.FilterExpression!);
        }
    }

    // -----------------------------------------------------------------------
    // Range matching (SetRange)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Check if a field value falls within [from, to] range.
    /// Uses IComparable when available, falls back to string comparison.
    /// When from == to, this is an equality check.
    /// </summary>
    private static bool MatchesRange(NavValue? fieldValue, string fieldStr, NavValue from, NavValue to)
    {
        var fromStr = NavValueToString(from);
        var toStr = NavValueToString(to);

        // Equality shortcut: from == to
        if (fromStr == toStr)
            return string.Equals(fieldStr, fromStr, StringComparison.Ordinal);

        // Try IComparable-based comparison for proper numeric/date ordering
        if (fieldValue is IComparable comparable && from is IComparable && to is IComparable)
        {
            try
            {
                var cmpFrom = comparable.CompareTo(from);
                var cmpTo = comparable.CompareTo(to);
                return cmpFrom >= 0 && cmpTo <= 0;
            }
            catch
            {
                // Fall through to string comparison
            }
        }

        // String comparison fallback
        return string.Compare(fieldStr, fromStr, StringComparison.Ordinal) >= 0 &&
               string.Compare(fieldStr, toStr, StringComparison.Ordinal) <= 0;
    }

    // -----------------------------------------------------------------------
    // Expression matching (SetFilter)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluate a filter expression against a field value.
    /// Supports: equality, range (FROM..TO), or-list (V1|V2|V3),
    /// wildcards (* for any), case-insensitive (@), not-equal (&lt;&gt;),
    /// comparison operators (&gt;, &gt;=, &lt;, &lt;=).
    /// Pipe-separated alternatives are OR-combined.
    /// </summary>
    private static bool MatchesFilterExpression(NavValue? fieldValue, string fieldStr, string expression)
    {
        // Split on | for OR alternatives (but not inside quotes)
        var alternatives = SplitOrAlternatives(expression);

        foreach (var alt in alternatives)
        {
            if (MatchesSingleExpression(fieldValue, fieldStr, alt.Trim()))
                return true;
        }
        return false;
    }

    /// <summary>Evaluate a single filter expression (no OR pipe).</summary>
    private static bool MatchesSingleExpression(NavValue? fieldValue, string fieldStr, string expr)
    {
        if (string.IsNullOrEmpty(expr))
            return true;

        // Check for case-insensitive prefix @
        bool caseInsensitive = false;
        var working = expr;
        if (working.StartsWith('@'))
        {
            caseInsensitive = true;
            working = working.Substring(1);
        }

        // Strip surrounding single quotes if present
        working = StripQuotes(working);

        // Check for not-equal: <>VALUE
        if (working.StartsWith("<>"))
        {
            var notVal = working.Substring(2);
            return !StringEquals(fieldStr, notVal, caseInsensitive);
        }

        // Check for comparison operators: >=, <=, >, <
        if (working.StartsWith(">="))
        {
            var cmpVal = working.Substring(2);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) >= 0;
        }
        if (working.StartsWith("<="))
        {
            var cmpVal = working.Substring(2);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) <= 0;
        }
        if (working.StartsWith(">") && !working.StartsWith(">="))
        {
            var cmpVal = working.Substring(1);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) > 0;
        }
        if (working.StartsWith("<") && !working.StartsWith("<=") && !working.StartsWith("<>"))
        {
            var cmpVal = working.Substring(1);
            return StringCompareOp(fieldStr, cmpVal, caseInsensitive) < 0;
        }

        // Check for range: FROM..TO
        var dotDotIdx = working.IndexOf("..", StringComparison.Ordinal);
        if (dotDotIdx >= 0)
        {
            var rangeFrom = working.Substring(0, dotDotIdx);
            var rangeTo = working.Substring(dotDotIdx + 2);
            var cmpFrom = StringCompareOp(fieldStr, rangeFrom, caseInsensitive);
            var cmpTo = StringCompareOp(fieldStr, rangeTo, caseInsensitive);
            return cmpFrom >= 0 && cmpTo <= 0;
        }

        // Check for wildcards: * means any characters
        if (working.Contains('*') || working.Contains('?'))
        {
            return WildcardMatch(fieldStr, working, caseInsensitive);
        }

        // Plain equality
        return StringEquals(fieldStr, working, caseInsensitive);
    }

    // -----------------------------------------------------------------------
    // String/comparison helpers
    // -----------------------------------------------------------------------

    private static bool StringEquals(string a, string b, bool caseInsensitive)
    {
        return string.Equals(a, b,
            caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static int StringCompareOp(string a, string b, bool caseInsensitive)
    {
        // Try numeric comparison first if both parse as decimal
        if (decimal.TryParse(a, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var decA) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var decB))
        {
            return decA.CompareTo(decB);
        }

        return string.Compare(a, b,
            caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Simple wildcard matching: * = any sequence of chars, ? = any single char.
    /// Converts to regex for matching.
    /// </summary>
    private static bool WildcardMatch(string input, string pattern, bool caseInsensitive)
    {
        // Escape regex specials except * and ?
        var regexPattern = "^" +
            Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") +
            "$";
        var options = RegexOptions.Singleline;
        if (caseInsensitive)
            options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(input, regexPattern, options);
    }

    /// <summary>Strip surrounding single quotes from a filter value.</summary>
    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
            return s.Substring(1, s.Length - 2);
        return s;
    }

    /// <summary>
    /// Split a filter expression on | (pipe) for OR alternatives.
    /// Respects single-quoted strings (pipe inside quotes is literal).
    /// </summary>
    private static List<string> SplitOrAlternatives(string expression)
    {
        var parts = new List<string>();
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (expression[i] == '|' && !inQuotes)
            {
                parts.Add(expression.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(expression.Substring(start));
        return parts;
    }

    /// <summary>
    /// Substitute %1, %2, ... placeholders in a filter expression with NavValue args.
    /// </summary>
    private static string SubstitutePlaceholders(string expression, NavValue[] args)
    {
        if (args.Length == 0)
            return expression;

        var result = expression;
        for (int i = args.Length; i >= 1; i--)
        {
            result = result.Replace($"%{i}", NavValueToString(args[i - 1]));
        }
        return result;
    }

    /// <summary>
    /// Convert a NavValue to its string representation for comparison.
    /// Uses explicit casts to avoid any NavSession dependency from ToString().
    /// </summary>
    private static string NavValueToString(NavValue value)
    {
        if (value is NavText nt) return (string)nt;
        if (value is NavCode nc) return (string)nc;
        if (value is NavInteger ni) return ((int)ni).ToString();
        if (value is NavDecimal nd)
        {
            try { return Convert.ToDecimal(nd.GetType().GetProperty("Value")?.GetValue(nd)).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch { }
        }
        if (value is NavBoolean nb) return ((bool)nb).ToString();
        if (value is NavBigInteger nbi) return ((long)nbi).ToString();
        if (value is NavGuid ng) return ((Guid)ng).ToString();
        // Fallback
        try { return value.ToString() ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Generate a simple row key for identity comparison (used by ALDeleteAll with filters).
    /// </summary>
    private static string RowKey(Dictionary<int, NavValue> row)
    {
        // Use all fields sorted by field number for a unique-ish key
        var parts = row.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={NavValueToString(kv.Value)}");
        return string.Join("|", parts);
    }

    private static NavValue DefaultForType(NavType navType)
    {
        return navType switch
        {
            NavType.Decimal => NavDecimal.Default,
            NavType.Integer => NavInteger.Default,
            NavType.Text => NavText.Default(0),
            NavType.Code => new NavCode(20, ""),
            NavType.Boolean => NavBoolean.Default,
            NavType.Media => NavMedia.Default,
            NavType.MediaSet => NavMediaSet.Default,
            _ => NavText.Default(0)
        };
    }

    /// <summary>
    /// AL's TRANSFERFIELDS — copies field values from another record.
    /// In standalone mode, copies all fields from source to this record.
    /// </summary>
    public void ALTransferFields(MockRecordHandle source, bool initPrimaryKey = true)
    {
        foreach (var kv in source._fields)
        {
            if (!initPrimaryKey && kv.Key == 1) continue; // skip PK field
            _fields[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// AL's MARK — marks or unmarks the current record.
    /// In standalone mode, no-op (marking is used for filtered iteration).
    /// </summary>
    public void ALMark(bool mark = true)
    {
        // No-op: record marking not implemented in standalone mode
    }

    /// <summary>
    /// AL's MARKEDONLY — sets/gets whether to only iterate over marked records.
    /// Exposed as both a property (for assignment: rec.ALMarkedOnly = true) and a method.
    /// In standalone mode, no-op.
    /// </summary>
    public bool ALMarkedOnly
    {
        get => _markedOnly;
        set { /* No-op: record marking not implemented in standalone mode */ }
    }
    private bool _markedOnly;

    /// <summary>
    /// AL SetAutoCalcFields — stub, no-op in standalone mode.
    /// In BC, this configures automatic calculation of FlowFields.
    /// </summary>
    public void ALSetAutoCalcFields(params object[] fields)
    {
        // No-op: FlowFields not supported in standalone mode
    }

    /// <summary>
    /// AL GetFilter — stub returning empty string.
    /// In BC, this returns the current filter expression as a string.
    /// </summary>
    public string ALGetFilter()
    {
        return "";
    }

    /// <summary>
    /// AL GetFilter(fieldNo) — stub returning empty string for specific field.
    /// </summary>
    public string ALGetFilter(int fieldNo)
    {
        return "";
    }

    /// <summary>
    /// AL GetFilters — returns all active filters as a single string.
    /// </summary>
    public string ALGetFilters()
    {
        return "";
    }

    /// <summary>
    /// AL's GETRANGEMIN — returns the minimum value of the filter range for a field.
    /// </summary>
    public NavValue ALGetRangeMinSafe(int fieldNo, NavType expectedType)
    {
        if (_filters.TryGetValue(fieldNo, out var filter) && filter.FromValue != null)
            return filter.FromValue;
        return DefaultForType(expectedType);
    }

    /// <summary>
    /// AL's GETRANGEMAX — returns the maximum value of the filter range for a field.
    /// </summary>
    public NavValue ALGetRangeMaxSafe(int fieldNo, NavType expectedType)
    {
        if (_filters.TryGetValue(fieldNo, out var filter) && filter.ToValue != null)
            return filter.ToValue;
        return DefaultForType(expectedType);
    }

    /// <summary>
    /// Invoke method (for cross-object method calls on records used as codeunit-like objects).
    /// Records in BC can have methods that are called via member IDs, similar to codeunits.
    /// In standalone mode, we use reflection to find and call the method.
    /// </summary>
    /// <summary>
    /// Invoke overload with DataError parameter — matches transpiler pattern
    /// for record method dispatch with error handling level.
    /// </summary>
    public object? Invoke(DataError errorLevel, int memberId, object[] args)
    {
        return Invoke(memberId, args);
    }

    public object? Invoke(int memberId, object[] args)
    {
        // Delegate to MockCodeunitHandle-style dispatch on the record type
        var assembly = MockCodeunitHandle.CurrentAssembly ?? System.Reflection.Assembly.GetExecutingAssembly();
        var recordTypeName = $"Record{_tableId}";
        var recordType = assembly.GetTypes().FirstOrDefault(t => t.Name == recordTypeName);
        if (recordType == null)
            throw new InvalidOperationException($"Record type {recordTypeName} not found in assembly for Invoke");

        // Find scope class matching the member ID
        var absMemberId = Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();
        foreach (var nested in recordType.GetNestedTypes(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
        {
            if (nested.Name.Contains($"_Scope_{memberIdStr}") ||
                nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                var scopeIdx = nested.Name.IndexOf("_Scope_");
                if (scopeIdx < 0) continue;
                var methodName = nested.Name.Substring(0, scopeIdx);
                var method = recordType.GetMethod(methodName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method == null) continue;

                var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(recordType);
                var parameters = method.GetParameters();
                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length && i < args.Length; i++)
                    convertedArgs[i] = args[i];
                return method.Invoke(instance, convertedArgs);
            }
        }

        throw new InvalidOperationException(
            $"Method with member ID {memberId} not found in record type {recordTypeName}");
    }
}
