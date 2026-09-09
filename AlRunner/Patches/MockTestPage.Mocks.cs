// MockTestPage.Mocks.cs — the default-answering ITestPage / ITestField / ITestAction / ITestPart
// implementations, plus the pageextension-only action fallback.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
/// <summary>
/// Minimal ITestPage + ITestFilter + IDisposable implementation.
/// All field/action/filter state is held in plain dictionaries; navigation
/// always reports "no more rows" (returns false / empty).
/// </summary>
internal class MockITestPage : ITestPage
{
    private readonly Dictionary<int, string>      _filters     = new();
    private readonly Dictionary<int, MockITestField>  _fields  = new();
    private readonly Dictionary<int, MockITestAction> _actions = new();
    private bool   _ascending        = true;
    private int[]? _currentKeyFields;

    /// <summary>
    /// The refusals this PAGE's controls have recorded, read back by ValidationErrorCount /
    /// GetValidationError below (#3009). Protected so LiveNavTestPage can hand it to every
    /// control it builds — one ledger per page, shared by all of that page's controls, which
    /// is the shape real BC has.
    /// </summary>
    private protected readonly TestPageValidationErrors _validationErrors = new();

    // ── ITestPage ──────────────────────────────────────────────────────────

    // IsOpened() = false so NavTestPageBase.Open() "already open" guard passes.
    public virtual bool IsOpened()  => false;
    public virtual void Close()     { }
    public virtual void Dispose()   { }

    public virtual ITestField GetField(int id)
    {
        if (!_fields.TryGetValue(id, out var f))
            _fields[id] = f = new MockITestField();
        return f;
    }

    public virtual ITestAction GetAction(int id)
    {
        if (!_actions.TryGetValue(id, out var a))
            _actions[id] = a = new MockITestAction();
        return a;
    }

    public virtual ITestPart  GetPart(int id)                                           => new MockITestPart();
    public virtual ITestAction GetBuiltInAction(FormResult formResult)                  => new MockITestAction();
    public virtual ITestFilter GetDataItemFilter(string id)                              => this;
    public void               SetSelection(bool value)                                  { }
    public virtual void       InsertEmptyRow(bool beforeCurrent)                        { }
    public virtual bool       MoveNext()                                                => false;
    public virtual bool       MovePrevious()                                            => false;
    public virtual bool       MoveFirst()                                               => false;
    public virtual bool       MoveLast()                                                => false;
    // #3009: was hardcoded `string.Empty`, so a validation error real BC reports on the PAGE
    // came back blank. Backed by a real ledger now; the base mock simply never has anything in
    // it, because a page with no live instance has no controls that can record a refusal. Not
    // virtual: LiveNavTestPage feeds the SAME ledger rather than overriding, which is what
    // stops the two views from drifting. See TestPageValidationErrors.
    public string             GetValidationError(int index)                             => _validationErrors.Get(index);
    public virtual bool       FindRowFromTableFieldValues(int[] f, object[] v, bool fw) => false;
    public virtual bool       FindRowFromControlFieldValue(int fId, object v, bool fw)  => false;
    public virtual object?    GetBookmark()                                             => null;
    public virtual bool       GoToBookmark(object bookmark)                             => false;
    public virtual object[]   GetTableFieldValues(int[] fieldIds)                       => Array.Empty<object>();
    // Virtual since #3185: LiveNavTestPage answers these with the page's real built-in
    // page-mode actions. The base mock keeps the no-op action a page with no live instance
    // has always had.
    public virtual ITestAction Edit()                                                   => new MockITestAction();
    public virtual ITestAction View()                                                   => new MockITestAction();
    public bool               Expand(bool doExpand)                                     => false;

    // #3009: was hardcoded 0. BC's NavTestPageBase.ALValidationErrorCount() reads this member
    // straight through to AL's TestPage.ValidationErrorCount(), and NavTestField.CheckError
    // reads it before and after every control write, so a constant 0 made the page branch of
    // CheckError unreachable and made AL's own read answer 0 after a refusal BC counts.
    public int        ValidationErrorCount => _validationErrors.Count;
    public virtual FormResult FormResult   => FormResult.OK;
    public string     Name                 => string.Empty;
    public virtual string Caption          => string.Empty;
    public virtual int PageId            => 0;
    public virtual Guid FormHandle         => Guid.Empty;
    public virtual bool Creatable          => false;
    public bool       IsExpanded           => false;
    public virtual bool RuntimeEditable    => true;

    // ── ITestFilter (inherited via ITestPage) ─────────────────────────────

