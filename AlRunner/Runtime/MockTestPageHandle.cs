using System.Reflection;
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
/// - GetAction(hash) — returns MockTestPageAction that dispatches to the compiled OnAction trigger
/// - ModalResult — tracks the FormResult set by action invocation (OK/Cancel)
/// </summary>
public class MockTestPageHandle
{
    public int PageId { get; }

    private readonly Dictionary<int, MockTestPageField> _fields = new();
    private readonly Dictionary<int, MockTestPageHandle> _parts = new();

    // ── Custom action dispatch ────────────────────────────────────────────────
    // Lazily created page instance (Page{N}) used to invoke compiled OnAction triggers.
    private object? _pageInstance;
    private MockRecordHandle? _currentRecord;
    private readonly Dictionary<int, MethodInfo> _actionMethodCache = new();
    private bool _actionCacheBuilt;

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

    private bool _editable = true;

    public void ALOpenEdit() { _editable = true; }
    public void ALOpenView() { _editable = false; }
    public void ALOpenNew() { _editable = true; }
    public void ALClose() { }
    public void ALTrap() { HandlerRegistry.RegisterTrap(PageId, this); }
    public void ALNew() { }
    public void ClearReference() { }

    /// <summary>
    /// Returns the page caption. Stub returns "TestPage" since the runner
    /// does not have page metadata infrastructure.
    /// </summary>
    public string ALCaption => "TestPage";

    /// <summary>
    /// Whether the page is editable. Set by ALOpenEdit/ALOpenView/ALOpenNew.
    /// BC emits <c>tP.Target.ALEditable</c> as a property access.
    /// </summary>
    public bool ALEditable => _editable;

    /// <summary>
    /// Returns the number of validation errors on the page. Stub returns 0.
    /// BC emits <c>tP.Target.ALValidationErrorCount()</c>.
    /// </summary>
    public int ALValidationErrorCount() => 0;

    // ── TestRequestPage-specific methods ────────────────────────────────────────

    /// <summary>
    /// Triggers preview of the report. Returns a no-op action.
    /// BC emits <c>tP.Target.ALPreview().ALInvoke()</c> — returns an Action.
    /// </summary>
    public MockTestPageAction ALPreview() => new MockTestPageAction();

    /// <summary>
    /// Triggers printing of the report. Returns a no-op action.
    /// BC emits <c>tP.Target.ALPrint().ALInvoke()</c> — returns an Action.
    /// </summary>
    public MockTestPageAction ALPrint() => new MockTestPageAction();

    /// <summary>
    /// Saves the report as PDF. No-op stub in standalone mode.
    /// BC emits <c>tP.Target.ALSaveAsPdf(fileName)</c>.
    /// </summary>
    public void ALSaveAsPdf(NavText fileName) { }
    public void ALSaveAsPdf(string fileName) { }

    /// <summary>
    /// Saves the report as Excel. No-op stub in standalone mode.
    /// BC emits <c>tP.Target.ALSaveAsExcel(fileName)</c>.
    /// </summary>
    public void ALSaveAsExcel(NavText fileName) { }
    public void ALSaveAsExcel(string fileName) { }

    /// <summary>
    /// Saves the report as Word. No-op stub in standalone mode.
    /// BC emits <c>tP.Target.ALSaveAsWord(fileName)</c>.
    /// </summary>
    public void ALSaveAsWord(NavText fileName) { }
    public void ALSaveAsWord(string fileName) { }

    /// <summary>
    /// Saves the report as XML. No-op stub in standalone mode.
    /// BC emits <c>tP.Target.ALSaveAsXml(reportFileName, dataFileName)</c>.
    /// </summary>
    public void ALSaveAsXml(NavText reportFileName, NavText dataFileName) { }
    public void ALSaveAsXml(string reportFileName, string dataFileName) { }

    /// <summary>
    /// Schedules the report. Returns a no-op action.
    /// BC emits <c>tP.Target.ALSchedule().ALInvoke()</c> — returns an Action.
    /// </summary>
    public MockTestPageAction ALSchedule() => new MockTestPageAction();

    /// <summary>
    /// Finds the first/next/previous field matching the given value. Stubs return false.
    /// Additional MockTestPageField-typed overloads handle BC TestPage field references
    /// passed directly (vs raw int hashes covered by the int overloads added by #689).
    /// </summary>
    public bool ALFindFirstField(MockTestPageField field, NavValue value) => false;
    public bool ALFindFirstField(MockTestPageField field, object? value) => false;
    public bool ALFindFirstField(DataError errorLevel, MockTestPageField field, NavValue value) => false;
    public bool ALFindFirstField(DataError errorLevel, MockTestPageField field, object? value) => false;

