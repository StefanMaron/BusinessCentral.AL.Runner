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
    private const int SystemIdFieldNo = 2000000000;
    private const int SystemModifiedAtFieldNo = 2000000003;

    private readonly int _tableId;
    /// <summary>The table ID this handle is bound to.</summary>
    public int TableId => _tableId;
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
    private string _viewText = string.Empty;

    // Primary key field numbers per table (registered via RegisterPrimaryKey)
    private static readonly Dictionary<int, int[]> _primaryKeys = new();

    // Field name -> field number mapping per table (registered via RegisterFieldName)
    private static readonly Dictionary<int, Dictionary<string, int>> _fieldNames = new();

    public MockRecordHandle(int tableId)
    {
        _tableId = tableId;
        if (!_tables.ContainsKey(tableId))
            _tables[tableId] = new List<Dictionary<int, NavValue>>();
    }

    /// <summary>Reset all tables between test runs.</summary>
    public static void ResetAll()
    {
        _tables.Clear();
        // Keep PK and field name registrations — they are structural, not data
    }

    /// <summary>
    /// Register the primary key field numbers for a table.
    /// Call this before inserting/getting records for tables with composite PKs.
    /// </summary>
    public static void RegisterPrimaryKey(int tableId, params int[] fieldNos)
    {
        _primaryKeys[tableId] = fieldNos;
    }

    /// <summary>
    /// Register a field name -> field number mapping for a table.
    /// Enables ALFieldNo(fieldName) lookups.
    /// </summary>
    public static void RegisterFieldName(int tableId, string fieldName, int fieldNo)
    {
        if (!_fieldNames.TryGetValue(tableId, out var dict))
        {
            dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _fieldNames[tableId] = dict;
        }
        dict[fieldName] = fieldNo;
    }

    /// <summary>Get the PK field numbers for this table. Falls back to [1] if not registered.</summary>
    private int[] GetPrimaryKeyFields()
    {
        if (_primaryKeys.TryGetValue(_tableId, out var pk))
            return pk;
        return new[] { 1 };
    }

    public void ALInit()
    {
        _fields = new Dictionary<int, NavValue>();
        // Apply any InitValue attributes declared on the table's fields.
        // Registry is populated at pipeline start from the AL source.
        TableInitValueRegistry.ApplyInitValues(_tableId, _fields);
    }

    /// <summary>
    /// ALRecordId property — returns a NavRecordId representing this record's identity.
    /// In standalone mode, returns NavRecordId.Default since we don't have real table metadata.
    /// </summary>
    public NavRecordId ALRecordId => NavRecordId.Default;

    /// <summary>
    /// Page extension global variable stubs — page extensions use GetGlobalVariable/SetGlobalVariable
    /// to read/write page-level boolean flags (e.g., PriceEditable, ProfitEditable).
    /// In standalone mode, return defaults.
    /// </summary>
    public NavValue GetGlobalVariable(int id, NavType type) => DefaultForType(type);
    public void SetGlobalVariable(int id, NavType type, object value) { }

    // Cache global array variables so mutations persist across calls.
    // Generated page/page-extension code reads the same array multiple times;
    // without caching each call returns a fresh instance, discarding prior writes.
    private readonly Dictionary<(int id, NavType type), object> _globalArrayCache = new();

    public object GetGlobalArrayVariable(int id, NavType type)
    {
        if (_globalArrayCache.TryGetValue((id, type), out var cached))
            return cached;

        object array = type switch
        {
            NavType.Code => new MockArray<NavCode>(new NavCode(20, ""), 8),
            NavType.Text => new MockArray<NavText>(NavText.Default(0), 8),
            NavType.Integer => new MockArray<NavInteger>(NavInteger.Default, 8),
            NavType.Decimal => new MockArray<NavDecimal>(NavDecimal.Default, 8),
            NavType.Boolean => new MockArray<NavBoolean>(NavBoolean.Default, 8),
            _ => throw new NotSupportedException(
                $"GetGlobalArrayVariable: unsupported element type {type}. " +
                "Add a case for this NavType if the generated code requires it."),
        };
        _globalArrayCache[(id, type)] = array;
        return array;
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

    /// <summary>
    /// Extension-scoped SetFieldValueSafe — called when code accesses a field defined
    /// in a table extension. The BC compiler emits (extensionId, fieldId, type, value).
    /// We ignore the extensionId and delegate to the standard overload.
    /// </summary>
    public void SetFieldValueSafe(int extensionId, int fieldNo, NavType expectedType, NavValue value)
    {
        SetFieldValueSafe(fieldNo, expectedType, value);
    }

    /// <summary>
    /// Extension-scoped SetFieldValueSafe with validate flag.
    /// Called as (extensionId, fieldId, type, value, validate).
    /// </summary>
    public void SetFieldValueSafe(int extensionId, int fieldNo, NavType expectedType, NavValue value, bool validate)
    {
        SetFieldValueSafe(fieldNo, expectedType, value, validate);
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
        // For BLOB fields, auto-generate and persist a MockBlob instance
        // so that repeated reads return the same instance (writes persist).
        if (expectedType == NavType.BLOB)
        {
            var blob = new MockBlob();
            _fields[fieldNo] = blob;
            return blob;
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

    /// <summary>
    /// Extension-scoped GetFieldValueSafe — called when code reads a field defined
    /// in a table extension. The BC compiler emits (extensionId, fieldId, type).
    /// We ignore the extensionId and delegate to the standard overload.
    /// </summary>
    public NavValue GetFieldValueSafe(int extensionId, int fieldNo, NavType expectedType)
    {
        return GetFieldValueSafe(fieldNo, expectedType);
    }

    /// <summary>
    /// Extension-scoped GetFieldValueSafe with locale flag.
    /// Called as (extensionId, fieldId, type, useLocale).
    /// </summary>
    public NavValue GetFieldValueSafe(int extensionId, int fieldNo, NavType expectedType, bool useLocale)
    {
        return GetFieldValueSafe(fieldNo, expectedType);
    }

    /// <summary>
    /// Extension-scoped GetFieldRefSafe.
    /// Called as (extensionId, fieldId, type).
    /// </summary>
    public NavValue GetFieldRefSafe(int extensionId, int fieldNo, NavType expectedType)
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
        _viewText = string.Empty;
    }

    /// <summary>
    /// Expose the in-memory table store for RecordRef-level access — the
    /// MockRecordRef stub reads this when it's been Open()'d against a
    /// table ID, so RecRef.IsEmpty / FindSet / Next work against the
    /// same row set normal `Record X` handles operate on.
    /// </summary>
    public static bool TableHasAnyRow(int tableId)
    {
        return _tables.TryGetValue(tableId, out var rows) && rows.Count > 0;
    }

    public static int TableRowCount(int tableId)
    {
        return _tables.TryGetValue(tableId, out var rows) ? rows.Count : 0;
    }

    public bool ALInsert(DataError errorLevel) => ALInsert(errorLevel, false);

    public bool ALInsert(DataError errorLevel, bool runTrigger)
    {
        var table = _tables[_tableId];

        // Fire OnBeforeInsertEvent(var Rec, RunTrigger: Boolean)
        // — always fires, regardless of runTrigger
        FireImplicitDbEvent("OnBeforeInsertEvent", this, runTrigger);

        // Run the AL `trigger OnInsert()` body if requested.
        if (runTrigger)
            TryFireRecordTrigger("OnInsert");

        // Enforce primary-key uniqueness.
        var pkFields = GetPrimaryKeyFields();
        foreach (var existing in table)
        {
            if (RowMatchesPrimaryKey(existing, _fields, pkFields))
            {
                if (errorLevel == DataError.ThrowError)
                    throw new Exception(
                        $"The {TableName()} already exists. Identification fields and values: " +
                        string.Join(", ", pkFields.Select(f =>
                            $"{f}='{(_fields.TryGetValue(f, out var v) ? NavValueToString(v) : "")}'")));
                return false;
            }
        }

        var row = new Dictionary<int, NavValue>(_fields);

        // Auto-populate SystemId if not already set
        if (!row.ContainsKey(SystemIdFieldNo) || (row[SystemIdFieldNo] is NavGuid g && g.ToGuid() == Guid.Empty))
            row[SystemIdFieldNo] = new NavGuid(Guid.NewGuid());

        table.Add(row);

        // Copy the SystemId back into the handle's field bag
        _fields[SystemIdFieldNo] = row[SystemIdFieldNo];

        // Fire OnAfterInsertEvent(var Rec, RunTrigger: Boolean)
        FireImplicitDbEvent("OnAfterInsertEvent", this, runTrigger);

        return true;
    }

    /// <summary>
    /// Invoke a <c>trigger On{Name}()</c> body on the generated Record
    /// class if one exists, passing this MockRecordHandle through so
    /// the trigger's <c>Rec.SetFieldValueSafe(…)</c> calls mutate this
    /// instance's field bag in place.
    /// </summary>
    private void TryFireRecordTrigger(string triggerName)
    {
        var assembly = MockCodeunitHandle.CurrentAssembly;
        if (assembly == null) return;

        var recordTypeName = $"Record{_tableId}";
        Type? recordType = null;
        foreach (var t in assembly.GetTypes())
        {
            if (t.Name == recordTypeName) { recordType = t; break; }
        }
        if (recordType == null) return;

        // Generated Record classes are plain classes (post-rewrite) with
        // a `public MockRecordHandle Rec { get; } = new MockRecordHandle(N)`
        // auto-property. The trigger body does `this.Rec.SetFieldValueSafe`
        // etc. — we create an uninitialized instance and overwrite the
        // compiler-generated `<Rec>k__BackingField` so the trigger sees
        // THIS MockRecordHandle.
        object instance;
        try { instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(recordType); }
        catch { return; }

        var recBackingField = recordType.GetField("<Rec>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (recBackingField != null && recBackingField.FieldType.IsInstanceOfType(this))
        {
            recBackingField.SetValue(instance, this);
        }
        // Also wire xRec to this handle so `xRec.<something>` reads still
        // round-trip — in real BC xRec is the previous row, but for
        // trigger purposes most code only uses Rec.
        var xRecBackingField = recordType.GetField("<xRec>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (xRecBackingField != null && xRecBackingField.FieldType.IsInstanceOfType(this))
        {
            xRecBackingField.SetValue(instance, this);
        }

        var method = recordType.GetMethod(triggerName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance);
        if (method == null) return;

        try
        {
            method.Invoke(instance, null);
        }
        catch (System.Reflection.TargetInvocationException tie)
        {
            if (tie.InnerException != null) throw tie.InnerException;
            throw;
        }
    }

    private string TableName() => $"Record Table {_tableId}";

    /// <summary>
    /// Fire an implicit DB trigger event (OnBefore/AfterInsertEvent, etc.)
    /// by dispatching through AlCompat.FireEvent with ObjectType.Table.
    /// Subscribers registered via <c>[EventSubscriber(ObjectType::Table, ...)]</c>
    /// will be invoked. These events always fire regardless of runTrigger —
    /// runTrigger only controls AL trigger bodies.
    /// </summary>
    private void FireImplicitDbEvent(string eventName, params object?[] eventArgs)
    {
        AlCompat.FireEvent(EventSubscriberRegistry.ObjectTypeTable, _tableId, eventName, eventArgs);
    }

    /// <summary>
    /// Create a snapshot of the current field bag (for xRec) before a mutation.
    /// Returns a new MockRecordHandle sharing the same table store but with
    /// a copy of the current field values.
    /// </summary>
    private MockRecordHandle SnapshotForXRec()
    {
        var xRec = new MockRecordHandle(_tableId);
        foreach (var kv in _fields)
            xRec._fields[kv.Key] = kv.Value;
        return xRec;
    }

    public bool ALModify(DataError errorLevel) => ALModify(errorLevel, false);

    public bool ALModify(DataError errorLevel, bool runTrigger)
    {
        var table = _tables[_tableId];
        var pkFields = GetPrimaryKeyFields();

        // Capture xRec (snapshot before mutation) for event subscribers
        var xRec = SnapshotForXRec();

        // Fire OnBeforeModifyEvent(var Rec, var xRec, RunTrigger: Boolean)
        FireImplicitDbEvent("OnBeforeModifyEvent", this, xRec, runTrigger);

        // Run the AL `trigger OnModify()` body if requested
        if (runTrigger)
            TryFireRecordTrigger("OnModify");

        for (int i = 0; i < table.Count; i++)
        {
            if (RowMatchesPrimaryKey(table[i], _fields, pkFields))
            {
                table[i] = new Dictionary<int, NavValue>(_fields);

                // Fire OnAfterModifyEvent(var Rec, var xRec, RunTrigger: Boolean)
                FireImplicitDbEvent("OnAfterModifyEvent", this, xRec, runTrigger);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
            throw new Exception($"Record not found for Modify in table {_tableId}");
        return false;
    }

    /// <summary>
    /// ModifyAll — sets a single field to a value on all records matching current filters.
    /// </summary>
    public void ALModifyAllSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        ALModifyAllSafe(fieldNo, expectedType, value, false);
    }

    public void ALModifyAllSafe(int fieldNo, NavType expectedType, NavValue value, bool runTrigger)
    {
        if (!_tables.TryGetValue(_tableId, out var table))
            return;
        var filtered = GetFilteredRecords();
        var pkFields = GetPrimaryKeyFields();
        foreach (var filteredRow in filtered)
        {
            // Find the matching row in the actual table and update it
            for (int i = 0; i < table.Count; i++)
            {
                if (RowMatchesPrimaryKey(table[i], filteredRow, pkFields))
                {
                    table[i][fieldNo] = value;
                    break;
                }
            }
        }
    }

    public bool ALGet(DataError errorLevel, params NavValue[] keyValues)
    {
        var table = _tables[_tableId];
        var pkFields = GetPrimaryKeyFields();
        foreach (var row in table)
        {
            bool match = true;
            for (int i = 0; i < keyValues.Length && i < pkFields.Length; i++)
            {
                var fieldNo = pkFields[i];
                var rowVal = row.TryGetValue(fieldNo, out var rv) ? NavValueToString(rv) : "";
                var keyVal = NavValueToString(keyValues[i]);
                if (!PkValuesEqual(rowVal, keyVal)) { match = false; break; }
            }
            if (match)
            {
                _fields = new Dictionary<int, NavValue>(row);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
        {
            var keyStr = string.Join(", ", keyValues.Select(v => NavValueToString(v)));
            throw new Exception($"Record not found in table {_tableId} for key ({keyStr})");
        }
        return false;
    }

    public bool ALGetBySystemId(DataError errorLevel, Guid systemId)
    {
        if (!_tables.TryGetValue(_tableId, out var table))
            return false;

        foreach (var row in table)
        {
            if (row.TryGetValue(SystemIdFieldNo, out var value) &&
                value is NavGuid navGuid &&
                navGuid.ToGuid() == systemId)
            {
                _fields = new Dictionary<int, NavValue>(row);
                return true;
            }
        }

        if (errorLevel == DataError.ThrowError)
            throw new Exception($"Record not found in table {_tableId} for SystemId '{systemId}'");
        return false;
    }

    public bool ALGetBySystemId(Guid systemId) => ALGetBySystemId(DataError.ThrowError, systemId);

    /// <summary>
    /// Compare two stringified primary-key values with cross-type tolerance.
    /// The string forms already match 99% of the time, but Guid values can
    /// arrive in multiple serialisations (with/without braces, upper/lower
    /// case) when they've round-tripped through a Text[N] field. Fall back
    /// to parsing both sides as Guid / decimal / int and comparing structurally
    /// so the common "Guid -&gt; Text[100] -&gt; Get" pattern works.
    /// </summary>
    private static bool PkValuesEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb))
            return ga == gb;
        if (decimal.TryParse(a, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var da) &&
            decimal.TryParse(b, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var db))
            return da == db;
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
        var pkFields = GetPrimaryKeyFields();

        // Fire OnBeforeDeleteEvent(var Rec, RunTrigger: Boolean)
        FireImplicitDbEvent("OnBeforeDeleteEvent", this, runTrigger);

        // Run the AL `trigger OnDelete()` body if requested
        if (runTrigger)
            TryFireRecordTrigger("OnDelete");

        for (int i = 0; i < table.Count; i++)
        {
            if (RowMatchesPrimaryKey(table[i], _fields, pkFields))
            {
                table.RemoveAt(i);

                // Fire OnAfterDeleteEvent(var Rec, RunTrigger: Boolean)
                FireImplicitDbEvent("OnAfterDeleteEvent", this, runTrigger);
                return true;
            }
        }
        if (errorLevel == DataError.ThrowError)
            throw new Exception(
                $"The {TableName()} does not exist. Identification fields and values: " +
                string.Join(", ", pkFields.Select(f =>
                    $"{f}='{(_fields.TryGetValue(f, out var v) ? NavValueToString(v) : "")}'")));
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
        get => 0;
        set { /* No-op: filter groups not implemented in standalone mode */ }
    }

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
        _viewText = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Count / IsEmpty — respect active filters
    // -----------------------------------------------------------------------

    public int ALCount => GetFilteredRecords().Count;

    public bool ALIsEmpty => GetFilteredRecords().Count == 0;

    /// <summary>Number of fields that have been set on this record handle.</summary>
    public int FieldCount => _fields.Count;

    /// <summary>Whether any filters are currently active on this record handle.</summary>
    public bool FiltersActive => _filters.Count > 0;

    /// <summary>Whether a field with the given number has been set on any record in this table.</summary>
    public bool HasField(int fieldNo)
    {
        if (_fields.ContainsKey(fieldNo)) return true;
        if (_tables.TryGetValue(_tableId, out var rows))
            return rows.Any(r => r.ContainsKey(fieldNo));
        return false;
    }

    /// <summary>
    /// Returns the field numbers that have been set on the current record, sorted ascending.
    /// Used by MockRecordRef.ALFieldIndex to return the nth field.
    /// </summary>
    internal IReadOnlyList<int> GetFieldNumbers() => _fields.Keys.OrderBy(k => k).ToList();

    public int ALFieldNo(string fieldName)
    {
        if (_fieldNames.TryGetValue(_tableId, out var dict) &&
            dict.TryGetValue(fieldName, out var fieldNo))
            return fieldNo;
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

    public bool ALReadPermission => true;

    // -----------------------------------------------------------------------
    // Validate — sets field value (triggers are not implemented)
    // -----------------------------------------------------------------------

    /// <summary>
    /// AL's VALIDATE(FieldNo, Value) — sets the field and fires OnValidate trigger.
    /// Also fires implicit OnBefore/AfterValidateEvent for event subscribers.
    /// </summary>
    public void ALValidateSafe(int fieldNo, NavType expectedType, NavValue value)
    {
        var xRec = SnapshotForXRec();
        FireImplicitDbEvent("OnBeforeValidateEvent", this, xRec, fieldNo);
        _fields[fieldNo] = value;
        FireOnValidate(fieldNo);
        FireImplicitDbEvent("OnAfterValidateEvent", this, xRec, fieldNo);
    }

    /// <summary>
    /// AL's VALIDATE(FieldNo) — re-validates the current field value (no new value provided).
    /// Fires OnValidate trigger with the existing value.
    /// </summary>
    public void ALValidateSafe(int fieldNo, NavType expectedType)
    {
        var xRec = SnapshotForXRec();
        FireImplicitDbEvent("OnBeforeValidateEvent", this, xRec, fieldNo);
        FireOnValidate(fieldNo);
        FireImplicitDbEvent("OnAfterValidateEvent", this, xRec, fieldNo);
    }

    /// <summary>
    /// ALValidate overload matching transpiler output pattern: ALValidate(DataError, fieldNo, NavType, value)
    /// </summary>
    public void ALValidate(DataError errorLevel, int fieldNo, NavType expectedType, NavValue value)
    {
        var xRec = SnapshotForXRec();
        FireImplicitDbEvent("OnBeforeValidateEvent", this, xRec, fieldNo);
        _fields[fieldNo] = value;
        FireOnValidate(fieldNo);
        FireImplicitDbEvent("OnAfterValidateEvent", this, xRec, fieldNo);
    }

    /// <summary>
    /// Fire the OnValidate trigger for a field. Looks up the generated Record type via reflection,
    /// finds the method with [FieldTriggerHandler(FieldTriggerType.OnValidate, fieldNo)], and invokes it.
    /// The record instance's Rec property is wired to this MockRecordHandle so field reads/writes
    /// during the trigger operate on the correct data.
    /// </summary>
    private void FireOnValidate(int fieldNo)
    {
        var assembly = MockCodeunitHandle.CurrentAssembly;
        if (assembly == null) return;

        // Search both the Record class and any TableExtension classes for the trigger
        var recordTypeName = $"Record{_tableId}";
        var candidateTypes = assembly.GetTypes()
            .Where(t => t.Name == recordTypeName || t.Name.StartsWith("TableExtension"))
            .ToList();

        foreach (var candidateType in candidateTypes)
        {
            if (TryFireOnValidateInType(candidateType, fieldNo))
                return;
        }
    }

    private bool TryFireOnValidateInType(Type type, int fieldNo)
    {
        // Find the method with [FieldTriggerHandler(FieldTriggerType.OnValidate, fieldNo)]
        foreach (var method in type.GetMethods(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance))
        {
            var attr = method.GetCustomAttributes(false)
                .FirstOrDefault(a => a.GetType().Name == "FieldTriggerHandlerAttribute");
            if (attr == null) continue;

            var triggerType = attr.GetType().GetProperty("TriggerType")?.GetValue(attr);
            var triggerFieldNo = attr.GetType().GetProperty("FieldNo")?.GetValue(attr);

            // FieldTriggerType.OnValidate == 0
            if (triggerType?.ToString() == "OnValidate" && triggerFieldNo is int fno && fno == fieldNo)
            {
                // Create instance and wire Rec to this MockRecordHandle
                var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
                var backingField = type.GetField("<Rec>k__BackingField",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                backingField?.SetValue(instance, this);

                try
                {
                    method.Invoke(instance, null);
                }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    if (tie.InnerException != null) throw tie.InnerException;
                    throw;
                }
                return true;
            }
        }
        return false;
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

    /// <summary>Overload: TestField with bool value (transpiler pattern for Boolean fields).</summary>
    public void ALTestFieldSafe(int fieldNo, NavType expectedType, bool expectedValue)
    {
        var actual = GetFieldValueSafe(fieldNo, expectedType);
        var actualStr = NavValueToString(actual);
        var expectedStr = expectedValue.ToString();
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
    /// AL's CALCFIELDS — calculates FlowFields by consulting the
    /// transpile-time <see cref="CalcFormulaRegistry"/> and evaluating
    /// <c>exist(...)</c> formulas against the in-memory tables.
    /// Other formula kinds (count/sum/lookup/...) remain no-ops.
    /// </summary>
    public void ALCalcFields(DataError errorLevel, params int[] fieldNos)
    {
        foreach (var fieldNo in fieldNos)
        {
            var formula = CalcFormulaRegistry.Find(_tableId, fieldNo);
            if (formula is null) continue;

            if (formula.Kind == CalcFormulaRegistry.FormulaKind.Exist)
            {
                bool exists = EvaluateExistFormula(formula);
                _fields[fieldNo] = NavBoolean.Create(exists);
            }
        }
    }

    private bool EvaluateExistFormula(CalcFormulaRegistry.Formula formula)
    {
        var targetId = CalcFormulaRegistry.GetTableIdByName(formula.TargetTableName);
        if (targetId is null || !_tables.TryGetValue(targetId.Value, out var rows)) return false;

        // Field name -> field id lookups. Prefer the transpile-time
        // TableFieldRegistry (populated from AL source at pipeline start)
        // since runtime RegisterFieldName is only called for tables whose
        // generated code actually references ALFieldNo(name) somewhere.
        int? ResolveField(int tableId, string fieldName)
        {
            var fromRegistry = TableFieldRegistry.GetFieldId(tableId, fieldName);
            if (fromRegistry.HasValue) return fromRegistry;
            if (_fieldNames.TryGetValue(tableId, out var dict) &&
                dict.TryGetValue(fieldName, out var id))
                return id;
            return null;
        }

        foreach (var row in rows)
        {
            bool allMatch = true;
            foreach (var clause in formula.Conditions)
            {
                var childFieldId = ResolveField(targetId.Value, clause.ChildField);
                if (childFieldId is null) { allMatch = false; break; }

                if (!row.TryGetValue(childFieldId.Value, out var childValue))
                {
                    allMatch = false;
                    break;
                }

                string childStr = NavValueToString(childValue);
                string expected;
                if (clause.OpKind == "field")
                {
                    var selfFieldId = ResolveField(_tableId, clause.Value);
                    if (selfFieldId is null) { allMatch = false; break; }
                    if (!_fields.TryGetValue(selfFieldId.Value, out var selfValue))
                    {
                        allMatch = false;
                        break;
                    }
                    expected = NavValueToString(selfValue);
                }
                else if (clause.OpKind == "const")
                {
                    expected = clause.Value;
                }
                else
                {
                    // filter(...) not supported yet — treat as non-matching.
                    allMatch = false;
                    break;
                }

                if (!string.Equals(childStr, expected, StringComparison.OrdinalIgnoreCase))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) return true;
        }
        return false;
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

    /// <summary>Overload with DataError level (transpiler pattern).</summary>
    public void ALSetLoadFields(DataError errorLevel, params int[] fieldNos)
    {
        // No-op: all fields always loaded in in-memory store
    }

    public string ALGetView() => _viewText;

    public void ALSetView(string view)
    {
        _viewText = view ?? string.Empty;
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

    public void ClearFieldValue(int fieldNo)
    {
        _fields.Remove(fieldNo);
    }

    public void ClearFieldValue(int extensionId, int fieldNo)
    {
        ClearFieldValue(fieldNo);
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
        var table = _tables[_tableId];
        var pkFields = GetPrimaryKeyFields();

        // 1. Find the current record in the table by its current PK values
        int sourceIndex = -1;
        for (int i = 0; i < table.Count; i++)
        {
            if (RowMatchesPrimaryKey(table[i], _fields, pkFields))
            {
                sourceIndex = i;
                break;
            }
        }
        if (sourceIndex < 0)
        {
            if (errorLevel == DataError.ThrowError)
                throw new Exception(
                    $"The {TableName()} does not exist. Identification fields and values: " +
                    string.Join(", ", pkFields.Select(f =>
                        $"{f}='{(_fields.TryGetValue(f, out var v) ? NavValueToString(v) : "")}'")));
            return false;
        }

        // 2. Build a temporary field bag with the new key values to check for conflicts
        var newKeyBag = new Dictionary<int, NavValue>(_fields);
        for (int i = 0; i < newKeyValues.Length && i < pkFields.Length; i++)
            newKeyBag[pkFields[i]] = newKeyValues[i];

        for (int i = 0; i < table.Count; i++)
        {
            if (i == sourceIndex) continue;
            if (RowMatchesPrimaryKey(table[i], newKeyBag, pkFields))
            {
                if (errorLevel == DataError.ThrowError)
                    throw new Exception(
                        $"The {TableName()} already exists. Identification fields and values: " +
                        string.Join(", ", pkFields.Select(f =>
                            $"{f}='{(newKeyBag.TryGetValue(f, out var v) ? NavValueToString(v) : "")}'")));
                return false;
            }
        }

        // 3. Update the PK fields in the table row and in the handle's field bag
        for (int i = 0; i < newKeyValues.Length && i < pkFields.Length; i++)
        {
            table[sourceIndex][pkFields[i]] = newKeyValues[i];
            _fields[pkFields[i]] = newKeyValues[i];
        }

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
    /// Returns the subset of table rows matching all active filters,
    /// sorted by the current key fields and ascending/descending direction.
    /// When no filters are set, returns all rows. When no sort key is set,
    /// returns in insertion order.
    /// </summary>
    private List<Dictionary<int, NavValue>> GetFilteredRecords()
    {
        var table = _tables[_tableId];
        List<Dictionary<int, NavValue>> result;

        if (_filters.Count == 0)
            result = new List<Dictionary<int, NavValue>>(table);
        else
        {
            result = new List<Dictionary<int, NavValue>>();
            foreach (var row in table)
            {
                if (RowMatchesAllFilters(row))
                    result.Add(row);
            }
        }

        // Apply sort ordering if a current key is set
        if (_currentKeyFields != null && _currentKeyFields.Length > 0)
        {
            result.Sort((a, b) =>
            {
                foreach (var fieldNo in _currentKeyFields)
                {
                    var aVal = a.TryGetValue(fieldNo, out var av) ? NavValueToString(av) : "";
                    var bVal = b.TryGetValue(fieldNo, out var bv) ? NavValueToString(bv) : "";
                    var cmp = StringCompareOp(aVal, bVal, false);
                    if (cmp != 0)
                    {
                        bool asc = !_ascending.TryGetValue(fieldNo, out var isAsc) || isAsc;
                        return asc ? cmp : -cmp;
                    }
                }
                return 0;
            });
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
    /// and-list (V1&amp;V2&amp;V3 — all must match), wildcards (*/?),
    /// case-insensitive (@), not-equal (&lt;&gt;), comparison operators
    /// (&gt;, &gt;=, &lt;, &lt;=). AND binds tighter than OR, matching BC's
    /// filter grammar.
    /// </summary>
    private static bool MatchesFilterExpression(NavValue? fieldValue, string fieldStr, string expression)
    {
        // BC filter precedence: | (OR) is top-level, & (AND) nests inside.
        // Split on | first, then each alternative on &.
        var alternatives = SplitOnOperator(expression, '|');

        foreach (var alt in alternatives)
        {
            var conjuncts = SplitOnOperator(alt, '&');
            bool allMatch = true;
            foreach (var conj in conjuncts)
            {
                if (!MatchesSingleExpression(fieldValue, fieldStr, conj.Trim()))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
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

        // Option-field filter normalisation: BC stores options as ordinals,
        // but AL filter literals often use member names ("Red" vs "0"). If
        // the field is a NavOption and the literal isn't numeric, resolve it
        // to an ordinal via EnumRegistry before string comparison.
        working = NormalizeOptionLiteral(fieldValue, working);

        // Check for not-equal: <>VALUE
        if (working.StartsWith("<>"))
        {
            var notVal = working.Substring(2);
            notVal = NormalizeOptionLiteral(fieldValue, notVal);
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

    /// <summary>
    /// If the filter's target field value is a NavOption and the literal
    /// string isn't a number, try to resolve it to an enum ordinal via
    /// <see cref="EnumRegistry"/>. Falls back to returning the literal
    /// unchanged so callers can still do plain-text comparisons.
    /// </summary>
    private static string NormalizeOptionLiteral(NavValue? fieldValue, string literal)
    {
        if (fieldValue is not NavOption) return literal;
        if (string.IsNullOrEmpty(literal)) return literal;
        if (int.TryParse(literal, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return literal;
        var stripped = literal.Trim('"', '\'');
        var ordinal = EnumRegistry.FindOrdinalByMemberName(stripped);
        if (ordinal.HasValue)
            return ordinal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return literal;
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
    /// Split a filter expression on a single-character operator (| or &amp;),
    /// respecting single-quoted string literals so separators inside
    /// quotes stay literal.
    /// </summary>
    private static List<string> SplitOnOperator(string expression, char op)
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
            else if (expression[i] == op && !inQuotes)
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
    // Cached PropertyInfo for slow reflection paths — reading these on
    // every NavValueToString call was adding measurable cost in filter-heavy
    // loops (issue #23). They're resolved once at type-init and reused.
    private static readonly System.Reflection.PropertyInfo? _navDecimalValueProp =
        typeof(NavDecimal).GetProperty("Value");

    /// <summary>
    /// Produce a canonical string form of a <see cref="NavValue"/> for
    /// filter comparison and PK hashing. **Do not fall back to
    /// <c>value.ToString()</c>**: BC's NavValue.ToString traps into
    /// <c>NavFormatEvaluateHelper</c> which triggers a Roslyn
    /// <c>Microsoft.CodeAnalysis</c> JIT load on first use — ~200 ms
    /// sitting inside a single filter-matching call. Every supported
    /// AL value type must have an explicit branch here. Unknown types
    /// return empty string so the caller still gets a comparable value
    /// without reaching the slow path.
    /// </summary>
    private static string NavValueToString(NavValue value)
    {
        switch (value)
        {
            case NavText nt: return (string)nt;
            case NavCode nc: return ((string)nc).TrimEnd();  // NavCode may be space-padded to maxLength
            case NavInteger ni: return ((int)ni).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case NavBoolean nb: return ((bool)nb).ToString();
            case NavBigInteger nbi: return ((long)nbi).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case NavGuid ng: return ((Guid)ng).ToString();
            case NavDate nd2: try { return ((DateTime)nd2).ToString("o", System.Globalization.CultureInfo.InvariantCulture); } catch { return ""; }
            case NavDateTime ndt: try { return ((DateTime)ndt).ToString("o", System.Globalization.CultureInfo.InvariantCulture); } catch { return ""; }
            case NavOption nopt: return nopt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // NavDecimal: use the cached PropertyInfo instead of reflecting per call.
        if (value is NavDecimal nd)
        {
            try
            {
                var raw = _navDecimalValueProp?.GetValue(nd);
                if (raw != null)
                    return Convert.ToDecimal(raw).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return "";
        }

        // Unknown type — return empty string rather than calling
        // value.ToString(), which triggers BC's formatter and blows
        // through Roslyn compilation on first use.
        return "";
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

    /// <summary>
    /// Check if two rows match on all primary key fields.
    /// </summary>
    private static bool RowMatchesPrimaryKey(Dictionary<int, NavValue> row, Dictionary<int, NavValue> current, int[] pkFields)
    {
        foreach (var fieldNo in pkFields)
        {
            var rowVal = row.TryGetValue(fieldNo, out var rv) ? NavValueToString(rv) : "";
            var curVal = current.TryGetValue(fieldNo, out var cv) ? NavValueToString(cv) : "";
            if (!PkValuesEqual(rowVal, curVal)) return false;
        }
        return true;
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
            NavType.Option => CreateDefaultNavOption(),
            NavType.BigInteger => NavBigInteger.Default,
            NavType.Date => NavDate.Default,
            NavType.Time => NavTime.Default,
            NavType.DateTime => NavDateTime.Default,
            NavType.DateFormula => NavDateFormula.Default,
            NavType.GUID => new NavGuid(Guid.Empty),
            NavType.Duration => NavDuration.Default,
            NavType.Media => NavMedia.Default,
            NavType.MediaSet => NavMediaSet.Default,
            _ => NavText.Default(0)
        };
    }

    /// <summary>
    /// Build a NavOption carrying the given ordinal. Exposed for
    /// TableInitValueRegistry which needs to turn AL enum member literals
    /// into field-bag values matching the NavOption storage shape.
    /// </summary>
    public static NavOption CreateOptionValue(int ordinal)
    {
        var defaultOpt = CreateDefaultNavOption();
        try
        {
            var createInstance = defaultOpt.GetType().GetMethod("CreateInstance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (createInstance != null)
            {
                var result = createInstance.Invoke(defaultOpt, new object[] { ordinal });
                if (result is NavOption opt) return opt;
            }
        }
        catch { }
        return defaultOpt;
    }

    /// <summary>
    /// Creates a default NavOption(0) with valid metadata so that CreateInstance() works.
    /// NavOption requires NCLOptionMetadata in its constructor, which we create with a
    /// generic option string. This enables the common pattern:
    ///   ((NavOption)record.GetFieldValueSafe(fieldNo, NavType.Option)).CreateInstance(value)
    /// </summary>
    private static NavOption? _cachedDefaultOption;
    private static NavOption CreateDefaultNavOption()
    {
        if (_cachedDefaultOption != null)
            return _cachedDefaultOption;

        try
        {
            // NavOption has internal ctor: NavOption(NCLOptionMetadata metadata, int value)
            var navOptionType = typeof(NavOption);
            var ctor = navOptionType.GetConstructors(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 2);
            if (ctor != null)
            {
                var metadataType = ctor.GetParameters()[0].ParameterType;
                var metaCtor = metadataType.GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                if (metaCtor != null)
                {
                    // Create generic option metadata with 20 option values
                    var meta = metaCtor.Invoke(new object[] { " ,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19" });
                    _cachedDefaultOption = (NavOption)ctor.Invoke(new object[] { meta, 0 });
                    return _cachedDefaultOption;
                }
            }
        }
        catch { }

        // Fallback: uninitialized object (CreateInstance will fail but field storage works)
        _cachedDefaultOption = (NavOption)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(NavOption));
        return _cachedDefaultOption;
    }

    /// <summary>
    /// AL's TRANSFERFIELDS — copies field values from another record.
    /// In standalone mode, copies all fields from source to this record.
    /// </summary>
    public void ALTransferFields(MockRecordHandle source, bool initPrimaryKey = true)
    {
        var pkFields = new HashSet<int>(GetPrimaryKeyFields());
        foreach (var kv in source._fields)
        {
            if (!initPrimaryKey && pkFields.Contains(kv.Key)) continue;
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
        get => false;
        set { /* No-op: record marking not implemented in standalone mode */ }
    }

    /// <summary>
    /// AL SetAutoCalcFields — stub, no-op in standalone mode.
    /// In BC, this configures automatic calculation of FlowFields.
    /// </summary>
    public void ALSetAutoCalcFields(params object[] fields)
    {
        // No-op: FlowFields not supported in standalone mode
    }

    /// <summary>
    /// AL GetFilter() — returns all active filters as a combined string.
    /// Equivalent to the no-arg overload in BC.
    /// </summary>
    public string ALGetFilter()
    {
        return ALGetFilters;
    }

    /// <summary>
    /// AL GetFilter(fieldNo) — returns the filter expression for a specific field.
    /// For range filters with from==to: returns the value string.
    /// For range filters with from!=to: returns "FROM..TO".
    /// For expression filters: returns the stored expression.
    /// Returns empty string when no filter is active on that field.
    /// </summary>
    public string ALGetFilter(int fieldNo)
    {
        if (!_filters.TryGetValue(fieldNo, out var filter))
            return "";
        return SerializeFilter(filter);
    }

    /// <summary>
    /// AL GetFilters — returns all active filters as a combined string.
    /// Format: "FieldName: Expression, FieldName2: Expression2"
    /// Fields are ordered by field number for deterministic output.
    /// </summary>
    public string ALGetFilters
    {
        get
        {
            if (_filters.Count == 0)
                return "";

            var parts = new List<string>();
            foreach (var kv in _filters.OrderBy(f => f.Key))
            {
                var fieldName = GetFieldNameByNo(kv.Key);
                var expr = SerializeFilter(kv.Value);
                parts.Add($"{fieldName}: {expr}");
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// AL HasFilter — returns true if any filters are currently active.
    /// </summary>
    public bool ALHasFilter => _filters.Count > 0;

    private string SerializeFilter(FieldFilter filter)
    {
        if (filter.IsRangeFilter)
        {
            var fromStr = NavValueToString(filter.FromValue!);
            var toStr = NavValueToString(filter.ToValue!);
            if (fromStr == toStr)
                return fromStr;
            return $"{fromStr}..{toStr}";
        }
        return filter.FilterExpression ?? "";
    }

    private string GetFieldNameByNo(int fieldNo)
    {
        if (_fieldNames.TryGetValue(_tableId, out var names))
        {
            foreach (var kv in names)
            {
                if (kv.Value == fieldNo)
                    return kv.Key;
            }
        }
        return $"Field{fieldNo}";
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
    /// AL CurrentKey — returns the name of the current sort key.
    /// When no key is explicitly set, returns the PK field names.
    /// </summary>
    public string ALCurrentKey
    {
        get
        {
            var keyFields = _currentKeyFields;
            if (keyFields == null || keyFields.Length == 0)
            {
                // Default to PK fields
                if (_primaryKeys.TryGetValue(_tableId, out var pk) && pk.Length > 0)
                    keyFields = pk;
                else
                    return "";
            }
            var names = new List<string>();
            foreach (var fieldNo in keyFields)
                names.Add(GetFieldNameByNo(fieldNo));
            return string.Join(",", names);
        }
    }

    /// <summary>
    /// AL Ascending — returns whether the current sort order is ascending.
    /// Defaults to true. Checks the first current key field's ascending state.
    /// </summary>
    public bool ALAscending
    {
        get
        {
            if (_currentKeyFields != null && _currentKeyFields.Length > 0)
            {
                if (_ascending.TryGetValue(_currentKeyFields[0], out var isAsc))
                    return isAsc;
            }
            return true;
        }
    }

    /// <summary>
    /// AL CountApprox — in BC returns an approximate count (faster than Count).
    /// In standalone mode, returns the same as Count.
    /// </summary>
    public int ALCountApprox => ALCount;

    /// <summary>
    /// AL Consistent — marks the record for transaction consistency checking.
    /// No-op in standalone mode (no transaction support).
    /// </summary>
    public void ALConsistent(bool consistent)
    {
        // No-op: transaction consistency not supported in standalone mode
    }

    /// <summary>
    /// AL FieldActive — returns whether a field is active (enabled) in the current record.
    /// In standalone mode, always returns true for any field.
    /// </summary>
    public bool ALFieldActive(int fieldNo)
    {
        return true;
    }

    // -----------------------------------------------------------------------
    // Record links — in-memory tracking
    // -----------------------------------------------------------------------
    private readonly List<string> _links = new();

    /// <summary>
    /// AL AddLink — adds a link (URL or note) to the record.
    /// </summary>
    public void ALAddLink(string link)
    {
        _links.Add(link);
    }

    /// <summary>
    /// AL AddLink — overload with description.
    /// </summary>
    public void ALAddLink(string link, string description)
    {
        _links.Add(link);
    }

    /// <summary>
    /// AL DeleteLink — deletes a specific link by index.
    /// </summary>
    public void ALDeleteLink(int linkId)
    {
        if (linkId > 0 && linkId <= _links.Count)
            _links.RemoveAt(linkId - 1);
    }

    /// <summary>
    /// AL DeleteLinks — deletes all links from the record.
    /// </summary>
    public void ALDeleteLinks()
    {
        _links.Clear();
    }

    /// <summary>
    /// AL HasLinks — returns true if the record has any links.
    /// </summary>
    public bool ALHasLinks => _links.Count > 0;

    /// <summary>
    /// AL WritePermission — checks if the user has write permission on the table.
    /// In standalone mode, always returns true (no permission enforcement).
    /// </summary>
    public bool ALWritePermission => true;

    /// <summary>
    /// AL SetPermissionFilter — applies permission-based filtering.
    /// No-op in standalone mode (no permission enforcement).
    /// </summary>
    public void ALSetPermissionFilter()
    {
        // No-op: permissions not supported in standalone mode
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

    /// <summary>
    /// Extension-scoped Invoke — called when invoking a method defined in a table
    /// extension. The BC compiler emits (extensionId, memberId, args).
    /// We ignore the extensionId and delegate to the standard Invoke.
    /// </summary>
    public object? Invoke(int extensionId, int memberId, object[] args)
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
        {
            throw new InvalidOperationException($"Record type {recordTypeName} not found in assembly for Invoke");
        }

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
                // Wire the Rec property to point to this MockRecordHandle instance.
                // The generated Record class has: public MockRecordHandle Rec { get; } = new MockRecordHandle(tableId);
                // Since GetUninitializedObject doesn't run initializers, Rec is null.
                // We use reflection to set the backing field so that the record method
                // operates on the same data as the calling MockRecordHandle.
                var recProp = recordType.GetProperty("Rec",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (recProp != null)
                {
                    // Auto-property backing field is named <Rec>k__BackingField
                    var backingField = recordType.GetField("<Rec>k__BackingField",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    backingField?.SetValue(instance, this);
                }
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
