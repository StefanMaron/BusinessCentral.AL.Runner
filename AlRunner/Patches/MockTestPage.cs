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
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;

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
    public virtual FormResult FormResult   => FormResult.OK;
    public string     Name                 => string.Empty;
    public string     Caption              => string.Empty;
    public virtual int PageId            => 0;
    public virtual Guid FormHandle         => Guid.Empty;
    public virtual bool Creatable          => false;
    public bool       IsExpanded           => false;
    public virtual bool RuntimeEditable    => true;

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

internal class LiveNavTestPage : MockITestPage
{
    private readonly NavRecord _record;
    private readonly IReadOnlyDictionary<int, int> _controlIdToFieldNo;
    private readonly Dictionary<int, LiveNavTestField> _fields = new();
    private readonly Dictionary<int, PageVariableTestField> _pageVariableFields = new();
    private readonly bool _creatable;
    // The live AL page object, when the runner could build one. Null for a page it did not
    // compile (no metadata to build a control tree from) — then only Rec-bound controls
    // resolve, which is all this class could ever do before.
    private readonly RunnerPageInstance? _page;

    // The ITreeObject every NavRecord on this page is constructed under, and the page's own
    // id — both needed to build a subpage part, which is another page over another table.
    private readonly object? _owner;
    private readonly int _pageId;
    private readonly Dictionary<int, ITestPart> _parts = new();

    public LiveNavTestPage(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo)
        : this(record, controlIdToFieldNo, creatable: true, page: null) { }

    public LiveNavTestPage(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable)
        : this(record, controlIdToFieldNo, creatable, page: null) { }

    public LiveNavTestPage(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page)
        : this(record, controlIdToFieldNo, creatable, page, owner: null, pageId: 0) { }

    public LiveNavTestPage(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page, object? owner, int pageId)
    {
        _record = record;
        _controlIdToFieldNo = controlIdToFieldNo;
        _creatable = creatable;
        _page = page;
        _owner = owner;
        _pageId = pageId;
    }

    internal NavRecord Record => _record;

    // BC reports these in NavInsertDeniedPermissionException and friends. Answering 0/""
    // (the mock's values) is what produced "Insert is not allowed. Page = , Id = 0" — an
    // error that named no page at all.
    public override int PageId => _pageId;

    // TestPage.Editable() reaches here (NavTestPage.ALEditable => TestPage.RuntimeEditable).
    // A constant true made every `CurrPage.Editable(false)` invisible to the test that was
    // written to check it.
    public override bool RuntimeEditable => _staticEditable;

    /// <summary>
    /// The subpage part hosted by <paramref name="controlId"/>, driven live over its own
    /// source table with the SubPageLink applied.
    ///
    /// Previously this handed back a bare MockITestPart whose Creatable is false, so BC's
    /// NavTestPageBase.ALNew() — which consults TestPage.Creatable — refused every insert
    /// made through a part with "New method failed because Insert is not allowed.
    /// Page = , Id = 0". A part that cannot be built now refuses by NAME rather than
    /// answering as an empty page that silently reports no rows and accepts no inserts.
    /// </summary>
    public override ITestPart GetPart(int controlId)
    {
        if (_parts.TryGetValue(controlId, out var cached)) return cached;

        var definition = _page?.TryGetPartDefinition(controlId)
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage part {controlId} (page {_pageId})",
                "testpage-part — the runner could not resolve this control to a subpage part"
                + (_page == null
                    ? "; no AL page object was built for the hosting page, so its part definitions "
                      + "are unavailable — see AlPageMetadataRegistry"
                    : "; the hosting page's metadata declares no part with this control id")
                + ". See docs/scope.md");

