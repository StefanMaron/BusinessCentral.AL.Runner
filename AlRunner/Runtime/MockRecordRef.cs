namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Mock for NavRecordRef — backed by a <see cref="MockRecordHandle"/> instance
/// so all record operations (insert, modify, find, iterate, filter, etc.)
/// delegate to the same in-memory table store that typed Record variables use.
/// Field-level access via <c>ALField(n)</c> returns a <see cref="MockFieldRef"/>
/// bound to this record ref's field bag.
/// </summary>
public class MockRecordRef
{
    public int Number { get; private set; }
    private MockRecordHandle? _handle;

    public MockRecordRef() { }

    public void Clear()
    {
        Number = 0;
        _handle = null;
    }

    // -- Open / Close --

    public void Open(int tableId) => Open(tableId, false, null);
    public void Open(int tableId, bool temporary) => Open(tableId, temporary, null);
    public void Open(int tableId, bool temporary, string? companyName)
    {
        Number = tableId;
        _handle = new MockRecordHandle(tableId, temporary);
    }

    public void Close()
    {
        Number = 0;
        _handle = null;
    }

    // -- AL-lowered Open variants (BC compiler emits CompilationTarget as first arg) --

    public void ALOpen(int tableId) => Open(tableId);
    public void ALOpen(int tableId, bool temporary) => Open(tableId, temporary);
    public void ALOpen(int tableId, bool temporary, string companyName) => Open(tableId, temporary, companyName);
    public void ALOpen(object compilationTarget, int tableId) => Open(tableId);
    public void ALOpen(object compilationTarget, int tableId, bool temporary) => Open(tableId, temporary);
    public void ALOpen(object compilationTarget, int tableId, bool temporary, string companyName) => Open(tableId, temporary, companyName);

    public void ALClose() => Close();

    // -- Number / TableNo --

    public int ALGetNumber() => Number;
    public int ALNumber => Number;

    // -- Name --

    /// <summary>
    /// ALName — returns the table name from metadata, or "TableN" fallback.
    /// Returns empty string when no table is open.
    /// </summary>
    public NavText ALName
    {
        get
        {
            if (Number == 0) return new NavText("");
            var name = TableFieldRegistry.GetTableName(Number);
            return new NavText(name ?? $"Table{Number}");
        }
    }

    // -- SetLoadFields / AddLoadFields / AreFieldsLoaded (no-op in standalone) --

    /// <summary>
    /// ALSetLoadFields — no-op in standalone mode. All fields are always in memory.
    /// BC compiler emits <c>recRef.ALSetLoadFields(params int[] fieldNos)</c>.
    /// </summary>
    public void ALSetLoadFields(params int[] fieldNos) { }
    public void ALSetLoadFields(DataError errorLevel, params int[] fieldNos) { }

    /// <summary>ALAddLoadFields — extends the load set. No-op in standalone (all fields loaded).</summary>
    public void ALAddLoadFields(params int[] fieldNos) { }
    public void ALAddLoadFields(DataError errorLevel, params int[] fieldNos) { }

    /// <summary>ALAreFieldsLoaded — standalone: all fields are always loaded, so always true.</summary>
    public bool ALAreFieldsLoaded(params int[] fieldNos) => true;
    public bool ALAreFieldsLoaded(DataError errorLevel, params int[] fieldNos) => true;

    /// <summary>ALLoadFields — deprecated alias for SetLoadFields. No-op in standalone.</summary>
    public void ALLoadFields(params int[] fieldNos) { }
    public void ALLoadFields(DataError errorLevel, params int[] fieldNos) { }

    // -- IsDirty — no dirty tracking in standalone --

    /// <summary>ALIsDirty — standalone has no write-pending tracking; always false.</summary>
    public bool ALIsDirty => false;

    // -- CopyLinks — no-op in standalone (no BC link service) --