    public bool ALFindNextField(MockTestPageField field, NavValue value) => false;
    public bool ALFindNextField(MockTestPageField field, object? value) => false;
    public bool ALFindNextField(DataError errorLevel, MockTestPageField field, NavValue value) => false;
    public bool ALFindNextField(DataError errorLevel, MockTestPageField field, object? value) => false;

    public bool ALFindPreviousField(MockTestPageField field, NavValue value) => false;
    public bool ALFindPreviousField(MockTestPageField field, object? value) => false;
    public bool ALFindPreviousField(DataError errorLevel, MockTestPageField field, NavValue value) => false;
    public bool ALFindPreviousField(DataError errorLevel, MockTestPageField field, object? value) => false;

    /// <summary>
    /// Navigates to the first record on the page. Stub returns true.
    /// </summary>
    public bool ALFirst() => true;

    /// <summary>
    /// Navigates to a specific record on the page. Stores the record so that when
    /// a custom action's OnAction trigger fires, the page instance operates on
    /// this record instead of its default empty one.
    /// </summary>
    public bool ALGoToRecord(MockRecordHandle rec)
    {
        _currentRecord = rec;
        LinkRecToPageInstance(rec);
        return true;
    }

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
    /// Returns whether the node is currently expanded. Stub returns false.
    /// BC emits <c>tP.Target.GetPart(hash).ALIsExpanded</c> as a property access
    /// (not a method call) for <c>TestPart.IsExpanded()</c>.
    /// </summary>
    public bool ALIsExpanded => false;

    /// <summary>
    /// Returns whether the part is enabled. Stub returns true.
    /// BC emits <c>tP.Target.GetPart(hash).ALEnabled()</c> for <c>TestPart.Enabled()</c>.
    /// Note: ALEditable is a page-handle property; ALEnabled is the part-level method.
    /// </summary>
    public bool ALEnabled() => true;

    /// <summary>
    /// Returns whether the part is visible. Stub returns true.
    /// BC emits <c>tP.Target.GetPart(hash).ALVisible()</c> for <c>TestPart.Visible()</c>.
    /// </summary>
    public bool ALVisible() => true;

    /// <summary>
    /// Returns the text of the nth validation error on the part. Stub returns empty string.
    /// BC emits <c>tP.Target.GetPart(hash).ALGetValidationError(n)</c> for
    /// <c>TestPart.GetValidationError(n)</c>.
    /// </summary>
    public string ALGetValidationError(int index) => string.Empty;
    public string ALGetValidationError(DataError errorLevel, int index) => ALGetValidationError(index);

    /// <summary>
    /// Finds the first record where the field (by hash) equals value. Stub returns true.
    /// BC emits <c>tP.Target.GetPart(hash).ALFindFirstField(DataError, int fieldHash, value)</c>
    /// for <c>TestPart.FindFirstField(field, value)</c>.
    /// </summary>
    public bool ALFindFirstField(int fieldHash, object value) => true;
    public bool ALFindFirstField(DataError errorLevel, int fieldHash, object value) => true;

    /// <summary>
    /// Finds the next record where the field (by hash) equals value. Stub returns false (end of set).
    /// BC emits <c>tP.Target.GetPart(hash).ALFindNextField(DataError, int fieldHash, value)</c>
    /// for <c>TestPart.FindNextField(field, value)</c>.
    /// </summary>
    public bool ALFindNextField(int fieldHash, object value) => false;
    public bool ALFindNextField(DataError errorLevel, int fieldHash, object value) => false;