    public virtual void SetFilter(int fieldId, string filterValue) => _filters[fieldId] = filterValue;
    public IEnumerable<NavFilter> GetFilter() => Array.Empty<NavFilter>();
    public virtual string GetFilter(int fieldId) => _filters.TryGetValue(fieldId, out var v) ? v : string.Empty;
    // Virtual since #3316: a page with a live rowset holds its key and direction on the
    // NavRecord it walks, not here — see LiveNavTestPage's overrides. This base implementation
    // is what a page with no record has: it remembers what was set, because nothing else can.
    public virtual void SetCurrentKeyFields(int[] fields) { _currentKeyFields = fields; }
    public virtual int[]  GetCurrentKeyFields() => _currentKeyFields ?? Array.Empty<int>();

    public virtual bool   Ascending
    {
        get => _ascending;
        set => _ascending = value;
    }

    // #3316: this used to join the raw field NUMBERS, so AL's TestPage.Filter.CurrentKey()
    // answered "2, 3" where BC answers a key naming its fields. NavTestFilter.ALCurrentKey
    // reads this member straight through, so the rendering is wholly ours. Without a record
    // there is no metadata to resolve a number against, so a recordless page renders the
    // numbers as before rather than inventing names; LiveNavTestPage overrides with the real
    // one. See docs/limitations.md.
    public virtual string CurrentKey
    {
        get
        {
            if (_currentKeyFields == null || _currentKeyFields.Length == 0) return string.Empty;
            return string.Join(", ", _currentKeyFields);
        }
    }
}

/// <summary>Minimal ITestField implementation — all reads return safe defaults.</summary>
internal sealed class MockITestField : ITestField
{
    private string _value = string.Empty;

    public string Value         { get => _value; set => _value = value; }
    public string Name          => string.Empty;
    public string Caption       => string.Empty;
    public NavType FieldType    => NavType.Text;
    // 0/0/0 and the empty GetValidationError below are FAITHFUL here, not the #2900/#3009
    // hardcoding that was fixed on the live controls — stated rather than left bare, because
    // they read identically. This class is only ever built by MockITestPage.GetField, the
    // degraded path for a page with no live RunnerPageInstance (see
    // TestPageClientConstructionRule, which warns on stderr when a handle site lands here).
    // Its Value setter is a plain string store that runs no AL: no OnValidate, no record
    // write, nothing that can refuse. So there is genuinely never anything to count, and a
    // ledger here would only ever report the same 0 with more machinery.
    public int    ValidationErrorCount        => 0;
    public long   LastUsedValidationErrorId   => 0;
    public long   MaxValidationErrorId        => 0;
    public object? ObjectValue               => _value;
    public int    OptionCount                => 0;
    public bool   Enabled                   => true;
    public bool   Editable                  => true;
    public bool   Visible                   => true;
    public bool   HideValue                 => false;
    public bool   ShowMandatory             => false;

    public string GetValidationError(int index)   => string.Empty;
    public void   Activate()                      { }

    public void   Lookup()                        { }
    public void   Lookup(NavDataSet dataSet)      { }
    public void   AssistEdit()                    { }
    public void   Drilldown()                     { }
    public void   Invoke()                        { }
    public string ValueToString(object? value)    => value?.ToString() ?? string.Empty;
    public string GetOption(int index)            => string.Empty;
}

/// <summary>Minimal ITestAction implementation — Invoke is a no-op.</summary>
internal sealed class MockITestAction : ITestAction
{
    public void Invoke()         { }
    public bool Visible          => true;
    public bool Enabled          => true;
}

/// <summary>
/// Dispatches an action against a pageextension's own OnAction trigger when there is no
/// live RunnerPageInstance for the base page to route LiveNavTestAction through (issue
/// #1923 — see RunnerPageInstance.TryRaiseExtensionOnlyAction's remarks for why that
/// happens and what it can and cannot faithfully do). Falls back to a silent no-op, exactly
/// matching MockITestAction, when no compiled pageextension actually owns this action id —
/// an id belonging to the (unbuildable) precompiled base page itself, the pre-existing,
/// narrower gap this deliberately leaves alone rather than expanding scope.
/// </summary>
internal sealed class ExtensionOnlyTestAction : ITestAction
{
    private readonly LiveNavTestPage _testPage;
    private readonly object _owner;
    private readonly NavRecord _record;
    private readonly int _pageId;
    private readonly int _actionId;

    public ExtensionOnlyTestAction(LiveNavTestPage testPage, object owner, NavRecord record, int pageId, int actionId)
    {
        _testPage = testPage;
        _owner = owner;
        _record = record;
        _pageId = pageId;
        _actionId = actionId;
    }

    public void Invoke()
    {
        _testPage.SaveCurrentRow();
        RunnerPageInstance.TryRaiseExtensionOnlyAction(_owner, _record, _pageId, _actionId);
    }

    public bool Visible => true;
    public bool Enabled => true;
}

/// <summary>
/// Minimal ITestPart implementation.
/// ITestPart extends ITestPage + ITestFilter + IDisposable, so this derives
/// from MockITestPage which already implements all required members.
/// </summary>
internal sealed class MockITestPart : MockITestPage, ITestPart
{
    public bool Enabled => true;
    public bool Visible => true;
}
