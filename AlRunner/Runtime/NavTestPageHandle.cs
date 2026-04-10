// OUT OF SCOPE — see CLAUDE.md
// TestPage mocking (NavTestPageHandle / MockTestPageHandle) is out of scope for the
// standalone unit test runner. AL tests that use TestPage variables to drive UI
// cannot run in standalone mode without a BC page runtime. Stefan will decide whether
// to keep, remove, or promote this file when TestPage support is needed.

namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Mock for NavTestPageHandle — the BC test framework's test page wrapper.
/// In AL, TestPage variables let tests open pages, read/write fields, invoke actions,
/// and navigate records. In standalone mode, we provide a minimal stub that tracks
/// the page ID and delegates record operations to a MockRecordHandle.
/// </summary>
public class MockTestPageHandle
{
    private readonly int _pageId;
    private readonly MockRecordHandle _record;

    public MockTestPageHandle(int pageId)
    {
        _pageId = pageId;
        // Use the page ID as a pseudo table ID for the underlying record
        _record = new MockRecordHandle(pageId);
    }

    // -----------------------------------------------------------------------
    // Page open/close
    // -----------------------------------------------------------------------

    public void ALOpenNew() { }
    public void ALOpenEdit() { }
    public void ALOpenView() { }
    public void ALClose() { }
    public void ALTrap() { }
    public void ALNew() { }

    /// <summary>
    /// AL's FILTER property on test pages - returns the underlying record handle
    /// for setting filters.
    /// </summary>
    public MockRecordHandle ALFilter => _record;

    public string ALCaption => $"Page{_pageId}";

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    public bool ALFirst()
    {
        return _record.ALFindFirst(DataError.TrapError);
    }

    public bool ALLast()
    {
        return _record.ALFindLast(DataError.TrapError);
    }

    public int ALNext()
    {
        return _record.ALNext();
    }

    public bool ALIsExpandable => false;

    public void ALExpand(bool expand) { }

    // -----------------------------------------------------------------------
    // Record interaction
    // -----------------------------------------------------------------------

    public void ALGoToRecord(DataError errorLevel, MockRecordHandle record)
    {
        // In a real BC page, this would navigate to the record.
        // Stub: no-op
    }

    public void ALGoToKey(DataError errorLevel, params NavValue[] keyValues)
    {
        // Stub: no-op
    }

    // -----------------------------------------------------------------------
    // Field access
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a test field handle for the given field ID.
    /// The test field handle wraps field get/set on the underlying record.
    /// </summary>
    public MockTestFieldHandle GetField(int fieldId)
    {
        return new MockTestFieldHandle(_record, fieldId);
    }

    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a test action handle for the given action ID.
    /// </summary>
    public MockTestActionHandle GetAction(int actionId)
    {
        return new MockTestActionHandle(_pageId, actionId);
    }

    /// <summary>
    /// Returns a built-in action handle (OK, Cancel, Close, etc.).
    /// </summary>
    public MockTestActionHandle GetBuiltInAction(int actionId)
    {
        return new MockTestActionHandle(_pageId, actionId);
    }

    /// <summary>
    /// GetBuiltInAction(FormResult) — overload for when the transpiler emits FormResult enum values.
    /// </summary>
    public MockTestActionHandle GetBuiltInAction(FormResult result)
    {
        return new MockTestActionHandle(_pageId, (int)result);
    }

    // -----------------------------------------------------------------------
    // Parts (subpages)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a test page handle for a subpage part.
    /// </summary>
    public MockTestPageHandle GetPart(int partId)
    {
        return new MockTestPageHandle(partId);
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets the editable state of the page.
    /// </summary>
    public bool ALEditable => true;

    /// <summary>
    /// Gets the visible state of the page.
    /// </summary>
    public bool ALVisible => true;
}

/// <summary>
/// Mock for test field handles on test pages.
/// </summary>
public class MockTestFieldHandle
{
    private readonly MockRecordHandle _record;
    private readonly int _fieldId;

    public MockTestFieldHandle(MockRecordHandle record, int fieldId)
    {
        _record = record;
        _fieldId = fieldId;
    }

    /// <summary>Get the field value.</summary>
    public NavValue ALValue
    {
        get => _record.GetFieldValueSafe(_fieldId, NavType.Text);
        set => _record.SetFieldValueSafe(_fieldId, NavType.Text, value);
    }

    /// <summary>Set value (triggers validation).</summary>
    public void ALSetValue(NavValue value)
    {
        _record.SetFieldValueSafe(_fieldId, NavType.Text, value);
    }

    /// <summary>Set value with ITreeObject context (first arg stripped to null by rewriter).</summary>
    public void ALSetValue(object? treeObject, NavValue value)
    {
        _record.SetFieldValueSafe(_fieldId, NavType.Text, value);
    }

    /// <summary>Assert the field value equals expected.</summary>
    public void ALAssertEquals(NavValue expected)
    {
        var actual = _record.GetFieldValueSafe(_fieldId, NavType.Text);
        if (actual?.ToString() != expected?.ToString())
            throw new Exception($"AssertEquals failed on field {_fieldId}: expected '{expected}' but was '{actual}'");
    }

    public bool ALEditable => true;
    public bool ALVisible => true;
    public bool ALEnabled => true;
    public string ALCaption => $"Field{_fieldId}";

    /// <summary>Get value as integer.</summary>
    public int ALAsInteger() => _record.GetFieldValueSafe(_fieldId, NavType.Integer).ToInt32();

    /// <summary>Get value as decimal.</summary>
    public decimal ALAsDecimal()
    {
        try { return (decimal)_record.GetFieldValueSafe(_fieldId, NavType.Decimal).ToDecimal(); }
        catch { return 0m; }
    }

    /// <summary>Get value as boolean.</summary>
    public bool ALAsBoolean()
    {
        try { return (bool)(NavBoolean)_record.GetFieldValueSafe(_fieldId, NavType.Boolean); }
        catch { return false; }
    }

    /// <summary>Invoke assist edit on the field.</summary>
    public void ALAssistEdit() { }

    /// <summary>Invoke drilldown on the field.</summary>
    public void ALDrillDown() { }
    /// <summary>Alias for ALDrillDown (different casing in some transpiler output).</summary>
    public void ALDrilldown() { }

    /// <summary>Invoke lookup on the field.</summary>
    public void ALLookup() { }
}

/// <summary>
/// Mock for test action handles on test pages.
/// </summary>
public class MockTestActionHandle
{
    private readonly int _pageId;
    private readonly int _actionId;

    public MockTestActionHandle(int pageId, int actionId)
    {
        _pageId = pageId;
        _actionId = actionId;
    }

    /// <summary>Invoke the action.</summary>
    public void ALInvoke()
    {
        // No-op in standalone mode — page actions can't execute without UI runtime
    }

    public bool ALEnabled => true;
    public bool ALVisible => true;
}