        var partPageId = definition.PagePartID;
        var built = _owner == null ? null : TestPageFactory.TryBuild(_owner, partPageId, out _);
        if (built == null)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage part {controlId} → page {partPageId}",
                "testpage-part — the part's own page could not be driven live "
                + "(no source table, or no runtime record type for it). See docs/scope.md");

        var part = new LiveNavTestPart(
            built.Record, RecordPatches.GetPageControlFieldMap(partPageId),
            RecordPatches.GetInsertAllowedForPage(partPageId), built.Page, _owner!, partPageId,
            parentRecord: _record, links: SubPageLinks(definition, partPageId));
        _parts[controlId] = part;
        return part;
    }

    // How the page was closed. BC's RunHandlerWithException reads this off the page right
    // after a [ModalPageHandler] returns, and it is what RunModal() reports back to the AL
    // that opened the page. The mock answers a constant OK, so a handler that cancelled was
    // indistinguishable from one that confirmed — every AL `if RunModal() = Action::OK`
    // took the OK branch regardless.
    private FormResult _formResult = FormResult.OK;

    public override FormResult FormResult => _formResult;

    /// <summary>
    /// The page's built-in OK/Cancel/LookupOK actions. Invoking one records how the page was
    /// closed; the base mock returned a no-op action, which is why Cancel() did nothing.
    ///
    /// Returning null for a result the page does not offer is LOAD-BEARING, not defensive.
    /// NavTestPageBase.GetBuiltInAction(OK) is implemented as
    /// FindBuiltInAction(FormResult.OK, FormResult.LookupOK): it asks the client for OK
    /// first and only falls through to LookupOK when the client answers NULL. Answering
    /// every result with an action made that fallthrough unreachable, so a page opened as a
    /// lookup still closed with plain OK — and AL that gates on the documented
    /// `if Picker.RunModal() <> Action::LookupOK then exit(false)` took the cancel branch
    /// even though the handler had picked a row and invoked OK.
    /// </summary>
    public override ITestAction GetBuiltInAction(FormResult formResult)
    {
        if (!Offers(formResult)) return null!;
        return new RecordingBuiltInAction(this, formResult);
    }

    /// <summary>
    /// Whether this page has the given built-in action at all. A page opened as a lookup
    /// closes with LookupOK/LookupCancel and has no plain OK/Cancel, and vice versa —
    /// exactly the distinction BC's own fallback pair encodes. Results outside those two
    /// pairs (Yes/No, Print, …) are left alone: this is about lookup-vs-normal closing,
    /// not a claim about which other built-ins a page has.
    /// </summary>
    private bool Offers(FormResult formResult)
    {
        if (_page == null) return true;
        bool lookup = _page.LookupMode;
        return formResult switch
        {
            FormResult.OK or FormResult.Cancel => !lookup,
            FormResult.LookupOK or FormResult.LookupCancel => lookup,
            _ => true,
        };
    }

    private sealed class RecordingBuiltInAction : ITestAction
    {
        private readonly LiveNavTestPage _page;
        private readonly FormResult _result;

        internal RecordingBuiltInAction(LiveNavTestPage page, FormResult result)
        {
            _page = page;
            _result = result;
        }

        /// <summary>
        /// Closing the page IS the commit point of the new-record flow. AL writes
        /// <c>Card.OpenNew(); Card.Name.SetValue(…); Card.OK().Invoke();</c> and then reads
        /// the table — so a row persisted only at Close/Dispose does not exist yet for every
        /// assertion in between, and the test reports a missing row rather than a late one.
        /// Cancel is the other half: it must abandon the row, not merely record a result.
        /// </summary>
        public void Invoke()
        {
            _page._formResult = _result;
            if (_result is FormResult.Cancel or FormResult.LookupCancel)
                _page.DiscardPendingNewRow();
            else
                _page.FlushRow();
        }

        public bool Visible => true;
        public bool Enabled => true;
    }

    private readonly Dictionary<int, ITestAction> _liveActions = new();

    /// <summary>
    /// The page action for <paramref name="actionId"/>, wired to the page's own OnAction
    /// trigger. The base mock returns a MockITestAction whose Invoke() is a literal no-op,
    /// so an invoked action silently did nothing and the test failed a step later
    /// complaining about the missing effect rather than about the action.
    /// </summary>
    public override ITestAction GetAction(int actionId)
    {
        if (_page == null) return base.GetAction(actionId);
        if (!_liveActions.TryGetValue(actionId, out var action))
            _liveActions[actionId] = action = new LiveNavTestAction(_page, actionId);
        return action;
    }

    /// <summary>
    /// The SubPageLink as (part field, parent field) pairs. Only FilterType.FIELD is a
    /// SubPageLink in the AL sense (<c>SubPageLink = ReportId = field(ReportId)</c>); CONST
    /// and FILTER links are a different, unimplemented shape and refuse loudly rather than
    /// silently leaving the part unfiltered — an unfiltered part shows other rows' children,
    /// which is a wrong answer, not a missing one.
    /// </summary>
    private static (int PartFieldNo, int ParentFieldNo)[] SubPageLinks(
        Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition definition, int partPageId)
    {
        var links = new List<(int, int)>();
        foreach (var link in definition.SubFormLink ?? new List<Microsoft.Dynamics.Nav.Types.Metadata.FilterDefinition>())
        {
            if (link.FilterType != Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD)
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage part → page {partPageId} SubPageLink ({link.FilterType})",
                    $"testpage-part-link — only FilterType.FIELD SubPageLinks are implemented; "
                    + $"this part links field {link.FieldID} by {link.FilterType} '{link.FilterValue}'. "
                    + "See docs/scope.md");
            if (!int.TryParse(link.FilterValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentFieldNo))
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage part → page {partPageId} SubPageLink",
                    $"testpage-part-link — a FIELD link's value must be the parent's field number, "
                    + $"but this one is '{link.FilterValue}'. See docs/scope.md");
            links.Add((link.FieldID, parentFieldNo));
        }
        return links.ToArray();
    }

    // BC's NavTestPageBase.New() consults Creatable before inserting. The base mock returns
    // false (it has no backing record to insert into), but a LIVE test page does — so the
    // answer must come from the page's declared InsertAllowed rather than a hardcoded false,
    // which denied every TestPage.New() regardless of the page under test.
    public override bool Creatable => _creatable;

    // Whether BC has opened this page. Set by the Cecil-rewritten NavTestPage.Open (via
    // RunnerTestPageState) and cleared on close.
    //
    // This has to be real state rather than a constant, because BOTH of BC's guards read
    // it and they want opposite answers at different moments:
    //   NavTestPageBase.Open()  throws NavTestPageAlreadyOpenException when it is true
    //   NavTestPageBase.Close() forwards to this class ONLY when it is true
    // In BC the two never conflict, because the page is attached during Open. The runner
    // attaches at construction (NavTestPageHandle.CreateTarget) and NclCecilRewrite keeps
    // that attachment across InternalClear, so a constant false silently disabled Close —
    // a row started with New() was then never persisted at Close, only at Dispose, which
    // is after the test's assertions have already read the table. See RunnerTestPageState.
    private bool _opened;

    /// <summary>
    /// Record that BC opened this page, in <paramref name="viewMode"/>.
    ///
    /// The mode is what <c>TestPage.Editable()</c> answers from. Real BC reports the page's
    /// STATIC editability there — the mode it was opened in, combined with the page's own
    /// <c>Editable</c> property — not whatever <c>CurrPage.Editable(…)</c> last set from a
    /// row trigger (corpus CU60687
    /// CurrPageEditable_TestPageGetterIgnoresTheRuntimeToggle, validated against a real
    /// service tier: a page whose OnAfterGetRecord flips CurrPage.Editable(false) still reads
    /// back Editable() = true). The live per-CONTROL properties are the mechanism that does
    /// follow the row; these are two different mechanisms and BC surfaces both.
    ///
    /// The page's declared Editable is read HERE, before OnOpenPage runs, so a runtime
    /// toggle cannot have moved it yet.
    /// </summary>
    internal void MarkOpened(Microsoft.Dynamics.Nav.Types.Metadata.ViewMode viewMode)
    {
        _opened = true;
        _staticEditable = viewMode != Microsoft.Dynamics.Nav.Types.Metadata.ViewMode.View
                          && (_page?.PageEditable ?? true);
    }

    private bool _staticEditable = true;

    /// <summary>Run the page's OnOpenPage — see RunnerTestPageState.MarkOpened.</summary>
    internal void RaiseOnOpenPage() => _page?.RaiseOnOpenPage();

    public override bool IsOpened() => _opened;

    // TestPage.New() reaches ITestPage.InsertEmptyRow. BC's client model is "start a blank
    // row now, persist it once the cursor leaves it (or the page closes)" — the SetValue
    // calls in between write into the record buffer. The base mock no-ops, which silently
    // dropped every insert made through a TestPage; a LIVE page has a real record, so it
    // must initialise the buffer and remember to flush it.
    private bool _pendingNewRow;

    public override void InsertEmptyRow(bool beforeCurrent)
    {
        FlushPendingNewRow();   // starting a second row persists the first

        // Ask the page to start the row, exactly as it would for a user: BC's NavForm.NewRecord
        // does ALInit, fills the linking fields in from the page's own filters, and raises
        // OnNewRecord. A filtered page is showing one parent's rows, so a row created on it
        // belongs to that parent — that is what makes Lines.New() on a subpage produce a line
        // already attached to its header.
        //
        // The runner used to do the first and last of those steps by hand and skip the middle,
        // so the row arrived with blank keys and the damage surfaced one step later: an
        // OnValidate looking its parent up found nothing, and the test failed naming a derived
        // field rather than the key that was never set.
        if (!(_page?.TryNewRecord(!beforeCurrent) ?? false))
        {
            // Record-only mode: no page to ask, so no filters and no trigger to run either.
            _record.ALInit();
        }

        _pendingNewRow = true;
    }

    internal void FlushPendingNewRow()
    {
        if (!_pendingNewRow) return;
        _pendingNewRow = false;
        // OnInsertRecord is the page's last word before the row exists, and its RETURN VALUE
        // is a veto — a page can refuse the insert outright. Running it and discarding the
        // answer would be worse than not running it: the row lands anyway, but now it also
        // carries whatever the trigger wrote on its way to saying no.
        if (_page != null && !_page.RaiseOnInsertRecord(false)) return;
        // runApplicationTrigger: true. Inserting a row from a page runs the table's OnInsert, the
        // same as Rec.Insert(true) — that trigger is where a table assigns its number series,
        // stamps its own derived fields, and enforces what it will not accept. Passing false
        // wrote a row the table had never agreed to.
        _record.ALInsertAsync(DataError.TrapError, true, false).GetAwaiter().GetResult();
    }

    /// <summary>Abandon an in-progress new row without writing it — how Cancel closes.</summary>
    internal void DiscardPendingNewRow() { _pendingNewRow = false; _pendingModify = false; }

    // The same client model as _pendingNewRow, for the other half of editing: a SetValue on an
    // EXISTING row writes into the record buffer, and the row is persisted when the cursor
    // leaves it or the page closes.
    //
    // Without this, every edit a TestPage made to an existing row was silently discarded. That
    // is worse than it sounds: the page keeps answering with the value that was set, so a test
    // that writes a field and reads it back through the page PASSES, and only a test that goes
    // to the table notices. Tests of the first shape were green while asserting nothing.
    private bool _pendingModify;

    /// <summary>A control wrote to the record. Called by the field, which owns no page state.</summary>
    internal void MarkEdited()
    {
        // A new row is already going to be written by FlushPendingNewRow; marking it modified
        // as well would try to Modify a row that does not exist yet.
        if (!_pendingNewRow) _pendingModify = true;
    }

    internal void FlushPendingModify()
    {
        if (!_pendingModify) return;
        _pendingModify = false;
        // OnModifyRecord vetoes exactly as OnInsertRecord does.
        if (_page != null && !_page.RaiseOnModifyRecord()) return;
        // ThrowError, not TrapError: a Modify that cannot be performed is something the user of
        // a real client would be told about. Trapping it turned "this page is not positioned on
        // a row" into an edit that appeared to succeed and quietly went nowhere.
        _record.ALModifyAsync(DataError.ThrowError, true).GetAwaiter().GetResult();
    }

    // Order matters at every flush point: an in-progress new row is finished by an Insert, an
    // edited existing row by a Modify, and only one of the two is ever pending.
    private void FlushRow() { FlushPendingNewRow(); FlushPendingModify(); }

    // BC routes TestPage teardown through both Close() and Dispose() depending on whether
    // the AL test calls Close() explicitly or lets the variable go out of scope. Flush on
    // both so a New() is never silently discarded.
    //
    // Parts flush with their host: an AL test closes the CARD, never the part, so a row
    // started with Card.Lines.New() has no other moment at which it could be persisted.
    public override void Close()
    {
        // OnQueryClosePage's veto is the one part of the close sequence the runner cannot
        // model: BC would leave the page open and hand control back to the user, which has no
        // meaning in a test that has already asked for the close. Refusing by name beats both
        // alternatives — closing anyway hides that the page objected, and hanging is worse.
        if (_page != null && !_page.RaiseOnClosePage(_formResult))
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage page {_pageId} — OnQueryClosePage",
                "testpage-close-veto — the page's OnQueryClosePage returned false, which in BC "
                + "leaves the page open awaiting the user. See docs/scope.md");
        FlushParts(); FlushRow(); _opened = false;
    }
    public override void Dispose() { FlushParts(); FlushRow(); }

    private void FlushParts()
    {
        foreach (var part in _parts.Values)
            if (part is LiveNavTestPage live) live.FlushRow();
    }

    public override ITestField GetField(int id)
    {
        // A control bound to a Rec field resolves against the record, as before.
        if (_controlIdToFieldNo.TryGetValue(id, out var tableFieldNo))
        {
            if (!_fields.TryGetValue(tableFieldNo, out var field))
                _fields[tableFieldNo] = field =
                    new LiveNavTestField(_record, tableFieldNo, _page, id, MarkEdited);
            return field;
        }

        // Otherwise it may be bound to a page VARIABLE — resolvable only through the page's
        // own binding table (NavForm.SourceExpressions).
        var expression = _page?.TryGetSourceExpression(id);
        if (expression != null)
        {
            if (!_pageVariableFields.TryGetValue(id, out var pageField))
                _pageVariableFields[id] = pageField = new PageVariableTestField(_page!, expression, id);
            return pageField;
        }

        // Neither. Historically `id` was handed to the record as a FIELD NUMBER, which
        // produced "The supplied field number '<hash>' cannot be found in the '<table>'
        // table" — a control-name hash reported as a missing field, blaming the table for
        // the runner's own inability to resolve the control. Say what actually happened.
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestPage control {id}",
            "testpage-control-binding — this control is bound neither to a field of the page's "
            + $"source table nor to a page variable the runner could resolve (table "
            + $"{_record.MetaTable?.TableName ?? "?"}"
            + (_page == null
                ? "; no AL page object was built for this page, so page-variable-bound controls "
                  + "cannot be resolved — see AlPageMetadataRegistry"
                : "; the page object has no source expression for this control id")
            + "). See docs/scope.md");
    }

    // Every cursor move leaves the in-progress new row, so it must be persisted first —
    // otherwise navigating away from a New() silently discards it. Parts flush too: moving
    // the parent re-links every part to a different row, so a row started in a part must be
    // persisted while the link that stamped its key is still the current one.
    public override bool MoveFirst() { FlushParts(); FlushRow(); return Loaded(_record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult()); }
    public override bool MoveLast() { FlushParts(); FlushRow(); return Loaded(_record.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult()); }
    public override bool MoveNext() { FlushParts(); FlushRow(); return Loaded(_record.ALNextAsync().GetAwaiter().GetResult() != 0); }
    public override bool MovePrevious() { FlushParts(); FlushRow(); return Loaded(_record.ALNextAsync(-1).GetAwaiter().GetResult() != 0); }

    /// <summary>
    /// A row just became the page's current row — run the page's OnAfterGetRecord, exactly
    /// as BC does after every load. That trigger is where a page derives its per-row state
    /// (the variable behind <c>Editable = …</c>, <c>CurrPage.Editable(…)</c>), so skipping it
    /// froze every page at whatever state its first row left behind.
    /// </summary>
    private bool Loaded(bool found)
    {
        if (found) _page?.RaiseOnAfterGetRecord();
        return found;
    }

    public override object? GetBookmark() => _record.ALGetPosition();

    public override bool GoToBookmark(object bookmark)
    {
        if (bookmark is not string position || string.IsNullOrEmpty(position)) return false;
        _record.ALSetPosition(position);
        return Loaded(true);
    }

    public override object[] GetTableFieldValues(int[] fieldIds)
        => fieldIds.Select(fieldNo => ReadClientObject(fieldNo) ?? string.Empty).ToArray();

    // The only ITestPage entry point that genuinely receives a CONTROL id.
    public override bool FindRowFromControlFieldValue(int controlId, object value, bool forward)
        => FindRowFromTableFieldValues(new[] { ControlIdToTableFieldNo(controlId) }, new[] { value }, forward);

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

        if (hasCurrent) { _record.ALSetPosition(original); Loaded(true); }
        return false;
    }

    // ITestFilter.SetFilter/GetFilter are handed a TABLE FIELD NUMBER, not a control id:
    // AL's `TestPage.Filter.SetFilter(Field, ...)` resolves the field reference itself and
    // BC passes the field number straight through. Routing these through the control map
    // was wrong in both directions — it would mistranslate a field number that happens to
    // collide with a control id, and it rejected small, perfectly valid field numbers as
    // "not a control" (Pageworks SetFilter(3, …) on PageworksPartial).
    public override void SetFilter(int fieldNo, string filterValue)
    {
        _record.ALSetFilter(fieldNo, filterValue);
        RepositionAfterFilterChange();
    }

    /// <summary>
    /// A filter changes which rows the page HAS, so the cursor may no longer be on one of
    /// them. Left alone, the page keeps answering from a record the filter excludes — and
    /// that reads as a real, plausible value belonging to the wrong row, so the test fails
    /// claiming the data is wrong rather than the cursor.
    ///
    /// Real BC always repositions to the FIRST row of the new filtered set, exactly like the
    /// underlying Record.SetFilter; it does not special-case "the current row still
    /// qualifies" to leave the cursor in place (corpus CU60694
    /// SetFilter_EvenWhenCurrentRowStillQualifies_RepositionsToTheFirstMatch, validated
    /// against a real service tier). An empty result leaves the page on no row, which
    /// MoveFirst reports as false.
    /// </summary>
    private void RepositionAfterFilterChange() => MoveFirst();

    public override string GetFilter(int fieldNo)
        => _record.ALGetFilter(fieldNo);

    /// <summary>
    /// Resolve a CONTROL id to the source-table field it is bound to. A control bound to a
    /// page variable is not in the rowset and cannot be used to locate a row, so this
    /// refuses rather than passing the control id through as a field number — which is
    /// what produced "field number '&lt;hash&gt;' cannot be found", blaming the table for
    /// the runner's own inability to resolve the control.
    /// </summary>
    private int ControlIdToTableFieldNo(int controlId)
    {
        if (_controlIdToFieldNo.TryGetValue(controlId, out var fieldNo)) return fieldNo;
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestPage control {controlId} used to locate a row",
            "testpage-control-binding — this control is not bound to a field of the page's "
            + $"source table ({_record.MetaTable?.TableName ?? "?"}), so it cannot be used to "
            + "locate a row. See docs/scope.md");
    }

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