    /// <summary>
    /// Finds the previous record where the field (by hash) equals value. Stub returns false (end of set).
    /// BC emits <c>tP.Target.GetPart(hash).ALFindPreviousField(DataError, int fieldHash, value)</c>
    /// for <c>TestPart.FindPreviousField(field, value)</c>.
    /// </summary>
    public bool ALFindPreviousField(int fieldHash, object value) => false;
    public bool ALFindPreviousField(DataError errorLevel, int fieldHash, object value) => false;

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
    /// <c>TestPage.MyAction.Invoke()</c>.
    /// Looks up the compiled <c>OnAction</c> trigger in the page class and returns
    /// an action that delegates to it, operating on the record set via GoToRecord.
    /// Falls back to a no-op action if the page class or method is not found.
    /// </summary>
    public MockTestPageAction GetAction(int actionHash)
    {
        EnsurePageInstance();
        if (_pageInstance == null) return new MockTestPageAction();

        EnsureActionCache();
        if (!_actionMethodCache.TryGetValue(actionHash, out var method))
            return new MockTestPageAction();

        var page = _pageInstance;
        return new MockTestPageAction(() => method.Invoke(page, null));
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

    // ── Page instance lifecycle ───────────────────────────────────────────────

    /// <summary>
    /// Lazily creates the compiled page instance (<c>Page{N}</c>) from the loaded
    /// assembly so that custom action triggers can be dispatched to it.
    /// </summary>
    private void EnsurePageInstance()
    {
        if (_pageInstance != null) return;

        var pageTypeName = $"Page{PageId}";
        Type? pageType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                pageType = Array.Find(
                    asm.GetTypes(),
                    t => t.Name == pageTypeName &&
                         t.Namespace?.Contains("BusinessApplication") == true);
                if (pageType != null) break;
            }
            catch { }
        }

        if (pageType == null) return;
        _pageInstance = Activator.CreateInstance(pageType);

