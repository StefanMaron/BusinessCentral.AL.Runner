// OUT OF SCOPE — see CLAUDE.md
// RecordRef mocking (MockRecordRef) is out of scope for the standalone unit test runner.
// AL code that uses RecordRef for dynamic record manipulation cannot be fully unit-tested
// in standalone mode. This stub satisfies compilation but field access via FieldRef is
// not functional. Stefan will decide whether to keep, remove, or promote this file.

namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory replacement for NavRecordRef which requires ITreeObject (NavSession chain).
/// NavRecordRef is the AL RecordRef type — a dynamic reference to a record.
/// In standalone mode, we provide a minimal stub that supports the operations
/// used in test code (Open, ALAssign, field access, etc.).
/// </summary>
public class MockRecordRef
{
    private int _tableId;
    private MockRecordHandle? _record;

    public MockRecordRef()
    {
    }

    public MockRecordRef(int tableId)
    {
        _tableId = tableId;
        _record = new MockRecordHandle(tableId);
    }

    /// <summary>
    /// AL RecordRef.Open(tableId) — opens a reference to the specified table.
    /// </summary>
    public void ALOpen(int tableId)
    {
        _tableId = tableId;
        _record = new MockRecordHandle(tableId);
    }

    /// <summary>
    /// AL RecordRef.Open(tableId, temporary) — opens a reference with temporary flag.
    /// </summary>
    public void ALOpen(int tableId, bool temporary)
    {
        _tableId = tableId;
        _record = new MockRecordHandle(tableId);
    }

    /// <summary>
    /// AL RecordRef.Open(compilationTarget, tableId) — transpiled from RecordRef.Open(tableId).
    /// The BC transpiler emits CompilationTarget as the first arg; we ignore it.
    /// </summary>
    public void ALOpen(CompilationTarget target, int tableId)
    {
        _tableId = tableId;
        _record = new MockRecordHandle(tableId);
    }

    /// <summary>
    /// AL RecordRef.Assign — copies from another RecordRef.
    /// Used in ByRef patterns: setValue => this.recRef.ALAssign(setValue)
    /// </summary>
    public void ALAssign(MockRecordRef other)
    {
        _tableId = other._tableId;
        _record = other._record;
    }

    /// <summary>
    /// AL RecordRef.Close — releases the reference.
    /// </summary>
    public void ALClose()
    {
        // No-op in standalone mode
    }

    /// <summary>
    /// AL RecordRef.Number — returns the table number.
    /// Property (not method) to match transpiled code: recRef.ALNumber
    /// </summary>
    public int ALNumber => _tableId;

    /// <summary>
    /// AL RecordRef.GetTable — gets the underlying record handle.
    /// </summary>
    public MockRecordHandle GetRecord()
    {
        return _record ?? new MockRecordHandle(_tableId);
    }

    /// <summary>
    /// AL RecordRef.ReadPermission — stub, always returns true in standalone mode.
    /// </summary>
    public bool ALReadPermission => true;

    /// <summary>
    /// AL RecordRef.WritePermission — stub, always returns true in standalone mode.
    /// </summary>
    public bool ALWritePermission => true;

    /// <summary>
    /// AL RecordRef.Name — returns the table name (stub: returns table ID as string).
    /// </summary>
    public string ALName() => $"Table{_tableId}";

    /// <summary>
    /// AL RecordRef.Count — delegates to underlying record.
    /// </summary>
    public int ALCount => GetRecord().ALCount;

    /// <summary>
    /// AL RecordRef.IsEmpty — delegates to underlying record.
    /// </summary>
    public bool ALIsEmpty => GetRecord().ALIsEmpty;

    /// <summary>
    /// AL RecordRef.Caption — returns the table caption (stub: table ID as string).
    /// </summary>
    public string ALCaption => $"Table{_tableId}";

    /// <summary>
    /// AL RecordRef.GetFilters — returns active filters as string (stub: empty string).
    /// </summary>
    public string ALGetFilters => "";

    /// <summary>
    /// AL RecordRef.FindSet — delegates to underlying record.
    /// </summary>
    public bool ALFindSet() => GetRecord().ALFindSet();

    /// <summary>
    /// AL RecordRef.FindSet(DataError) — delegates to underlying record.
    /// </summary>
    public bool ALFindSet(DataError dataError) => GetRecord().ALFindSet();

    /// <summary>
    /// AL RecordRef.Next — delegates to underlying record.
    /// </summary>
    public int ALNext() => GetRecord().ALNext();

    /// <summary>
    /// AL RecordRef.FieldCount — stub, returns 0.
    /// </summary>
    public int ALFieldCount() => 0;

    /// <summary>
    /// AL RecordRef.IsTemporary — stub, returns false.
    /// </summary>
    public bool ALIsTemporary => false;

    /// <summary>
    /// AL RecordRef.Copy — copies the record ref.
    /// </summary>
    public void ALCopy(MockRecordRef source)
    {
        _tableId = source._tableId;
        _record = source._record;
    }

    /// <summary>
    /// AL RecordRef.FindSet — delegates to underlying record.
    /// </summary>
    public bool ALFindSet(int mode) => GetRecord().ALFindSet();

    /// <summary>
    /// AL RecordRef.FieldIndex — stub returning a field ref-like value.
    /// </summary>
    public object ALFieldIndex(int index) => index;

    /// <summary>
    /// AL RecordRef.RecordId — returns a stub RecordId (NavRecordId needs NavSession).
    /// Used as a string representation of the current record's identity.
    /// </summary>
    public object ALRecordId => $"Table{_tableId}:Record";

    /// <summary>
    /// AL RecordRef.Field(fieldNo) — returns a NavFieldRef for the specified field.
    /// Creates a NavFieldRef with null ITreeObject; works for basic field access in standalone mode.
    /// </summary>
    public NavFieldRef ALField(int fieldNo)
    {
        // NavFieldRef requires ITreeObject but works with null for basic operations.
        // Return a new NavFieldRef — actual field value access goes through MockRecordHandle.
        return new NavFieldRef(null!);
    }
}
