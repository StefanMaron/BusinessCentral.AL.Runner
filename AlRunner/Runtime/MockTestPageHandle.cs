using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Mock for NavTestPageHandle — the BC type emitted for TestPage variables in test codeunits.
///
/// BC generates: new NavTestPageHandle(this, pageId)
/// Rewriter transforms to: new MockTestPageHandle(pageId)
///
/// Supports:
/// - ALOpenEdit(), ALOpenView(), ALOpenNew(), ALClose() — lifecycle no-ops
/// - ALTrap() — marks page as expecting modal open (no-op)
/// - GetField(fieldHash) — returns MockTestPageField for value get/set
/// - GetBuiltInAction(FormResult) — returns MockTestPageAction for OK/Cancel/Close
/// - ModalResult — tracks the FormResult set by action invocation (OK/Cancel)
/// </summary>
public class MockTestPageHandle
{
    public int PageId { get; }

    private readonly Dictionary<int, MockTestPageField> _fields = new();
    private readonly Dictionary<int, MockTestPageHandle> _parts = new();

    /// <summary>
    /// The modal result set by invoking a built-in action (OK, Cancel, etc.).
    /// Defaults to LookupOK (3) — same as BC when the handler completes without
    /// explicitly invoking Cancel.
    /// </summary>
    public FormResult ModalResult { get; set; } = FormResult.LookupOK;

    public MockTestPageHandle() { }

    public MockTestPageHandle(int pageId)
    {
        PageId = pageId;
    }

    // Lifecycle methods — no-ops in standalone mode
    public void ALOpenEdit() { }
    public void ALOpenView() { }
    public void ALOpenNew() { }
    public void ALClose() { }
    public void ALTrap() { }
    public void ALNew() { }
    public void ClearReference() { }

    /// <summary>
    /// Returns the page caption. Stub returns "TestPage" since the runner
    /// does not have page metadata infrastructure.
    /// </summary>
    public string ALCaption => "TestPage";

    /// <summary>
    /// Whether the page is editable. Stub returns true.
    /// BC emits <c>tP.Target.ALEditable</c> as a property access.
    /// </summary>
    public bool ALEditable => true;

    /// <summary>
    /// Returns the number of validation errors on the page. Stub returns 0.
    /// BC emits <c>tP.Target.ALValidationErrorCount()</c>.
    /// </summary>
    public int ALValidationErrorCount() => 0;

    /// <summary>
    /// Navigates to the first record on the page. Stub returns true.
    /// </summary>
    public bool ALFirst() => true;

    /// <summary>
    /// Navigates to a specific record on the page. Standalone mode does not bind
    /// page buffers, so this succeeds without changing state.
    /// </summary>
    public bool ALGoToRecord(MockRecordHandle rec) => true;
    public bool ALGoToRecord(DataError errorLevel, MockRecordHandle rec) => ALGoToRecord(rec);

    /// <summary>
    /// Moves to the next row. TestPage.Next() is boolean in generated code.
    /// </summary>
    public bool ALNext() => false;
    public bool ALNext(int steps) => false;

    /// <summary>
    /// Navigates to the last record on the page. Stub returns false (empty page).
    /// BC emits <c>tP.Target.ALLast()</c>.
    /// </summary>
    public bool ALLast() => false;

    /// <summary>
    /// Navigates to the previous record on the page. Stub returns false (empty page).
    /// BC emits <c>tP.Target.ALPrevious()</c>.
    /// </summary>
    public bool ALPrevious() => false;

    /// <summary>
    /// Expands or collapses a tree node on the page. No-op in standalone mode.
    /// BC emits <c>tP.Target.ALExpand(bool)</c>.
    /// </summary>
    public void ALExpand(bool expand) { }

    /// <summary>
    /// Returns a MockRecordHandle representing the underlying record of the page.
    /// Stub returns an unbound empty record handle (table 0). In BC, GetRecord
    /// returns the page's source table record, but we lack source-table metadata
    /// here. Using 0 avoids polluting the global store under a wrong table ID.
    /// BC emits <c>tP.Target.ALGetRecord()</c> for <c>TestPage.GetRecord(var Rec)</c>.
    /// </summary>
    public MockRecordHandle ALGetRecord() => new MockRecordHandle(0);