    /// <summary>ALCopyLinks — copies record links. No-op in standalone mode.</summary>
    public void ALCopyLinks(MockRecordRef fromRef) { }
    public void ALCopyLinks(object fromRecord) { }

    // -- ReadConsistency — no SQL transactions in standalone --

    /// <summary>ALReadConsistency — standalone has no read-consistency isolation; always false.</summary>
    public bool ALReadConsistency => false;

    // -- SecurityFiltering — get/set stub --

    private SecurityFiltering _securityFiltering;
    /// <summary>ALSecurityFiltering — get/set the security filter mode. Stored but not enforced.</summary>
    public SecurityFiltering ALSecurityFiltering
    {
        get => _securityFiltering;
        set => _securityFiltering = value;
    }

    // -- Truncate — delete all rows (same as DeleteAll without triggers) --

    /// <summary>ALTruncate — removes all records from the table without running triggers.</summary>
    public void ALTruncate() => _handle?.ALDeleteAll(DataError.ThrowError, false);
    public void ALTruncate(DataError errorLevel) => _handle?.ALDeleteAll(errorLevel, false);

    // -- Caption --

    /// <summary>
    /// ALCaption — returns the table caption. Stub: delegates to handle's ALTableCaption
    /// or returns empty string when no table is open.
    /// BC compiler emits <c>recRef.ALCaption</c>.
    /// </summary>
    public NavText ALCaption => _handle != null ? new NavText(_handle.ALTableCaption) : new NavText("");

    // -- Field access --

    /// <summary>
    /// ALField — returns a MockFieldRef bound to the given field number on this record ref.
    /// BC compiler emits <c>recRef.ALField(fieldNo)</c> (after stripping the ITreeObject arg).
    /// </summary>
    public MockFieldRef ALField(int fieldNo)
    {
        var fref = new MockFieldRef();
        fref.Bind(this, fieldNo);
        return fref;
    }

    /// <summary>
    /// ALFieldIndex — returns a MockFieldRef for the nth field (1-based index).
    /// BC compiler emits <c>recRef.ALFieldIndex(n)</c>.
    /// Returns the nth field by field number order. If index is out of range,
    /// returns a stub MockFieldRef with field number 0.
    /// </summary>
    public MockFieldRef ALFieldIndex(int index)
    {
        if (_handle != null)
        {
            var fieldNos = _handle.GetFieldNumbers();
            if (index >= 1 && index <= fieldNos.Count)
            {
                var fref = new MockFieldRef();
                fref.Bind(this, fieldNos[index - 1]);
                return fref;
            }
        }
        // Out of range or no handle: return stub with field 0
        var stub = new MockFieldRef();
        stub.Bind(this, 0);
        return stub;
    }

    // -- Data operations (delegated to MockRecordHandle) --

    public bool IsEmpty() => _handle == null || _handle.ALIsEmpty;
    public bool ALIsEmpty => IsEmpty();

    public int Count() => _handle?.ALCount ?? 0;
    public int ALCount => Count();

    public bool FindFirst() => TryFind(() => MarkedOnlyFind(() => _handle?.ALFindFirst() ?? false));
    public bool ALFindFirst() => FindFirst();
    public bool ALFindFirst(DataError errorLevel) => _handle != null && MarkedOnlyFind(() => _handle.ALFindFirst(errorLevel));

    public bool FindLast() => TryFind(() => MarkedOnlyFindReverse(() => _handle?.ALFindLast() ?? false));
    public bool ALFindLast() => FindLast();
    public bool ALFindLast(DataError errorLevel) => _handle != null && MarkedOnlyFindReverse(() => _handle.ALFindLast(errorLevel));

    public bool FindSet() => TryFind(() => MarkedOnlyFind(() => _handle?.ALFindSet() ?? false));
    public bool ALFindSet() => FindSet();
    public bool ALFindSet(DataError errorLevel) => _handle != null && MarkedOnlyFind(() => _handle.ALFindSet(errorLevel));
    public bool ALFindSet(DataError errorLevel, bool forUpdate) => _handle != null && MarkedOnlyFind(() => _handle.ALFindSet(errorLevel, forUpdate));