/// <summary>
/// Option values as a TestPage sees them: member NAMES going in, a member name coming back out.
///
/// AL's TestPage API is string-typed for every control — <c>Field.SetValue('Sum')</c>,
/// <c>Field.Value()</c> — so the option's member table is the only thing that can turn that
/// string into the ordinal the record stores, and back. Without it a write puts a NavText into
/// an Option and dies inside BC's own setter ("The value \"Sum\" can't be evaluated into type
/// Option"), and a read answers with the bare ordinal, which no AL test is written against.
///
/// Shared by the Rec-bound field and the page-variable-bound field. It was originally written
/// for the latter only, which is exactly the shape of bug worth avoiding here: the two kinds of
/// control look identical in AL, so a test author has no way to know that one of them resolves
/// option names and the other does not.
/// </summary>
internal static class TestPageOptionValue
{
    /// <summary>Turn the string a test wrote into the NavOption the binding holds.</summary>
    internal static NavValue Resolve(NavOption current, string value, string[]? captions, string context)
    {
        var metadata = current.NavOptionMetadata
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                context,
                "testpage-option-value — the control is bound to an Option with no option "
                + "metadata, so a value cannot be resolved by name. See docs/scope.md");

        var options = Members(metadata);
        var ordinals = Ordinals(metadata);

