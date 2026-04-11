namespace AlRunner.Runtime;

/// <summary>
/// Compile-only stub for NavRecordRef / AL's <c>RecordRef</c>.
/// NavRecordRef's real constructors all take an ITreeObject parent with a
/// valid Tree handler — unavailable standalone — so declaring a RecordRef
/// local was excluding the whole containing codeunit.
///
/// MockRecordRef satisfies the declaration-site type and the most common
/// call surface (Open/Close/Find/IsEmpty/Clear/Count/Number) with no-op
/// behavior. Consistent with the runner's documented RecordRef policy:
/// "stubs compile but do not function at runtime". Anything that reads a
/// real row back will return default values.
/// </summary>
public class MockRecordRef
{
    public int Number { get; private set; }

    public MockRecordRef() { }

    public void Clear()
    {
        Number = 0;
    }

    public void Open(int tableId) => Number = tableId;
    public void Open(int tableId, bool temporary) => Number = tableId;
    public void Open(int tableId, bool temporary, string companyName) => Number = tableId;

    public void Close()
    {
        Number = 0;
    }

    public bool IsEmpty() => true;
    public bool Find(string which) => false;
    public bool FindFirst() => false;
    public bool FindLast() => false;
    public bool FindSet() => false;
    public bool Next() => false;
    public int Count() => 0;

    // AL-lowered surface the BC compiler sometimes emits for RecordRef calls.
    public void ALOpen(int tableId) => Number = tableId;
    public void ALClose() => Number = 0;
    public bool ALIsEmpty() => true;
    public bool ALFindFirst() => false;
    public bool ALFindSet() => false;
    public int ALCount() => 0;
    public int ALGetNumber() => Number;
}