    /// <summary>
    /// Navigates to the record matching the given key values. Stub returns true.
    /// BC emits: ALGoToKey(DataError.TrapError, ALCompiler.ToNavValue(...))
    /// </summary>
    public bool ALGoToKey(DataError errorLevel, params NavValue[] keyValues) => true;

    /// <summary>
    /// Returns a filter object for the TestPage. BC emits:
    ///   tP.ALFilter.ALSetFilter(fieldNo, filterValue)
    /// </summary>
    public MockTestPageFilter ALFilter { get; } = new();

    /// <summary>
    /// Returns a MockTestPageField for the given field hash.
    /// BC generates field hashes (not field IDs) for TestPage field access.
    /// The field stores values in memory for get/set assertions.
    /// </summary>
    public MockTestPageField GetField(int fieldHash)
    {
        if (!_fields.TryGetValue(fieldHash, out var field))
        {
            field = new MockTestPageField(fieldHash);
            _fields[fieldHash] = field;
        }
        return field;
    }

    /// <summary>
    /// Returns a nested part handle for subpages such as lines on a card page.
    /// </summary>
    public MockTestPageHandle GetPart(int partHash)
    {
        if (!_parts.TryGetValue(partHash, out var part))
        {
            part = new MockTestPageHandle(PageId);
            _parts[partHash] = part;
        }
        return part;
    }

    /// <summary>
    /// Returns a MockTestPageAction for a custom page action.
    /// BC emits <c>tP.Target.GetAction(actionHash).ALInvoke()</c> for
    /// <c>TestPage.MyAction.Invoke()</c>. Custom actions in standalone mode
    /// are no-ops — the returned action's ALInvoke() does nothing.
    /// </summary>
    public MockTestPageAction GetAction(int actionHash)
    {
        return new MockTestPageAction();
    }

    /// <summary>
    /// Returns a MockTestPageAction for built-in actions (OK, Cancel, Close, etc.).
    /// BC casts FormResult enum values: GetBuiltInAction((FormResult)1) for OK.
    /// The action is linked back to this handle so ALInvoke() can set the ModalResult.
    /// </summary>
    public MockTestPageAction GetBuiltInAction(object formResult)
    {
        var fr = (FormResult)formResult;
        return new MockTestPageAction(this, fr);
    }
}

/// <summary>
/// Mock for TestPage field access. BC generates:
///   tP.GetField(fieldHash).ALSetValue(this.Session, value)
///   tP.GetField(fieldHash).ALValue
///
/// Stores the last set value and returns it via the settable ALValue property.
/// The backing store is <c>object?</c> so both <see cref="NavValue"/> instances
/// and raw CLR values (assigned through the setter) are supported.
/// </summary>
public class MockTestPageField
{
    private readonly int _fieldHash;
    private object? _value;

    public MockTestPageField(int fieldHash)
    {
        _fieldHash = fieldHash;
        _value = new NavText("");
    }

    /// <summary>
    /// Set the field value. BC passes (session, navValue) — session is null in standalone mode.
    /// </summary>
    public void ALSetValue(object? session, NavValue value)
    {
        _value = value;
    }

    public void ALSetValue(object? session, object? value)
    {
        _value = value;
    }

    /// <summary>
    /// Get or set the current field value.
    /// The getter returns the last value written via <see cref="ALSetValue"/> or the setter.
    /// The value may be a <see cref="NavValue"/>, a raw CLR type, or <c>null</c>.
    /// </summary>
    public object? ALValue
    {
        get => _value;
        set => _value = value;
    }

    /// <summary>
    /// ALCaption — returns the field caption. Stub: empty string.
    /// BC emits <c>tP.GetField(hash).ALCaption</c> for TestPage field Caption reads.
    /// </summary>
    public NavText ALCaption => new NavText("");

