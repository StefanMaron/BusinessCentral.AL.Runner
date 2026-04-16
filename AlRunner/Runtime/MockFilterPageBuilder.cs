namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory replacement for NavFilterPageBuilder.
/// FilterPageBuilder builds a filter dialog at runtime. In standalone mode there
/// is no UI, so this mock:
///   - Stores table/field registrations in memory.
///   - SetView/GetView round-trip a filter string per named caption.
///   - RunModal returns FormResult.OK (Action::OK) — no dialog is shown.
///   - Count, Name, PageCaption are fully functional.
/// </summary>
public class MockFilterPageBuilder
{
    private readonly List<(string Caption, int TableNo, string View)> _tables = new();

    /// <summary>Caption shown in the filter dialog title bar. Get/set in-memory.</summary>
    public string ALPageCaption { get; set; } = string.Empty;

    // ── AddXxx methods ────────────────────────────────────────────────────────

    /// <summary>Register a table by number with a display caption.</summary>
    public int ALAddTable(string caption, int tableNo)
    {
        _tables.Add((caption, tableNo, string.Empty));
        return _tables.Count;
    }

    /// <summary>DataError overload — BC emits this form for AL Text/Integer parameters.</summary>
    public int ALAddTable(DataError errorLevel, string caption, int tableNo)
    {
        return ALAddTable(caption, tableNo);
    }

    /// <summary>Register a record instance; stores caption and table metadata.</summary>
    public int ALAddRecord(string caption, MockRecordHandle record)
    {
        _tables.Add((caption, record.TableId, string.Empty));
        return _tables.Count;
    }

    public int ALAddRecord(DataError errorLevel, string caption, MockRecordHandle record)
        => ALAddRecord(caption, record);

    /// <summary>Register a RecordRef; stores caption and table metadata.</summary>
    public int ALAddRecordRef(string caption, MockRecordRef recordRef)
    {
        _tables.Add((caption, recordRef.TableId, string.Empty));
        return _tables.Count;
    }

    public int ALAddRecordRef(DataError errorLevel, string caption, MockRecordRef recordRef)
        => ALAddRecordRef(caption, recordRef);

    /// <summary>Register by table + field number; stored as a table entry.</summary>
    public int ALAddFieldNo(string caption, int tableNo, int fieldNo)
    {
        _tables.Add((caption, tableNo, string.Empty));
        return _tables.Count;
    }

    public int ALAddFieldNo(DataError errorLevel, string caption, int tableNo, int fieldNo)
        => ALAddFieldNo(caption, tableNo, fieldNo);

    /// <summary>Register via a FieldRef. Stores the caption and field's table.</summary>
    public int ALAddField(string caption, MockFieldRef fieldRef)
    {
        var tableId = 0;
        try { tableId = (int)fieldRef.Record().TableId; } catch { /* ignore */ }
        _tables.Add((caption, tableId, string.Empty));
        return _tables.Count;
    }

    public int ALAddField(DataError errorLevel, string caption, MockFieldRef fieldRef)
        => ALAddField(caption, fieldRef);

    // ── GetView / SetView ─────────────────────────────────────────────────────

    /// <summary>
    /// Return the filter string currently associated with the named table caption.
    /// Returns empty string if the caption is not registered or no view was set.
    /// </summary>
    public string ALGetView(string caption)
    {
        var idx = _tables.FindIndex(t => t.Caption == caption);
        return idx >= 0 ? _tables[idx].View : string.Empty;
    }

    public string ALGetView(DataError errorLevel, string caption)
        => ALGetView(caption);

    /// <summary>
    /// Store a filter string for the named table caption.
    /// Has no effect if the caption is not registered.
    /// </summary>
    public void ALSetView(string caption, string view)
    {
        var idx = _tables.FindIndex(t => t.Caption == caption);
        if (idx >= 0)
        {
            var (cap, tno, _) = _tables[idx];
            _tables[idx] = (cap, tno, view);
        }
    }

    public void ALSetView(DataError errorLevel, string caption, string view)
        => ALSetView(caption, view);

    // ── Count / Name ──────────────────────────────────────────────────────────

    /// <summary>Number of registered table entries.</summary>
    public int ALCount => _tables.Count;

    /// <summary>
    /// Return the display caption at the given 1-based index.
    /// Returns empty string if the index is out of range.
    /// </summary>
    public string ALName(int index)
        => (index >= 1 && index <= _tables.Count) ? _tables[index - 1].Caption : string.Empty;

    public string ALName(DataError errorLevel, int index)
        => ALName(index);

    // ── RunModal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulate running the filter dialog. In standalone mode no UI is shown;
    /// returns FormResult.OK (equivalent to Action::OK).
    /// </summary>
    public FormResult ALRunModal()
        => FormResult.OK;

    public FormResult ALRunModal(DataError errorLevel)
        => FormResult.OK;
}