    public bool Find(string which) => TryFind(() => MarkedOnlyFind(() => _handle?.ALFind(DataError.ThrowError, which) ?? false));
    public bool Find() => Find("=");
    public bool ALFind(string which) => Find(which);
    public bool ALFind(DataError errorLevel) => Find("=");
    public bool ALFind(DataError errorLevel, string which) => _handle != null && MarkedOnlyFind(() => _handle.ALFind(errorLevel, which));

    /// <summary>
    /// Try a find operation, catching exceptions for no-DataError callers.
    /// MockRecordHandle.ALFindFirst etc. throw when DataError.ThrowError is passed
    /// and no records exist, but RecordRef.FindFirst() (AL) returns false instead.
    /// </summary>
    private static bool TryFind(Func<bool> findAction)
    {
        try { return findAction(); }
        catch { return false; }
    }

    /// <summary>
    /// When MarkedOnly is true, advance forward from the initial find position
    /// until we land on a marked record (or run out of records).
    /// </summary>
    private bool MarkedOnlyFind(Func<bool> findAction)
    {
        if (!findAction()) return false;
        if (!_markedOnly) return true;
        // Already on a marked record?
        if (ALMark()) return true;
        // Advance until we find a marked one
        while (_handle != null && _handle.ALNext() != 0)
        {
            if (ALMark()) return true;
        }
        return false;
    }

    /// <summary>
    /// MarkedOnly-aware reverse find (for FindLast which should scan backwards).
    /// MockRecordHandle has no ALPrev, so we do a two-pass forward scan:
    /// Pass 1 — walk the entire filtered set from the first record and record the
    ///          index of the last marked record.
    /// Pass 2 — restart from the first record and advance to that saved index.
    /// </summary>
    private bool MarkedOnlyFindReverse(Func<bool> findAction)
    {
        if (!findAction()) return false;
        if (!_markedOnly) return true;
        if (_handle == null) return false;

        // If the reverse-find already landed on a marked record, we're done.
        if (ALMark()) return true;

        // Pass 1: scan the filtered set forward and remember the index of the
        // last marked record.
        if (!_handle.ALFindFirst()) return false;

        int currentIndex = 0;
        int lastMarkedIndex = ALMark() ? 0 : -1;

        while (_handle.ALNext() != 0)
        {
            currentIndex++;
            if (ALMark())
                lastMarkedIndex = currentIndex;
        }

        if (lastMarkedIndex < 0) return false;

        // Pass 2: reposition on the last marked record.
        if (!_handle.ALFindFirst()) return false;
        for (int i = 0; i < lastMarkedIndex; i++)
        {
            if (_handle.ALNext() == 0) return false;
        }

        return true;
    }

    public bool Next() => MarkedOnlyNext(() => _handle != null && _handle.ALNext() != 0);
    public int ALNext() => MarkedOnlyNext(() => (_handle?.ALNext() ?? 0) != 0) ? 1 : 0;

