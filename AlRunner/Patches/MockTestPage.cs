// MockTestPage — lightweight ITestPage / ITestField / ITestAction implementations
// for the runner's NavTestPage vtable fix.
//
// NavTestPageHandle_CreateTarget constructs a real NavTestPage via its internal
// 3-arg ctor passing a MockITestPage as the ITestPage.  Cecil IL rewrites in
// NclCecilRewrite ensure the runtime never calls out to the real TestPageClient
// or TestClientProxy.Proxy, so these mocks only need to satisfy the direct method
// calls NavTestPageBase.GetField / GetAction / GetDataItem make into them.
using System;
using System.Collections.Generic;
using System.Globalization;
using AlRunnerV2.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;

namespace AlRunnerV2;

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

    public ITestAction GetAction(int id)
    {
        if (!_actions.TryGetValue(id, out var a))
            _actions[id] = a = new MockITestAction();
        return a;
    }

    public ITestPart          GetPart(int id)                                           => new MockITestPart();
    public ITestAction        GetBuiltInAction(FormResult formResult)                   => new MockITestAction();
    public ITestFilter        GetDataItemFilter(string id)                              => this;
    public void               SetSelection(bool value)                                  { }
    public virtual void       InsertEmptyRow(bool beforeCurrent)                        { }
    public virtual bool       MoveNext()                                                => false;
    public virtual bool       MovePrevious()                                            => false;
    public virtual bool       MoveFirst()                                               => false;
    public virtual bool       MoveLast()                                                => false;
    public string             GetValidationError(int index)                             => string.Empty;
    public virtual bool       FindRowFromTableFieldValues(int[] f, object[] v, bool fw) => false;
    public virtual bool       FindRowFromControlFieldValue(int fId, object v, bool fw)  => false;
    public virtual object?    GetBookmark()                                             => null;
    public virtual bool       GoToBookmark(object bookmark)                             => false;
    public virtual object[]   GetTableFieldValues(int[] fieldIds)                       => Array.Empty<object>();
    public ITestAction        Edit()                                                    => new MockITestAction();
    public ITestAction        View()                                                    => new MockITestAction();
    public bool               Expand(bool doExpand)                                     => false;

    public int        ValidationErrorCount => 0;
    public FormResult FormResult           => FormResult.OK;
    public string     Name                 => string.Empty;
    public string     Caption              => string.Empty;
    public int        PageId               => 0;
    public Guid       FormHandle           => Guid.Empty;
    public virtual bool Creatable          => false;
    public bool       IsExpanded           => false;
    public bool       RuntimeEditable      => true;

    // ── ITestFilter (inherited via ITestPage) ─────────────────────────────

    public virtual void SetFilter(int fieldId, string filterValue) => _filters[fieldId] = filterValue;
    public IEnumerable<NavFilter> GetFilter() => Array.Empty<NavFilter>();
    public virtual string GetFilter(int fieldId) => _filters.TryGetValue(fieldId, out var v) ? v : string.Empty;
    public void   SetCurrentKeyFields(int[] fields) { _currentKeyFields = fields; }
    public int[]  GetCurrentKeyFields() => _currentKeyFields ?? Array.Empty<int>();

    public bool   Ascending
    {
        get => _ascending;
        set => _ascending = value;
    }

    public string CurrentKey
    {
        get
        {
            if (_currentKeyFields == null || _currentKeyFields.Length == 0) return string.Empty;
            return string.Join(", ", _currentKeyFields);
        }
    }
}

internal sealed class LiveNavTestPage : MockITestPage
{
    private readonly NavRecord _record;
    private readonly IReadOnlyDictionary<int, int> _controlIdToFieldNo;
    private readonly Dictionary<int, LiveNavTestField> _fields = new();
    private readonly bool _creatable;

    public LiveNavTestPage(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo)
        : this(record, controlIdToFieldNo, creatable: true) { }

    public LiveNavTestPage(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable)
    {
        _record = record;
        _controlIdToFieldNo = controlIdToFieldNo;
        _creatable = creatable;
    }

    // BC's NavTestPageBase.New() consults Creatable before inserting. The base mock returns
    // false (it has no backing record to insert into), but a LIVE test page does — so the
    // answer must come from the page's declared InsertAllowed rather than a hardcoded false,
    // which denied every TestPage.New() regardless of the page under test.
    public override bool Creatable => _creatable;

    public override bool IsOpened() => false;

    // TestPage.New() reaches ITestPage.InsertEmptyRow. BC's client model is "start a blank
    // row now, persist it once the cursor leaves it (or the page closes)" — the SetValue
    // calls in between write into the record buffer. The base mock no-ops, which silently
    // dropped every insert made through a TestPage; a LIVE page has a real record, so it
    // must initialise the buffer and remember to flush it.
    private bool _pendingNewRow;

    public override void InsertEmptyRow(bool beforeCurrent)
    {
        FlushPendingNewRow();   // starting a second row persists the first
        _record.ALInit();
        _pendingNewRow = true;
    }

    internal void FlushPendingNewRow()
    {
        if (!_pendingNewRow) return;
        _pendingNewRow = false;
        _record.ALInsertAsync(DataError.TrapError, false, false).GetAwaiter().GetResult();
    }

    // BC routes TestPage teardown through both Close() and Dispose() depending on whether
    // the AL test calls Close() explicitly or lets the variable go out of scope. Flush on
    // both so a New() is never silently discarded.
    public override void Close() => FlushPendingNewRow();
    public override void Dispose() => FlushPendingNewRow();