        if (_currentRecord != null)
            LinkRecToPageInstance(_currentRecord);
    }

    /// <summary>
    /// Sets the page instance's <c>Rec</c> backing field to the given record handle
    /// so that the compiled OnAction trigger operates on the correct in-memory record.
    /// The page Rec is an auto-property with a private backing field (<c>&lt;Rec&gt;k__BackingField</c>).
    /// </summary>
    private void LinkRecToPageInstance(MockRecordHandle rec)
    {
        if (_pageInstance == null) return;
        var field = _pageInstance.GetType()
            .GetField("<Rec>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(_pageInstance, rec);
    }

    /// <summary>
    /// Scans the page class for <c>*_a{N}_OnAction</c> methods, extracts the AL action
    /// name from each method name, and computes the BC member hash via
    /// <see cref="IdSpaceHelper.GetMemberId"/>. Populates <c>_actionMethodCache</c>.
    /// </summary>
    private void EnsureActionCache()
    {
        if (_actionCacheBuilt) return;
        _actionCacheBuilt = true;
        if (_pageInstance == null) return;

        foreach (var method in _pageInstance.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var actionName = ExtractActionName(method.Name);
            if (actionName == null) continue;

            int hash = IdSpaceHelper.GetMemberId(PageId, actionName);
            if (hash != 0)
                _actionMethodCache[hash] = method;
        }
    }

    /// <summary>
    /// Extracts the AL action name from a compiled method name.
    /// BC generates <c>{ActionName}_a{N}_OnAction</c> for each action trigger.
    /// For example: <c>SetFlag_a45_OnAction</c> → <c>"SetFlag"</c>.
    /// Returns <c>null</c> if the method name does not match the pattern.
    /// </summary>
    private static string? ExtractActionName(string methodName)
    {
        const string suffix = "_OnAction";
        if (!methodName.EndsWith(suffix, StringComparison.Ordinal)) return null;

        // Find the last "_a{digits}_OnAction" pattern
        int onActionIdx = methodName.Length - suffix.Length;
        int sepStart = -1;
        for (int i = onActionIdx - 2; i >= 0; i--)
        {
            if (methodName[i] == '_' && i + 1 < onActionIdx)
            {
                // Check if everything from i+2 to onActionIdx-1 is digits
                // and there's an 'a' at i+1
                if (i + 1 < onActionIdx && methodName[i + 1] == 'a')
                {
                    var between = methodName.AsSpan(i + 2, onActionIdx - i - 2);
                    if (!between.IsEmpty && between.IndexOfAnyExceptInRange('0', '9') < 0)
                    {
                        sepStart = i;
                        break;
                    }
                }
            }
        }

        if (sepStart <= 0) return null;
        return methodName[..sepStart];
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
    /// ALHideValue — hides the field value on the page. Returns true in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALHideValue()</c>; the return value is Boolean.
    /// </summary>
    public bool ALHideValue() => true;

    /// <summary>
    /// ALShowMandatory — marks the field as mandatory on the page. Returns true in standalone mode.
    /// BC emits <c>tP.GetField(hash).ALShowMandatory()</c>; the return value is Boolean.
    /// </summary>
    public bool ALShowMandatory() => true;

    /// <summary>
    /// ALAsBoolean — converts the stored field value to bool.
    /// Uses NavIndirectValueToBoolean to handle NavBoolean wrapper types.
    /// BC emits <c>tP.GetField(hash).ALAsBoolean()</c> for TestField.AsBoolean().
    /// </summary>
    public bool ALAsBoolean() => AlCompat.NavIndirectValueToBoolean(_value);

    /// <summary>
    /// ALAsInteger — converts the stored field value to int.
    /// Uses NavIndirectValueToInt32 to handle NavInteger wrapper types.
    /// BC emits <c>tP.GetField(hash).ALAsInteger()</c> for TestField.AsInteger().
    /// </summary>
    public int ALAsInteger() => AlCompat.NavIndirectValueToInt32(_value);

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
    /// Uses NavIndirectValueToInt32 to handle NavInteger wrapper types.
    /// BC emits <c>tP.GetField(hash).ALGetOption()</c>.
    /// </summary>
    public int ALGetOption() => AlCompat.NavIndirectValueToInt32(_value);
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
    private readonly Action? _customAction;

    /// <summary>Parameterless ctor for backward compat (non-modal / no-op usage).</summary>
    public MockTestPageAction() { _result = FormResult.LookupOK; }

    /// <summary>Create a custom page action that invokes the given delegate on ALInvoke().</summary>
    public MockTestPageAction(Action customAction)
    {
        _customAction = customAction;
        _result = FormResult.LookupOK;
    }

    /// <summary>Create an action linked to a TestPage handle with a specific result.</summary>
    public MockTestPageAction(MockTestPageHandle parent, FormResult result)
    {
        _parent = parent;
        _result = result;
    }

    /// <summary>
    /// BC emits: <c>action.ALEnabled()</c> for <c>TestAction.Enabled()</c>.
    /// Stub: always returns true in standalone mode.
    /// </summary>
    public bool ALEnabled() => true;

    /// <summary>
    /// BC emits: <c>action.ALVisible()</c> for <c>TestAction.Visible()</c>.
    /// Stub: always returns true in standalone mode.
    /// </summary>
    public bool ALVisible() => true;

    public void ALInvoke()
    {
        if (_customAction != null)
        {
            _customAction();
            return;
        }

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
/// Mock for TestPage.Filter property. BC emits the following patterns:
///   tP.ALFilter.ALSetFilter(fieldNo, filterValue)       — method
///   tP.ALFilter.ALGetFilter(fieldNo) → string           — method
///   tP.ALFilter.ALAscending = false                     — property setter
///   tP.ALFilter.ALAscending                             — property getter
///   tP.ALFilter.ALSetCurrentKey(DataError, fieldNo, ...) — method (DataError-prefixed)
///   tP.ALFilter.ALCurrentKey                            — property getter
///
/// Tracks per-field filter expressions, sort direction, and current key in
/// memory so assertions can verify what was last set.
/// </summary>
public class MockTestPageFilter
{
    private readonly Dictionary<int, string> _filters = new();
    private int[] _currentKeyFields = Array.Empty<int>();

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
    /// BC emits: ALGetFilter(fieldNo) → string
    /// </summary>
    public string ALGetFilter(int fieldNo)
    {
        return _filters.TryGetValue(fieldNo, out var filter) ? filter : "";
    }

    /// <summary>
    /// Sort direction. Defaults to true (ascending).
    /// BC compiles Filter.Ascending(false) → ALAscending = false (property assignment)
    /// and Filter.Ascending() → ALAscending (property read).
    /// </summary>
    public bool ALAscending { get; set; } = true;

    /// <summary>
    /// Sets the current sort key by field numbers.
    /// BC emits: ALSetCurrentKey(DataError, fieldNo, ...) — DataError is prepended
    /// for error-handling on the sort operation.
    /// </summary>
    public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos)
    {
        _currentKeyFields = fieldNos ?? Array.Empty<int>();
    }

    /// <summary>Legacy overload without DataError (defensive — kept for compat).</summary>
    public void ALSetCurrentKey(params int[] fieldNos)
    {
        _currentKeyFields = fieldNos ?? Array.Empty<int>();
    }

    /// <summary>
    /// Returns the current sort key as a comma-separated string of field numbers.
    /// Empty when no SetCurrentKey has been called.
    /// BC compiles Filter.CurrentKey() → ALCurrentKey (property read).
    /// </summary>
    public string ALCurrentKey
    {
        get
        {
            if (_currentKeyFields.Length == 0)
                return string.Empty;
            return string.Join(",", _currentKeyFields);
        }
    }
}