    /// <summary>ALNext(steps) — advance N records forward (RecRef.Next(N) in AL).</summary>
    public int ALNext(int steps)
    {
        if (_handle == null) return 0;
        int moved = 0;
        for (int i = 0; i < steps; i++)
        {
            if (!MarkedOnlyNext(() => _handle.ALNext() != 0)) break;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// When MarkedOnly is true, keep advancing until we land on a marked record.
    /// </summary>
    private bool MarkedOnlyNext(Func<bool> nextAction)
    {
        if (!_markedOnly) return nextAction();
        while (nextAction())
        {
            if (ALMark()) return true;
        }
        return false;
    }

    /// <summary>ALGetView — delegates to the underlying record handle's view text.</summary>
    public string ALGetView() => _handle?.ALGetView() ?? string.Empty;

    /// <summary>ALSetView — applies a filter-view string (stub: no-op; view strings require full BC parser).</summary>
    public void ALSetView(string view) => _handle?.ALSetView(view);

    // -- Insert / Modify / Delete --

    public bool Insert() => Insert(false);
    public bool Insert(bool runTrigger) => _handle != null && _handle.ALInsert(DataError.ThrowError, runTrigger);
    public bool ALInsert(DataError errorLevel) => _handle != null && _handle.ALInsert(errorLevel);
    public bool ALInsert(DataError errorLevel, bool runTrigger) => _handle != null && _handle.ALInsert(errorLevel, runTrigger);

    public bool Modify() => Modify(false);
    public bool Modify(bool runTrigger) => _handle != null && _handle.ALModify(DataError.ThrowError, runTrigger);
    public bool ALModify(DataError errorLevel) => _handle != null && _handle.ALModify(errorLevel);
    public bool ALModify(DataError errorLevel, bool runTrigger) => _handle != null && _handle.ALModify(errorLevel, runTrigger);

    public bool Delete() => Delete(false);
    public bool Delete(bool runTrigger) => _handle != null && _handle.ALDelete(DataError.ThrowError, runTrigger);
    public bool ALDelete(DataError errorLevel) => _handle != null && _handle.ALDelete(errorLevel);
    public bool ALDelete(DataError errorLevel, bool runTrigger) => _handle != null && _handle.ALDelete(errorLevel, runTrigger);

    public void DeleteAll() => DeleteAll(false);
    public void DeleteAll(bool runTrigger) => _handle?.ALDeleteAll(DataError.ThrowError, runTrigger);
    public void ALDeleteAll() => _handle?.ALDeleteAll();
    public void ALDeleteAll(DataError errorLevel) => _handle?.ALDeleteAll(errorLevel);
    public void ALDeleteAll(DataError errorLevel, bool runTrigger) => _handle?.ALDeleteAll(errorLevel, runTrigger);
    public void ALDeleteAll(bool runTrigger) => _handle?.ALDeleteAll(runTrigger);

    // -- Init / Reset --

    public void ALInit() => _handle?.ALInit();
    public void ALReset() => _handle?.ALReset();

    // -- Filter operations --

    public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
        => _handle?.ALSetRange(fieldNo, expectedType, fromValue, toValue);

    public void ALSetRangeSafe(int fieldNo, NavType expectedType)
        => _handle?.ALSetRangeSafe(fieldNo, expectedType);
    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value)
        => _handle?.ALSetRangeSafe(fieldNo, expectedType, value);
    public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue)
        => _handle?.ALSetRangeSafe(fieldNo, expectedType, fromValue, toValue);

    public void ALSetFilter(int fieldNo, string filterExpression, params NavValue[] args)
        => _handle?.ALSetFilter(fieldNo, filterExpression, args);
    public void ALSetFilter(int fieldNo, NavType expectedType, string filterExpression, params NavValue[] args)
        => _handle?.ALSetFilter(fieldNo, expectedType, filterExpression, args);

