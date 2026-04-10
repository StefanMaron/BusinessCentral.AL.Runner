// OUT OF SCOPE — see CLAUDE.md
// Page mocking (MockFormHandle) is out of scope for the standalone unit test runner.
// AL code that uses Page, PAGE.RUN, PAGE.RUNMODAL, or lookups cannot be unit-tested
// without injecting a page interface via AL interfaces. Stefan will decide whether to
// keep, remove, or promote this file to in-scope when Page support is needed.

namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Mock for NavFormHandle — the BC runtime page handle used for page interactions.
/// In AL, Page variables and page-related calls use NavFormHandle internally.
/// In standalone mode, we provide a minimal stub that tracks the page ID.
///
/// NavFormHandle is distinct from NavTestPageHandle (MockTestPageHandle):
/// - NavFormHandle is used in runtime code (PAGE.RUN, PAGE.RUNMODAL, lookups)
/// - NavTestPageHandle is used in test code (TestPage variables)
/// </summary>
public class MockFormHandle
{
    private readonly int _pageId;

    public MockFormHandle(int pageId)
    {
        _pageId = pageId;
    }

    /// <summary>RunModal — returns default FormResult (LookupOK).</summary>
    public FormResult RunModal()
    {
        return FormResult.LookupOK;
    }

    /// <summary>Gets or sets the lookup mode.</summary>
    public bool LookupMode { get; set; }

    /// <summary>Gets the caption of the page.</summary>
    public string ALCaption => $"Page{_pageId}";

    /// <summary>Opens the page for editing.</summary>
    public void ALOpenEdit() { }

    /// <summary>Opens the page for viewing.</summary>
    public void ALOpenView() { }

    /// <summary>Opens a new record.</summary>
    public void ALNew() { }

    /// <summary>Closes the page.</summary>
    public void ALClose() { }

    /// <summary>Close the page (non-AL naming variant).</summary>
    public void Close() { }

    /// <summary>Run the page.</summary>
    public void Run() { }

    /// <summary>Page caption property.</summary>
    public string PageCaption { get; set; } = "";

    /// <summary>Save the current record.</summary>
    public void SaveRecord() { }

    /// <summary>Update the page.</summary>
    public void Update(bool saveRecord = true) { }

    /// <summary>Set selection filter on a record handle.</summary>
    public void SetSelectionFilter(MockRecordHandle record) { }

    /// <summary>Traps the next page that opens.</summary>
    public void ALTrap() { }

    /// <summary>Navigate to first record.</summary>
    public bool ALFirst() => false;

    /// <summary>Navigate to last record.</summary>
    public bool ALLast() => false;

    /// <summary>Navigate to next record.</summary>
    public int ALNext() => 0;

    /// <summary>Get a field handle by ID.</summary>
    public MockTestFieldHandle GetField(int fieldId)
    {
        return new MockTestFieldHandle(new MockRecordHandle(_pageId), fieldId);
    }

    /// <summary>Get an action handle by ID.</summary>
    public MockTestActionHandle GetAction(int actionId)
    {
        return new MockTestActionHandle(_pageId, actionId);
    }

    /// <summary>Get a part (subpage) handle.</summary>
    public MockFormHandle GetPart(int partId)
    {
        return new MockFormHandle(partId);
    }

    /// <summary>Get a record from the page into the target record handle.</summary>
    public void GetRecord(MockRecordHandle target)
    {
        // No-op: can't extract records from a page in standalone mode
    }

    /// <summary>Create a NavFormHandle from this (used in CurrPage.GetPart().CreateNavFormHandle()).</summary>
    public MockFormHandle CreateNavFormHandle(object? treeObject)
    {
        return this;
    }

    /// <summary>Invoke a method by member ID (for page-level dispatch).</summary>
    public object? Invoke(int memberId, object[] args)
    {
        // No-op: page-level method dispatch not supported standalone
        return null;
    }
}