    public override ITestField GetField(int id)
    {
        var tableFieldNo = ToTableFieldNo(id);
        if (!_fields.TryGetValue(tableFieldNo, out var field))
            _fields[tableFieldNo] = field = new LiveNavTestField(_record, tableFieldNo);
        return field;
    }

    // Every cursor move leaves the in-progress new row, so it must be persisted first —
    // otherwise navigating away from a New() silently discards it.
    public override bool MoveFirst() { FlushPendingNewRow(); return _record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult(); }
    public override bool MoveLast() { FlushPendingNewRow(); return _record.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult(); }
    public override bool MoveNext() { FlushPendingNewRow(); return _record.ALNextAsync().GetAwaiter().GetResult() != 0; }
    public override bool MovePrevious() { FlushPendingNewRow(); return _record.ALNextAsync(-1).GetAwaiter().GetResult() != 0; }

    public override object? GetBookmark() => _record.ALGetPosition();

    public override bool GoToBookmark(object bookmark)
    {
        if (bookmark is not string position || string.IsNullOrEmpty(position)) return false;
        _record.ALSetPosition(position);
        return true;
    }

    public override object[] GetTableFieldValues(int[] fieldIds)
        => fieldIds.Select(fieldNo => ReadClientObject(fieldNo) ?? string.Empty).ToArray();

    public override bool FindRowFromControlFieldValue(int fieldId, object value, bool forward)
        => FindRowFromTableFieldValues(new[] { ToTableFieldNo(fieldId) }, new[] { value }, forward);

    public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)
    {
        if (fieldNos.Length != values.Length) return false;

        var original = _record.ALGetPosition();
        var hasCurrent = !string.IsNullOrEmpty(original);

        // Scan the WHOLE rowset, always starting from the first (or last, when searching
        // backward) row — never from wherever the page happens to be positioned. `forward`
        // is a direction, not "resume from the cursor": BC's client locates the requested
        // row anywhere in the rowset. Starting at the current row silently failed to find
        // any row BEHIND the cursor, so navigating C -> A returned false even though A is
        // on the page (tests/runner-extras/testpage-gotorecord GoToRecord_MovesBetweenRows).
        var hasRow = forward ? MoveFirst() : MoveLast();

        while (hasRow)
        {
            if (Matches(fieldNos, values)) return true;
            hasRow = forward ? MoveNext() : MovePrevious();
        }

        if (hasCurrent) _record.ALSetPosition(original);
        return false;
    }

    public override void SetFilter(int fieldId, string filterValue)
        => _record.ALSetFilter(ToTableFieldNo(fieldId), filterValue);

    public override string GetFilter(int fieldId)
        => _record.ALGetFilter(ToTableFieldNo(fieldId));

    private int ToTableFieldNo(int id)
        => _controlIdToFieldNo.TryGetValue(id, out var fieldNo) ? fieldNo : id;

    private bool Matches(int[] fieldNos, object[] values)
    {
        for (var i = 0; i < fieldNos.Length; i++)
            if (!ValuesEqual(ReadClientObject(fieldNos[i]), Unwrap(values[i])))
                return false;
        return true;
    }

    private object? ReadClientObject(int fieldNo) => Unwrap(_record.GetFieldValue(fieldNo));

    internal static object? Unwrap(object? value)
        => value is NavValue navValue ? navValue.ClientObject : value;

    private static bool ValuesEqual(object? left, object? right)
    {
        left = Unwrap(left);
        right = Unwrap(right);
        return Equals(left, right);
    }
}

internal sealed class LiveNavTestField : ITestField
{
    private readonly NavRecord _record;
    private readonly int _fieldNo;

    public LiveNavTestField(NavRecord record, int fieldNo)
    {
        _record = record;
        _fieldNo = fieldNo;
    }

    public string Value
    {
        get => Convert.ToString(ObjectValue, CultureInfo.InvariantCulture) ?? string.Empty;
        set => _record.SetFieldValue(_fieldNo, ALCompiler.ToNavValue(value));
    }

    public string Name => Caption;
    public string Caption => TryGetMetaFieldName() ?? $"Field {_fieldNo}";
    public NavType FieldType => TryGetMetaFieldType() ?? NavType.Text;
    public int ValidationErrorCount => 0;
    public long LastUsedValidationErrorId => 0;
    public long MaxValidationErrorId => 0;
    public object? ObjectValue => LiveNavTestPage.Unwrap(_record.GetFieldValue(_fieldNo));
    public int OptionCount => 0;
    public bool Enabled => true;
    public bool Editable => true;
    public bool Visible => true;
    public bool HideValue => false;
    public bool ShowMandatory => false;

    public string GetValidationError(int index) => string.Empty;
    public void Activate() { }
    public void Lookup() { }
    public void Lookup(NavDataSet dataSet) { }
    public void AssistEdit() { }
    public void Drilldown() { }
    public void Invoke() { }
    public string ValueToString(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    public string GetOption(int index) => string.Empty;

    private string? TryGetMetaFieldName()
    {
        return _record.MetaTable.TryGetFieldByNo(_fieldNo, out var field) ? field.FieldName : null;
    }

    private NavType? TryGetMetaFieldType()
    {
        return _record.MetaTable.TryGetFieldByNo(_fieldNo, out var field) ? field.FieldNavType : null;
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
/// Minimal ITestPart implementation.
/// ITestPart extends ITestPage + ITestFilter + IDisposable, so this derives
/// from MockITestPage which already implements all required members.
/// </summary>
internal sealed class MockITestPart : MockITestPage, ITestPart
{
    public bool Enabled => true;
    public bool Visible => true;
}
