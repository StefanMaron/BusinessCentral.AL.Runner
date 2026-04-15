namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Mock for NavKeyRef — represents a key definition on a table.
/// In standalone mode we only know the primary key (registered via
/// MockRecordHandle.RegisterPrimaryKey). ALKeyCount is always 1 and
/// ALKeyIndex(1) returns the PK fields.
/// </summary>
public class MockKeyRef
{
    private int _tableId;
    private int[] _fieldNos;
    private MockRecordRef? _recordRef;

    /// <summary>Parameterless constructor for BC-declared KeyRef variables (rewriter strips args).</summary>
    public MockKeyRef()
    {
        _fieldNos = Array.Empty<int>();
    }

    public MockKeyRef(int tableId, int[] fieldNos, MockRecordRef recordRef)
    {
        _tableId = tableId;
        _fieldNos = fieldNos;
        _recordRef = recordRef;
    }

    /// <summary>ALActive — whether this key is active. Always true.</summary>
    public bool ALActive => true;

    /// <summary>ALFieldCount — number of fields in this key.</summary>
    public int ALFieldCount => _fieldNos.Length;

    /// <summary>
    /// ALFieldIndex — returns a MockFieldRef for the Nth field (1-based) in this key.
    /// </summary>
    public MockFieldRef ALFieldIndex(int index)
    {
        if (index >= 1 && index <= _fieldNos.Length && _recordRef != null)
        {
            var fref = new MockFieldRef();
            fref.Bind(_recordRef, _fieldNos[index - 1]);
            return fref;
        }
        throw new Exception($"KeyRef.FieldIndex: index {index} out of range (1..{_fieldNos.Length})");
    }

    /// <summary>ALRecord — returns the owning RecordRef.</summary>
    public MockRecordRef ALRecord() => _recordRef ?? new MockRecordRef();

    /// <summary>
    /// ALAssign — BC emits <c>kRef.ALAssign(recRef.ALKeyIndex(this, n))</c> for
    /// <c>KRef := RecRef.KeyIndex(n)</c> in AL. Copy the source key's state.
    /// </summary>
    public void ALAssign(MockKeyRef other)
    {
        _tableId = other._tableId;
        _fieldNos = other._fieldNos;
        _recordRef = other._recordRef;
    }
}