    /// <summary>
    /// ALVisible — whether the field is visible on the page. Stub: always true.
    /// BC emits <c>tP.GetField(hash).ALVisible()</c> as a method call for reads.
    /// </summary>
    public bool ALVisible() => true;

    /// <summary>
    /// ALEditable — whether the field is editable on the page. Stub: always true.
    /// BC emits <c>tP.GetField(hash).ALEditable()</c> as a method call for reads.
    /// </summary>
    public bool ALEditable() => true;

    /// <summary>
    /// ALEnabled — whether the field is enabled on the page. Stub: always true.
    /// </summary>
    public bool ALEnabled() => true;

    /// <summary>
    /// Convert the current field value to decimal for test assertions.
    /// </summary>
    public decimal ALAsDecimal() => AlCompat.ObjectToDecimal(_value);

    /// <summary>
    /// ALLookup — triggers the lookup action on the field. No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALLookup()</c>.
    /// </summary>
    public void ALLookup() { }

    /// <summary>
    /// ALDrilldown — triggers the drilldown action on the field. No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALDrilldown()</c>.
    /// </summary>
    public void ALDrilldown() { }

    /// <summary>
    /// ALActivate — focuses/activates the field on the page. No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALActivate()</c>.
    /// </summary>
    public void ALActivate() { }

    /// <summary>
    /// ALAssistEdit — triggers the assist-edit action on the field. No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALAssistEdit()</c>.
    /// </summary>
    public void ALAssistEdit() { }

    /// <summary>
    /// ALInvoke — invokes the field action. No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALInvoke()</c> for TestField.Invoke().
    /// </summary>
    public void ALInvoke() { }

    /// <summary>
    /// ALHideValue — hides or shows the field value. No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALHideValue(bool)</c>.
    /// </summary>
    public void ALHideValue(bool hide) { }

    /// <summary>
    /// ALShowMandatory — marks the field as mandatory (or not). No-op in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALShowMandatory(bool)</c>.
    /// </summary>
    public void ALShowMandatory(bool show) { }

    /// <summary>
    /// ALAsBoolean — converts the stored field value to bool.
    /// BC emits <c>tP.GetField(hash).ALAsBoolean()</c> for TestField.AsBoolean().
    /// </summary>
    public bool ALAsBoolean() => AlCompat.ObjectToBoolean(_value);

    /// <summary>
    /// ALAsInteger — converts the stored field value to int.
    /// BC emits <c>tP.GetField(hash).ALAsInteger()</c> for TestField.AsInteger().
    /// </summary>
    public int ALAsInteger() => (int)AlCompat.ObjectToDecimal(_value);

    /// <summary>
    /// ALAsDate — returns the stored field value as a NavDate.
    /// BC emits <c>tP.GetField(hash).ALAsDate()</c> for TestField.AsDate().
    /// </summary>
    public NavDate ALAsDate()
    {
        if (_value is NavDate d) return d;
        if (_value is MockVariant mv && mv.Value is NavDate mvd) return mvd;
        return NavDate.Default;
    }

    /// <summary>
    /// ALAsTime — returns the stored field value as a NavTime.
    /// BC emits <c>tP.GetField(hash).ALAsTime()</c> for TestField.AsTime().
    /// </summary>
    public NavTime ALAsTime()
    {
        if (_value is NavTime t) return t;
        if (_value is MockVariant mv && mv.Value is NavTime mvt) return mvt;
        return NavTime.Default;
    }

    /// <summary>
    /// ALAsDateTime — returns the stored field value as a NavDateTime.
    /// BC emits <c>tP.GetField(hash).ALAsDateTime()</c> for TestField.AsDateTime().
    /// </summary>
    public NavDateTime ALAsDateTime()
    {
        if (_value is NavDateTime dt) return dt;
        if (_value is MockVariant mv && mv.Value is NavDateTime mvdt) return mvdt;
        return NavDateTime.Default;
    }