    public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos)
        => _handle?.ALSetCurrentKey(errorLevel, fieldNos);
    public void ALSetCurrentKey(params int[] fieldNos)
        => _handle?.ALSetCurrentKey(fieldNos);

    // -- RecordId --

    public NavRecordId ALRecordId => _handle?.ALRecordId ?? NavRecordId.Default;

    // -- Get (by RecordId or key values) --

    public bool ALGet(DataError errorLevel, params NavValue[] keyValues)
        => _handle != null && _handle.ALGet(errorLevel, keyValues);

    public bool ALGetBySystemId(DataError errorLevel, Guid systemId)
        => _handle != null && _handle.ALGetBySystemId(errorLevel, systemId);

    public bool ALGetBySystemId(Guid systemId)
        => _handle != null && _handle.ALGetBySystemId(systemId);

    // -- SetTable / GetTable --
    // These copy field data between a typed Record variable and the RecordRef.
    // In the BC compiler output, these appear as:
    //   recRef.ALSetTable(record.Target)  — copy typed record's fields into recRef
    //   recRef.ALGetTable(record.Target)  — copy recRef's fields back into typed record
    // After the rewriter strips .Target, the calls become:
    //   recRef.ALSetTable(record)  — where record is a Record<N> instance

    /// <summary>
    /// ALSetTable — AL: RecRef.SetTable(Rec) — copies RecRef's current record INTO the Record variable.
    /// Direction: RecRef → Rec
    /// </summary>
    public void ALSetTable(object record)
    {
        if (record == null || _handle == null) return;
        var targetHandle = ResolveHandle(record);
        if (targetHandle == null) return;
        targetHandle.ALCopy(_handle);
    }

    /// <summary>
    /// ALGetTable — AL: RecRef.GetTable(Rec) — makes the RecRef refer to Rec's table and copies Rec's record.
    /// Direction: Rec → RecRef
    /// </summary>
    public void ALGetTable(object record)
    {
        if (record == null) return;
        var sourceHandle = ResolveHandle(record);
        if (sourceHandle == null) return;

        var tableId = sourceHandle.TableId;
        if (tableId != 0) Number = tableId;
        _handle = new MockRecordHandle(Number);
        _handle.ALCopy(sourceHandle);
    }

    /// <summary>
    /// Resolve a record argument to a MockRecordHandle.
    /// Handles both direct MockRecordHandle args and Record class wrappers.
    /// </summary>
    private static MockRecordHandle? ResolveHandle(object record)
    {
        if (record is MockRecordHandle handle)
            return handle;
        var recProp = record.GetType().GetProperty("Rec");
        if (recProp != null)
            return recProp.GetValue(record) as MockRecordHandle;
        return null;
    }

    // -- FieldCount --

    public int ALFieldCount
    {
        get
        {
            // Prefer schema field count from metadata registry
            var schemaCount = TableFieldRegistry.GetFieldCount(Number);
            if (schemaCount > 0) return schemaCount;
            return _handle?.FieldCount ?? 0;
        }
    }

    // -- LockTable (no-op) --
    public void ALLockTable(DataError errorLevel = DataError.ThrowError) { }
    public void ALSetRecFilter() => _handle?.ALSetRecFilter();

    // -- Mark / MarkedOnly --
    private readonly HashSet<string> _markedRecords = new();
    private bool _markedOnly;

    /// <summary>
    /// ALMark() — returns whether the current record is marked.
    /// </summary>
    public bool ALMark()
    {
        return _markedRecords.Contains(GetCurrentPkKey());
    }

    /// <summary>
    /// ALMark(bool) — marks or unmarks the current record.
    /// </summary>
    public void ALMark(bool mark)
    {
        var key = GetCurrentPkKey();
        if (mark)
            _markedRecords.Add(key);
        else
            _markedRecords.Remove(key);
    }

    /// <summary>
    /// ALMarkedOnly — when true, iteration (FindSet/Next) returns only marked records.
    /// </summary>
    public bool ALMarkedOnly
    {
        get => _markedOnly;
        set => _markedOnly = value;
    }

    /// <summary>ALClearMarks — removes all marks.</summary>
    public void ALClearMarks()
    {
        _markedRecords.Clear();
    }

    /// <summary>
    /// Build a string key from the current record's PK field values for marking.
    /// Uses the handle's registered PK fields, falling back to field 1.
    /// </summary>
    private string GetCurrentPkKey()
    {
        if (_handle == null) return "";
        var pkFields = _handle.GetPrimaryKeyFieldNos();
        var parts = new List<string>();
        foreach (var fno in pkFields)
        {
            // Use NavType.Integer as a dummy — GetFieldValueSafe returns the stored value
            // regardless of expectedType; it only matters for the default fallback.
            var val = _handle.GetFieldValueSafe(fno, NavType.Integer);
            parts.Add(AlCompat.Format(val));
        }
        return string.Join("|", parts);
    }

    // -- ChangeCompany (no-op in standalone — single company) --
    public bool ALChangeCompany(string companyName) => true;
    public bool ALChangeCompany(DataError errorLevel, string companyName) => true;
    public bool ALChangeCompany() => true;
    public bool ALChangeCompany(DataError errorLevel) => true;

    // -- Ascending (delegates to handle) --
    public bool ALAscending
    {
        get => _handle?.ALAscending ?? true;
        set => _handle?.SetOverallAscending(value);
    }

    // -- HasFilter / GetFilters --
    public bool ALHasFilter => _handle?.FiltersActive ?? false;
    public string ALGetFilters => _handle?.ALGetFilters ?? "";

    // -- GetPosition / SetPosition --
    public string ALGetPosition()
    {
        if (_handle == null) return "";
        var val = _handle.GetFieldValueSafe(1, NavType.Integer);
        string valStr;
        try { valStr = AlCompat.Format(val); }
        catch { valStr = val?.ToString() ?? ""; }
        return $"Entry No.={valStr}";
    }
    public string ALGetPosition(bool useCaptions) => ALGetPosition();
    public void ALSetPosition(string position) { /* No-op stub */ }

    // -- Rename --
    public void ALRename(params NavValue[] values) => _handle?.ALRename(DataError.ThrowError, values);
    public bool ALRename(DataError errorLevel, params NavValue[] values)
    {
        if (_handle == null) return false;
        return _handle.ALRename(errorLevel, values);
    }

    // -- FieldExists --
    public bool ALFieldExists(int fieldNo) => _handle?.HasField(fieldNo) ?? false;
    public bool ALFieldExists(string fieldName) => false;

    // -- ModifyAll --
    public void ALModifyAll(int fieldNo, NavValue newValue) => _handle?.ALModifyAllSafe(fieldNo, NavType.Text, newValue);
    public void ALModifyAll(int fieldNo, NavValue newValue, bool runTrigger) => _handle?.ALModifyAllSafe(fieldNo, NavType.Text, newValue, runTrigger);

    // -- GetFilter (per-field — delegates to handle) --
    public string ALGetFilter(int fieldNo) => _handle?.ALGetFilter(fieldNo) ?? "";

    // -- CurrentKey / CurrentKeyIndex --
    public string ALCurrentKey => _handle?.ALCurrentKey ?? "";
    public int ALCurrentKeyIndex => 1;

    // -- KeyCount / KeyIndex --
    public int ALKeyCount => 1;
    public MockKeyRef ALKeyIndex(int index)
    {
        if (index == 1 && _handle != null)
        {
            var pkFields = _handle.GetPrimaryKeyFieldNos();
            return new MockKeyRef(Number, pkFields, this);
        }
        throw new Exception($"RecordRef.KeyIndex: index {index} out of range (only 1 key available)");
    }

    // -- CurrentCompany --
    public string ALCurrentCompany => "";

    // -- TableCaption / TableName --
    public string ALTableCaption => _handle?.ALTableCaption ?? "";
    public string ALTableName => _handle?.ALTableName ?? "";

    // -- IsTemporary --
    public bool ALIsTemporary => false;
    public int ALSystemIdNo => 2000000000;
    public int ALSystemCreatedAtNo => 2000000001;
    public int ALSystemCreatedByNo => 2000000002;
    public int ALSystemModifiedAtNo => 2000000003;
    public int ALSystemModifiedByNo => 2000000004;

    // -- ReadIsolation (no-op in standalone mode) --
    /// <summary>
    /// AL's RecordRef.ReadIsolation — sets read isolation level.
    /// No-op in standalone mode since there's no SQL transaction.
    /// </summary>
    public object ALReadIsolation
    {
        get => 0;
        set { /* No-op */ }
    }

    // -- Duplicate --
    /// <summary>
    /// ALDuplicate — AL: RecRef.Duplicate() — returns a copy of this RecordRef
    /// pointing to the same table with copied field data and filters.
    /// </summary>
    public MockRecordRef ALDuplicate(object? parent = null)
    {
        var dup = new MockRecordRef();
        dup.Number = Number;
        if (_handle != null)
        {
            dup._handle = new MockRecordHandle(Number);
            dup._handle.ALCopy(_handle);
        }
        return dup;
    }

    // -- Assign (`:=` operator in AL, lowered to ALAssign by BC compiler) --
    public void ALAssign(MockRecordRef other)
    {
        Number = other.Number;
        if (other._handle != null)
        {
            _handle = new MockRecordHandle(Number);
            _handle.ALCopy(other._handle);
        }
        else
        {
            _handle = null;
        }
    }

    /// <summary>
    /// ALAssign(MockFieldRef) — BC emits <c>recRef.ALAssign(fldRef)</c> when AL
    /// code does <c>RecRef := FldRef.Record()</c>. The compiler inlines the
    /// .Record() call, so we extract the owner RecordRef from the FieldRef.
    /// </summary>
    public void ALAssign(MockFieldRef fieldRef)
    {
        var owner = fieldRef.ALRecord();
        ALAssign(owner);
    }

    // -- KeyIndex (with compilation target stripped) --
    public MockKeyRef ALKeyIndex(object compilationTarget, int index) => ALKeyIndex(index);

    // -- Copy --
    public void ALCopy(MockRecordRef source, bool shareFilters = false)
    {
        if (source._handle != null && _handle != null)
            _handle.ALCopy(source._handle, shareFilters);
    }

    // -- Internal helpers for MockFieldRef --

    internal NavValue GetFieldValue(int fieldNo)
    {
        if (_handle == null) return NavText.Default(0);
        return _handle.GetFieldValueSafe(fieldNo, NavType.Text);
    }

    internal void SetFieldValue(int fieldNo, NavValue value)
    {
        _handle?.SetFieldValueSafe(fieldNo, NavType.Text, value);
    }

    internal void ValidateFieldValue(int fieldNo, NavValue value)
    {
        _handle?.ALValidateSafe(fieldNo, NavType.Text, value);
    }

    internal void SetRange(int fieldNo, NavValue value)
    {
        _handle?.ALSetRangeSafe(fieldNo, NavType.Text, value);
    }

    internal void SetRange(int fieldNo, NavValue fromValue, NavValue toValue)
    {
        _handle?.ALSetRangeSafe(fieldNo, NavType.Text, fromValue, toValue);
    }

    internal void ClearRange(int fieldNo)
    {
        _handle?.ALSetRangeSafe(fieldNo, NavType.Text);
    }

    internal void SetFilter(int fieldNo, string filterExpression, NavValue[] args)
    {
        _handle?.ALSetFilter(fieldNo, filterExpression, args);
    }

    internal void TestField(int fieldNo)
    {
        _handle?.ALTestFieldSafe(fieldNo, NavType.Text);
    }

    internal void TestField(int fieldNo, NavValue expectedValue)
    {
        _handle?.ALTestFieldSafe(fieldNo, NavType.Text, expectedValue);
    }

    // -- Internal helpers for MockFieldRef filter/range access --

    internal string GetFieldFilter(int fieldNo)
    {
        return _handle?.ALGetFilter(fieldNo) ?? "";
    }

    internal NavValue? GetFieldRangeMin(int fieldNo)
    {
        return _handle?.GetRangeMin(fieldNo);
    }

    internal NavValue? GetFieldRangeMax(int fieldNo)
    {
        return _handle?.GetRangeMax(fieldNo);
    }

    /// <summary>Expose the table ID for MockFieldRef enum lookups.</summary>
    internal int TableId => Number;

    /// <summary>Expose the underlying handle for MockFieldRef.CalcSum.</summary>
    internal MockRecordHandle? Handle => _handle;
}