        // A TestPage sets an option by what the user sees, i.e. the control's OptionCaption,
        // which is NOT the option's member names (Pageworks: captions
        // "Fields,Blocks,Images,…" over members [Field, Block, Image, …]). Captions first,
        // then members — the caption is what AL test code is written against.
        if (captions != null)
            for (var i = 0; i < captions.Length; i++)
                if (OptionNamesEqual(captions[i], value))
                    return NavOption.Create(metadata, OrdinalAt(ordinals, i));

        for (var i = 0; i < options.Length; i++)
            if (OptionNamesEqual(options[i], value))
                return NavOption.Create(metadata, OrdinalAt(ordinals, i));

        // A bare number is a legal way to set an option, and unambiguous.
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var literal)
            && (ordinals == null || ordinals.Contains(literal)))
            return NavOption.Create(metadata, literal);

        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            context,
            $"testpage-option-value — '{value}' is not one of the option's values "
            + $"[{string.Join(", ", options)}]"
            + (captions != null
                ? $" nor one of its captions [{string.Join(", ", captions)}]"
                : " (the control declares no OptionCaption)")
            + ". See docs/scope.md");
    }

    /// <summary>
    /// The text a test reads back. Deliberately the same spelling <see cref="Resolve"/> accepts
    /// first, so <c>SetValue(Value())</c> is a no-op — a page whose read and write disagreed
    /// about captions-vs-members would let a test copy a value from one field to another and
    /// silently write a different member.
    /// </summary>
    internal static string? Display(NavOption option, string[]? captions)
    {
        var metadata = option.NavOptionMetadata;
        if (metadata == null) return null;

        var index = IndexOfOrdinal(metadata, option.Value);
        if (index < 0) return null;

        if (captions != null && index < captions.Length) return captions[index];
        var options = Members(metadata);
        return index < options.Length ? options[index] : null;
    }

    /// <summary>The number of members, for AL that walks an option set rather than naming one.</summary>
    internal static int Count(NavOption option)
        => option.NavOptionMetadata is { } metadata ? Members(metadata).Length : 0;

    /// <summary>The member at a position, in the same spelling <see cref="Display"/> uses.</summary>
    internal static string MemberAt(NavOption option, int index, string[]? captions)
    {
        if (captions != null && index >= 0 && index < captions.Length) return captions[index];
        if (option.NavOptionMetadata is not { } metadata) return string.Empty;
        var options = Members(metadata);
        return index >= 0 && index < options.Length ? options[index] : string.Empty;
    }

    // Options / OrdinalValues are internal to Ncl — read them reflectively rather than
    // re-deriving the option set from OptionString, which would lose the ordinal gaps a
    // declared option set is allowed to have.
    private static string[] Members(object metadata)
        => ReadNonPublic<string[]>(metadata, "Options") ?? Array.Empty<string>();

    private static int[]? Ordinals(object metadata) => ReadNonPublic<int[]>(metadata, "OrdinalValues");

    private static int OrdinalAt(int[]? ordinals, int index)
        => ordinals != null && index < ordinals.Length ? ordinals[index] : index;

    private static int IndexOfOrdinal(object metadata, int ordinal)
    {
        var ordinals = Ordinals(metadata);
        if (ordinals == null)
            return ordinal >= 0 && ordinal < Members(metadata).Length ? ordinal : -1;
        return Array.IndexOf(ordinals, ordinal);
    }

    private static T? ReadNonPublic<T>(object target, string name) where T : class
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var pi = t.GetProperty(name, System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (pi != null) return pi.GetValue(target) as T;
        }
        return null;
    }

    // AL option names are compared ignoring case and spacing, the same way the runner
    // compares object and field names elsewhere ("Custom Fields" vs "CustomFields").
    private static bool OptionNamesEqual(string left, string right)
        => string.Equals(left.Replace(" ", string.Empty), right.Replace(" ", string.Empty),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class LiveNavTestField : ITestField
{
    private readonly NavRecord _record;
    private readonly int _fieldNo;
    // The page behind the control, when there is one. A Rec-bound control still has an
    // OnLookup trigger on the page, and that trigger is the only thing Lookup() can run.
    private readonly RunnerPageInstance? _page;
    private readonly int _controlId;

    // Told when this field writes, so the page can persist the row at the moment BC would.
    // The field itself owns no page state and must not: a part's fields belong to the part's
    // page, not to the card the test is holding.
    private readonly Action? _onEdited;

    public LiveNavTestField(NavRecord record, int fieldNo)
        : this(record, fieldNo, page: null, controlId: 0, onEdited: null) { }

    public LiveNavTestField(NavRecord record, int fieldNo, RunnerPageInstance? page, int controlId,
        Action? onEdited)
    {
        _record = record;
        _fieldNo = fieldNo;
        _page = page;
        _controlId = controlId;
        _onEdited = onEdited;
    }

    public string Value
    {
        // An option field answers with its MEMBER NAME, not the ordinal it stores. Returning the
        // ordinal made every comparison against a member name fail while looking like a data
        // problem ("expected <Mid>, got <0>") rather than a missing option table.
        get => (CurrentOption() is { } option
                   ? TestPageOptionValue.Display(option, OptionCaptions())
                   : null)
               ?? Convert.ToString(ObjectValue, CultureInfo.InvariantCulture)
               ?? string.Empty;
        set
        {
            var navValue = CurrentOption() is { } option
                ? TestPageOptionValue.Resolve(option, value, OptionCaptions(),
                    $"TestPage SetValue (field {_fieldNo})")
                : ALCompiler.ToNavValue(value);

            // Setting a field on a page is a VALIDATE, not an assignment. That is what fills in
            // the caption when a user picks an id, and what lets a field refuse a value outright.
            // A raw SetFieldValue stored what the test wrote — so the field itself read back
            // correctly and every field DERIVED from it stayed empty, which made the test fail
            // pointing at the derived field, the one place the defect was not.
            _record.ALValidateAsync(_fieldNo, navValue, null).GetAwaiter().GetResult();

            // Then the control's own OnValidate, which is a second and independent trigger: the
            // table field's runs first, the page's after it.
            if (_page != null && _controlId != 0) _page.RaiseOnValidate(_controlId);

            _onEdited?.Invoke();
        }
    }

    // The stored NavValue, not the unwrapped ClientObject — the option metadata rides on the
    // NavOption itself, and unwrapping it to an int is what loses the member table.
    private NavOption? CurrentOption() => _record.GetFieldValue(_fieldNo) as NavOption;

    // Record-only mode has no control to carry an OptionCaption, so members are all there is.
    private string[]? OptionCaptions()
        => _page != null && _controlId != 0 ? _page.TryGetOptionCaptions(_controlId) : null;

    public string Name => Caption;
    public string Caption => TryGetMetaFieldName() ?? $"Field {_fieldNo}";
    public NavType FieldType => TryGetMetaFieldType() ?? NavType.Text;
    public int ValidationErrorCount => 0;
    public long LastUsedValidationErrorId => 0;
    public long MaxValidationErrorId => 0;
    public object? ObjectValue => LiveNavTestPage.Unwrap(_record.GetFieldValue(_fieldNo));
    public int OptionCount => CurrentOption() is { } option ? TestPageOptionValue.Count(option) : 0;

    // The control's declared state, not a constant. `Editable = false` / `Editable = SomeVar`
    // is how a page protects rows it does not own, so answering true unconditionally made
    // every test of that protection pass no matter what the page said. Falls back to true
    // only when there is no page object to ask — the record-only mode, which has no control
    // metadata at all and never claimed to model these.
    public bool Enabled  => _page?.ControlEnabled(_controlId) ?? true;
    public bool Editable => _page?.ControlEditable(_controlId) ?? true;
    public bool Visible  => _page?.ControlVisible(_controlId) ?? true;
    public bool HideValue => false;
    public bool ShowMandatory => false;

    public string GetValidationError(int index) => string.Empty;
    public void Activate() { }

    /// <summary>
    /// Run the control's OnLookup trigger — the AL a user's F4 would run. The base mock does
    /// nothing, which let a test invoke a lookup, observe no change, and compare two empty
    /// strings successfully.
    /// </summary>
    public void Lookup()
    {
        if (_page == null)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage lookup on field {_fieldNo}",
                "testpage-lookup — no AL page object was built for this page, so its OnLookup "
                + "trigger cannot be reached. See docs/scope.md");

        // BC's contract: the trigger writes the selection back and returns true; a false
        // return means the user cancelled and the field keeps its value.
        var picked = _page.RaiseOnLookup(_controlId, NavText.Create(Value));
        if (picked != null) Value = picked.ToString();
    }

    public void Lookup(NavDataSet dataSet) => Lookup();
    public void AssistEdit() { }
    public void Drilldown() { }
    public void Invoke() { }
    public string ValueToString(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    // AL that walks an option set (building a picker, asserting the members a field offers) got
    // an empty string for every index, which reads as "this option has blank members" rather
    // than as an unimplemented accessor.
    public string GetOption(int index)
        => CurrentOption() is { } option
            ? TestPageOptionValue.MemberAt(option, index, OptionCaptions())
            : string.Empty;

    private string? TryGetMetaFieldName()
    {
        return _record.MetaTable.TryGetFieldByNo(_fieldNo, out var field) ? field.FieldName : null;
    }

    private NavType? TryGetMetaFieldType()
    {
        return _record.MetaTable.TryGetFieldByNo(_fieldNo, out var field) ? field.FieldNavType : null;
    }
}

/// <summary>
/// A TestPage field over a control bound to a PAGE VARIABLE rather than to a source-table
/// field. Reads and writes go through the page's own source expression — BC's binding, not
/// a runner-side copy — so the value lives on the page instance exactly where the AL
/// declared it, and a second page instance starts with its own.
///
/// Writing also runs the control's OnValidate trigger, because that is what setting a
/// value on a page does; a setter that skipped it would let a test observe the value it
/// just wrote while none of the page's AL had run.
/// </summary>
internal sealed class PageVariableTestField : ITestField
{
    private readonly RunnerPageInstance _page;
    private readonly object _expression;
    private readonly int _controlId;

    public PageVariableTestField(RunnerPageInstance page, object expression, int controlId)
    {
        _page = page;
        _expression = expression;
        _controlId = controlId;
    }

    public string Value
    {
        get => Convert.ToString(ObjectValue, CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            RunnerPageInstance.SetValue(_expression, ToBoundValue(value));
            _page.RaiseOnValidate(_controlId);
        }
    }

    public object? ObjectValue => LiveNavTestPage.Unwrap(RunnerPageInstance.GetValue(_expression));

    /// <summary>
    /// Convert the string a test wrote into the NavValue the binding actually holds.
    /// AL's TestPage SetValue is string-typed for every control, so the target type has to
    /// come from the binding, not from the caller — writing a NavText into an Option
    /// binding throws deep inside the page's own generated setter
    /// ("Unable to cast object of type 'NavText' to type 'NavOption'"), which says nothing
    /// about the value that was wrong.
    /// </summary>
    private NavValue ToBoundValue(string value)
        => RunnerPageInstance.GetValue(_expression) is NavOption option
            ? TestPageOptionValue.Resolve(option, value, _page.TryGetOptionCaptions(_controlId),
                $"TestPage SetValue (control {_controlId})")
            : ALCompiler.ToNavValue(value);

    public string Name => Caption;
    public string Caption => _expression.GetType()
        .GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.GetValue(_expression) as string ?? string.Empty;

    public NavType FieldType => NavType.Text;
    public int ValidationErrorCount => 0;
    public long LastUsedValidationErrorId => 0;
    public long MaxValidationErrorId => 0;
    public int OptionCount => 0;

    // See LiveNavTestField — a control bound to a page variable declares the same properties
    // as one bound to a record field, and they are read the same way.
    public bool Enabled  => _page.ControlEnabled(_controlId);
    public bool Editable => _page.ControlEditable(_controlId);
    public bool Visible  => _page.ControlVisible(_controlId);
    public bool HideValue => false;
    public bool ShowMandatory => false;

    public string GetValidationError(int index) => string.Empty;
    public void Activate() { }
    /// <summary>Run the control's OnLookup trigger — see LiveNavTestField.Lookup.</summary>
    public void Lookup()
    {
        var picked = _page.RaiseOnLookup(_controlId, NavText.Create(Value));
        if (picked != null) Value = picked.ToString();
    }
    public void Lookup(NavDataSet dataSet) => Lookup();
    public void AssistEdit() { }
    public void Drilldown() { }
    public void Invoke() { }
    public string ValueToString(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    public string GetOption(int index) => string.Empty;
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
/// A page action driven live: Invoke() runs the page's own OnAction trigger, on the page
/// instance the TestPage is driving, so the trigger sees the current row.
///
/// Visible/Enabled come from the action's own declared properties, which are constants or
/// expressions evaluated against the page's live state — so an action gated on the current
/// row (<c>Enabled = RowEditable</c>) reports differently as the cursor moves.
/// </summary>
internal sealed class LiveNavTestAction : ITestAction
{
    private readonly RunnerPageInstance _page;
    private readonly int _actionId;

    public LiveNavTestAction(RunnerPageInstance page, int actionId)
    {
        _page = page;
        _actionId = actionId;
    }

    public void Invoke() => _page.RaiseOnAction(_actionId);

    public bool Visible => _page.ActionVisible(_actionId);
    public bool Enabled => _page.ActionEnabled(_actionId);
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

/// <summary>
/// A subpage part driven live: its own page over its own source table, showing only the
/// rows the SubPageLink selects for the parent's CURRENT row.
///
/// The link is re-applied before every operation rather than once at construction, because
/// NavTestPageBase caches parts for the life of the page: a filter applied once would go
/// stale the moment the AL test moved the parent to another row, and the part would then
/// show the previous row's children — a wrong answer that no assertion in the part itself
/// could distinguish from a right one.
/// </summary>
internal sealed class LiveNavTestPart : LiveNavTestPage, ITestPart
{
    private readonly NavRecord _parentRecord;
    private readonly (int PartFieldNo, int ParentFieldNo)[] _links;

    public LiveNavTestPart(NavRecord record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page, object owner, int pageId,
        NavRecord parentRecord, (int PartFieldNo, int ParentFieldNo)[] links)
        : base(record, controlIdToFieldNo, creatable, page, owner, pageId)
    {
        _parentRecord = parentRecord;
        _links = links;
    }

    public bool Enabled => true;
    public bool Visible => true;

    /// <summary>Filter the part's rowset to the parent's current row.</summary>
    private void ApplyLink()
    {
        foreach (var (partFieldNo, parentFieldNo) in _links)
        {
            var parentValue = _parentRecord.GetFieldValue(parentFieldNo);
            Record.ALSetRange(partFieldNo, parentValue);
        }
    }

    public override bool MoveFirst() { ApplyLink(); return base.MoveFirst(); }
    public override bool MoveLast() { ApplyLink(); return base.MoveLast(); }
    public override bool MoveNext() { ApplyLink(); return base.MoveNext(); }
    public override bool MovePrevious() { ApplyLink(); return base.MovePrevious(); }

    public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)
    {
        ApplyLink();
        return base.FindRowFromTableFieldValues(fieldNos, values, forward);
    }

    /// <summary>
    /// Start a new row already carrying the parent's key. BC stamps the linked fields onto
    /// the new record, which is why an AL test never sets them — asserting that the link
    /// field arrived without being written is how the test proves the link ran at all.
    /// </summary>
    public override void InsertEmptyRow(bool beforeCurrent)
    {
        ApplyLink();
        base.InsertEmptyRow(beforeCurrent);
        foreach (var (partFieldNo, parentFieldNo) in _links)
            Record.SetFieldValue(partFieldNo, _parentRecord.GetFieldValue(parentFieldNo));
    }
}