    /// <summary>
    /// ALAssertEquals — asserts that the stored field value equals the expected value.
    /// Throws if the values differ (string comparison via AlCompat.Format).
    /// BC emits <c>tP.GetField(hash).ALAssertEquals(session, expected)</c>.
    /// </summary>
    public void ALAssertEquals(object? session, object? expected)
    {
        var actual = AlCompat.Format(_value);
        var exp = AlCompat.Format(expected);
        if (actual != exp)
            throw new InvalidOperationException(
                $"Assert.AreEqual failed. Expected:<{exp}>. Actual:<{actual}>.");
    }

    /// <summary>Single-arg overload for callers that omit the session argument.</summary>
    public void ALAssertEquals(object? expected)
        => ALAssertEquals(null, expected);

    /// <summary>
    /// ALValidationErrorCount — returns the number of validation errors on the field.
    /// Stub: no validation errors exist in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALValidationErrorCount()</c>.
    /// </summary>
    public int ALValidationErrorCount() => 0;

    /// <summary>
    /// ALGetValidationError — returns the validation error text at the given index.
    /// Stub: returns empty string (no errors in standalone mode).
    /// BC emits <c>tP.GetField(hash).ALGetValidationError(index)</c>.
    /// </summary>
    public NavText ALGetValidationError(int index) => NavText.Empty;

    /// <summary>
    /// ALOptionCount — returns the number of options for an option field.
    /// Stub: returns 0 (no option metadata available in standalone mode).
    /// BC emits <c>tP.GetField(hash).ALOptionCount()</c>.
    /// </summary>
    public int ALOptionCount() => 0;

    /// <summary>
    /// ALGetOption — returns the current option value as an integer index.
    /// Reads the integer representation of the stored value.
    /// BC emits <c>tP.GetField(hash).ALGetOption()</c>.
    /// </summary>
    public int ALGetOption() => (int)AlCompat.ObjectToDecimal(_value);
}

/// <summary>
/// Mock for TestPage built-in actions (OK, Cancel, Close).
/// BC generates: tP.GetBuiltInAction((FormResult)1).ALInvoke()
///
/// When ALInvoke() is called, sets the parent MockTestPageHandle's ModalResult
/// to the corresponding FormResult. This allows RunModal interception to return
/// the correct result based on whether the handler invoked OK or Cancel.
/// </summary>
public class MockTestPageAction
{
    private readonly MockTestPageHandle? _parent;
    private readonly FormResult _result;

    /// <summary>Parameterless ctor for backward compat (non-modal usage).</summary>
    public MockTestPageAction() { _result = FormResult.LookupOK; }

    /// <summary>Create an action linked to a TestPage handle with a specific result.</summary>
    public MockTestPageAction(MockTestPageHandle parent, FormResult result)
    {
        _parent = parent;
        _result = result;
    }

    public void ALInvoke()
    {
        if (_parent != null)
        {
            // Map the "page action" FormResult to the "modal return" FormResult.
            // BC maps OK (1) -> LookupOK (3), Cancel (2) -> LookupCancel (4).
            _parent.ModalResult = _result switch
            {
                FormResult.OK => FormResult.LookupOK,
                FormResult.Cancel => FormResult.LookupCancel,
                _ => _result
            };
        }
    }
}

/// <summary>
/// Mock for TestPage.Filter property. BC emits:
///   tP.ALFilter.ALSetFilter(fieldNo, filterValue)
///
/// Tracks per-field filter expressions in memory so that <see cref="ALGetFilter"/>
/// can return the last value set for a given field.
/// </summary>
public class MockTestPageFilter
{
    private readonly Dictionary<int, string> _filters = new();

    /// <summary>
    /// Sets a filter on the given field.
    /// BC emits: ALSetFilter(fieldNo, filterExpression)
    /// </summary>
    public void ALSetFilter(int fieldNo, string filterExpression)
    {
        _filters[fieldNo] = filterExpression ?? "";
    }

    /// <summary>
    /// Returns the last filter set for a field.
    /// </summary>
    public string ALGetFilter(int fieldNo)
    {
        return _filters.TryGetValue(fieldNo, out var filter) ? filter : "";
    }
}
