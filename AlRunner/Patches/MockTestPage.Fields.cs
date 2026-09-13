// MockTestPage.Fields.cs — the two live ITestField implementations: a control bound to a
// source-table field, and one bound to a page variable.
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

    // Told BEFORE this field writes, so the page can turn its implicit new-row line into a
    // real started row while the write's own OnValidate can still see it — see
    // LiveNavTestPage.PromoteNewRowLineForWrite (#2923). Separate from _onEdited because the
    // order is the whole point: _onEdited runs after the validate and is far too late to give
    // the row its keys.
    private readonly Action? _onBeforeEdit;

    // Told when this control takes focus — LiveNavTestPage.ActivateControl, which is where a
    // started new row is inserted (#4062). Called by Activate() and before every write.
    private readonly Action<int>? _onActivate;

    public LiveNavTestField(NavRecord record, int fieldNo)
        : this(record, fieldNo, page: null, controlId: 0, onEdited: null, onBeforeEdit: null,
               onActivate: null, pageValidationErrors: null) { }

    public LiveNavTestField(NavRecord record, int fieldNo, RunnerPageInstance? page, int controlId,
        Action? onEdited, Action? onBeforeEdit, Action<int>? onActivate,
        TestPageValidationErrors? pageValidationErrors)
    {
        _record = record;
        _fieldNo = fieldNo;
        _page = page;
        _controlId = controlId;
        _onEdited = onEdited;
        _onBeforeEdit = onBeforeEdit;
        _onActivate = onActivate;
        _validationErrors = new TestFieldValidationErrors(pageValidationErrors);
    }

    // The refusals this control has recorded, read back by ValidationErrorCount /
    // GetValidationError below. See TestFieldValidationErrors for BC's own contract: the
    // ITestField setter RECORDS a refusal, it does not throw it — NavTestField.CheckError
    // (BC's own precompiled code, wrapping every SetValue) is what raises it afterwards, and
    // it can only do that if the ledger survives the write (#2900).
    //
    // Constructed with the owning PAGE's ledger (#3009) so a refusal lands in both: BC reads
    // the field's count AND the page's around every write, and AL can read either afterwards.
    // Null for the record-only ctor above, which has no page to report to.
    private readonly TestFieldValidationErrors _validationErrors;

    public string Value
    {
        // An option field answers with its MEMBER NAME, not the ordinal it stores. Returning the
        // ordinal made every comparison against a member name fail while looking like a data
        // problem ("expected <Mid>, got <0>") rather than a missing option table.
        get => (CurrentOption() is { } option
                   ? TestPageOptionValue.Display(option, OptionCaptions())
                   : null)
               // #3406: the control's OWN decimal format, computed by BC's GetDecimalString
               // cascade and read off the page, not a hardcoded two decimals. Null _page is
               // the record-only field, which has no control and keeps the old spelling.
               ?? TestPageNumericValue.Format(_record.GetFieldValue(_fieldNo) as NavValue,
                                              _page?.TryGetControlFormat(_controlId))
               // #2795: "Yes"/"No", not Convert.ToString's "True"/"False".
               ?? TestPageBooleanValue.Format(_record.GetFieldValue(_fieldNo) as NavValue)
               // #2361: a blank Date/Time/DateTime is '', not the rendered CLR minimum.
               ?? TestPageBlankTemporalValue.Format(_record.GetFieldValue(_fieldNo) as NavValue)
               ?? Convert.ToString(ObjectValue, CultureInfo.InvariantCulture)
               ?? string.Empty;
        // appendRefreshSuffix: true — a Rec-bound control stages a row edit, and real BC's
        // client decorates its recorded validation error with the offer to discard it. Measured
        // on corpus run 34002487601; see TestFieldValidationErrors' header.
        set => _validationErrors.RunRecordingRefusal(() => Write(value), appendRefreshSuffix: true);
    }

    private void Write(string value)
    {
        // FIRST, before anything reads or writes the record: if the page is parked on its
        // implicit new-row line, this write is what turns that line into a row the flush path
        // will persist, and BC settles the row's key BEFORE the typed value is validated onto
        // it. Doing it after (which is all MarkEdited below could do) handed the field's own
        // OnValidate a row with no key: issue #2923, 35 Tests-SMB failures on Sales Line and
        // Purchase Line. A no-op on every page not sitting on that line, which is all of them
        // once a real row is current.
        //
        // Inside Write, so it is inside the RunRecordingRefusal the setter wraps this in
        // (#3007): starting the row is part of this control write, so a refusal raised while
        // starting it is recorded and re-raised by BC's NavTestField.CheckError exactly as a
        // refusal of the value itself would be.
        _onBeforeEdit?.Invoke();

        // BC's TestFieldProxy.Value setter activates the control before it writes, and focus
        // arriving on a non-key control is what inserts a started new row (#4062).
        _onActivate?.Invoke(_controlId);

        // #3640: everything from here on can mutate Rec — the field's own OnValidate, the
        // control's, and any pageextension modify() trigger around them — and real BC discards
        // those mutations when the write raises. Snapshot AFTER _onBeforeEdit, because the
        // new-row promotion above writes the row's KEY, which is page state settled before the
        // value is validated rather than a trigger's mutation of it; the tier measured the
        // trigger half and says nothing about unwinding the promotion.
        //
        // Taken and put back INLINE rather than by wrapping the rest of this method in a
        // lambda: TestPageNewRowLinePromotionTests reads this method's IL to pin that
        // _onBeforeEdit precedes ALValidateAsync and _onEdited follows it (#2923), and moving
        // any of those three into a compiler-generated closure hides the ordering from the
        // one test that guards it. See TestPageWriteBuffer.
        var restore = TestPageWriteBuffer.Snapshot(_record);
        try
        {
        // Issue #1870 — the Rec-bound half of #1837 that #1869 (the page-variable half)
        // left open. FieldType (sourced from the source table field's own declared type,
        // see TryGetMetaFieldType) answers Boolean for a `field(Flag; Rec.Flag)` control
        // over a `Boolean` table field; falling through to ALCompiler.ToNavValue(value)
        // there always produced a NavText, which NavTestField.ALSetValue's own Boolean
        // ALValidateAsync then rejected with "The value 'True' can't be evaluated into
        // type Boolean" — the same shape of bug TestPageBooleanValue already fixed for
        // PageVariableTestField.
        //
        // #3384 — Date/DateTime/Time needed the same kind of arm. Without one this fell through
        // to ToNavValue for every temporal control, so the typed value BC's ALSetValue had just
        // rendered to text was validated as a NavText and refused, and text the test wrote
        // itself was refused the same way — including the ISO spelling BC's own message
        // recommends. See TestPageTemporalValue.
        var navValue = CurrentOption() is { } option
            ? TestPageOptionValue.Resolve(option, value, OptionCaptions(),
                $"TestPage SetValue (field {_fieldNo})")
            : FieldType == NavType.Boolean
                ? TestPageBooleanValue.Resolve(value, Caption)
                : TestPageTemporalValue.TryResolve(FieldType, value, out var temporal)
                    ? temporal!
                    : ALCompiler.ToNavValue(value);

        // MinValue/MaxValue (#2495): measured against real BC (28.1/28.4), a bounded field's
        // MinValue/MaxValue is enforced on a TestPage control WRITE, but NOT on Rec.Validate
        // or a plain field assignment — so this check belongs here, at the page-write layer,
        // and must not move into ALValidateAsync below (that is also what Rec.Validate calls,
        // and pulling the check in there would enforce it on Validate too, which real BC does
        // not). See TestPageMinMaxValue.Check's own doc comment for the exact message shape.
        TestPageMinMaxValue.Check(_record.MetaTable, _fieldNo, FieldType, value, Caption);

        // Setting a field on a page is a VALIDATE, not an assignment. That is what fills in
        // the caption when a user picks an id, and what lets a field refuse a value outright.
        // A raw SetFieldValue stored what the test wrote — so the field itself read back
        // correctly and every field DERIVED from it stayed empty, which made the test fail
        // pointing at the derived field, the one place the defect was not.
        //
        // Issue #2705 — real BC (measured on a 28.4 container) runs the bound field's
        // OnValidate with CurrFieldNo equal to that field's number for the duration of a
        // page-driven write (own-table AND tableextension fields alike), while a
        // Rec.Validate from AL code leaves it at 0. NavRecord.CurrFieldNo
        // (Microsoft.Dynamics.Nav.Ncl.dll, decompiled) is a plain public get/set property
        // that nothing in Ncl itself ever assigns — real BC's compiled client/page glue must
        // set it around a UI-originated validate, which is exactly what a TestPage SetValue
        // is standing in for here. Restoring the PREVIOUS value (not unconditionally 0)
        // keeps a nested SetValue-from-OnValidate honest, and the try/finally matches what
        // arm E of the corpus test measures: OnModify after Close() sees CurrFieldNo = 0
        // again, so the assignment must not outlive this one validate call.
        var previousCurrFieldNo = _record.CurrFieldNo;
        _record.CurrFieldNo = _fieldNo;
        // #3543: BC brackets a page-driven field validate in a transaction of its own —
        // NavRecord.ValidateFieldsAsync opens Session.BeginTransaction() per field and closes
        // it with Session.EndTransaction(commit) in a finally, and NavForm.ModifyAsync does
        // the same around the page's own Modify. So an OnValidate that writes is legal even
        // when the CALLER holds no transaction, which under TransactionModel::None is the
        // whole test body (#3480). Without this bracket the runner refused a write BC allows.
        //
        // Observably equivalent: the counter grants a write nothing outside a None scope,
        // where ThrowIfNoTransactionForWrite returns before it is read at all, and it changes
        // no commit point — which is a separate question, tracked by NoteTransactionEnd and
        // deliberately left alone here (#2413 measured that conflating the two is wrong).
        AlRunner.Patches.ALDatabasePatches.EnterRunTransaction();
        try
        {
            try
            {
                _record.ALValidateAsync(_fieldNo, navValue, null).GetAwaiter().GetResult();
            }
            finally
            {
                _record.CurrFieldNo = previousCurrFieldNo;
            }

            // Then the control's own OnValidate, which is a second and independent trigger: the
            // table field's runs first, the page's after it.
            if (_page != null && _controlId != 0) _page.RaiseOnValidate(_controlId);
        }
        finally
        {
            AlRunner.Patches.ALDatabasePatches.ExitRunTransaction();
        }

        _onEdited?.Invoke();
        }
        catch
        {
            restore?.Invoke();
            throw;
        }
    }

    // The stored NavValue, not the unwrapped ClientObject — the option metadata rides on the
    // NavOption itself, and unwrapping it to an int is what loses the member table.
    private NavOption? CurrentOption() => _record.GetFieldValue(_fieldNo) as NavOption;

    // Record-only mode has no control to carry an OptionCaption, so members are all there is.
    // CurrentOption() is passed through so an Enum-typed field can fall back to the enum's
    // own captions when the control declares no OptionCaption — see TryGetOptionCaptions.
    private string[]? OptionCaptions()
        => _page != null && _controlId != 0 ? _page.TryGetOptionCaptions(_controlId, CurrentOption()) : null;

    public string Name => Caption;

    // TestPage field Caption() (#1777). BC's own precedence, control-declared wins over the
    // source field's Caption, which wins over the field's bare name:
    //   1. the control's own Caption (field(Foo; Rec.Foo) { Caption = '…'; }) — page metadata
    //      that only exists when this field is bound to a live control, not a bare NavRecord.
    //   2. the source table field's declared Caption (field(2; Foo; Text[30]) { Caption = '…'; })
    //      — read straight from the parse-time metadata, bypassing NCLMetaField.FieldCaption
    //      (JmpHooked to always answer the field NAME; see TryGetParsedFieldCaption).
    //   3. the field's technical name, BC's own fallback when neither is declared.
    public string Caption
        => (_page != null && _controlId != 0 ? _page.TryGetControlCaption(_controlId) : null)
           ?? TryGetMetaFieldCaption()
           ?? TryGetMetaFieldName()
           ?? $"Field {_fieldNo}";
    public NavType FieldType => TryGetMetaFieldType() ?? NavType.Text;
    // BC's own NavTestField.CheckError reads all three around every control write, and
    // NavTestField.ALValidationErrorCount / ALGetValidationError hand the first and fourth
    // straight to AL. Hardcoded 0/"" made `ValidationErrorCount()` answer 0 after a refusal
    // real BC counts as 1, and made a refusal escape the setter raw instead of being wrapped
    // by BC in "Validation error for Field: ..." (#2900). See TestFieldValidationErrors.
    public int ValidationErrorCount => _validationErrors.Count;
    public long LastUsedValidationErrorId => _validationErrors.LastUsedId;
    public long MaxValidationErrorId => _validationErrors.MaxId;
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

    public string GetValidationError(int index) => _validationErrors.Get(index);
    public void Activate() => _onActivate?.Invoke(_controlId);

    /// <summary>
    /// Run the control's OnLookup trigger — the AL a user's F4 would run. The base mock does
    /// nothing, which let a test invoke a lookup, observe no change, and compare two empty
    /// strings successfully.
    /// </summary>
    public void Lookup()
    {
        if (_page == null)
            throw TestPageShapeGap.Lookup(
                $"TestPage lookup on field {_fieldNo}",
                "no AL page object was built for this page, so its OnLookup trigger cannot be "
                + "reached");

        // BC's contract: the trigger writes the selection back and returns true; a false
        // return means the user cancelled and the field keeps its value.
        // The record and field number go with the call so RaiseOnLookup can fall back to the
        // SOURCE TABLE FIELD's own OnLookup when the control declares none (#2549). A table
        // trigger writes into Rec rather than handing a value back, which is why the null it
        // returns for that path needs no special handling here: the Value getter reads the
        // record, where the trigger already wrote.
        var picked = _page.RaiseOnLookup(_controlId, NavText.Create(Value), _record, _fieldNo);
        if (picked != null) Value = picked.ToString();
    }

    public void Lookup(NavDataSet dataSet) => Lookup();

    /// <summary>
    /// Run the control's OnAssistEdit trigger — see RunnerPageInstance.RaiseOnAssistEdit,
    /// including why a control with no such trigger stays silent here rather than refusing.
    /// Was a literal no-op (#2362, #3642), so a page's declared OnAssistEdit never ran and
    /// nothing said so: the UI handlers the test bound for what that trigger raises went
    /// unexecuted and the test failed at teardown, pointing at the handlers rather than here.
    /// </summary>
    public void AssistEdit()
    {
        if (_page == null)
            throw TestPageShapeGap.AssistEdit(
                $"TestPage assist-edit on field {_fieldNo}",
                "no AL page object was built for this page, so its OnAssistEdit trigger cannot "
                + "be reached");

        _page.RaiseOnAssistEdit(_controlId);
    }

    /// <summary>
    /// Run the control's OnDrillDown trigger — see RunnerPageInstance.RaiseOnDrillDown for the
    /// full contract, including the fixed error real BC raises when no trigger is declared.
    /// Left #57's literal no-op (`public void Drilldown() { }`), which let a test call
    /// DrillDown(), observe nothing happened, and pass anyway — the trigger's effect (or its
    /// documented absence-error) never ran, and the test only tripped one step later on a
    /// missing side effect that pointed at the wrong place.
    /// </summary>
    public void Drilldown()
    {
        if (_page == null)
            throw TestPageShapeGap.DrillDown(
                $"TestPage drilldown on field {_fieldNo}",
                "no AL page object was built for this page, so its OnDrillDown trigger cannot "
                + "be reached");

        _page.RaiseOnDrillDown(_controlId);
    }

    public void Invoke() { }

    // An Option/Enum-bound control renders an ordinal as the text the control SHOWS, the same
    // spelling the Value getter answers with — issue #2367. BC's own ALAssertEquals/ALSetValue
    // strip an AL option value down to a bare ordinal before calling this (see
    // TestPageOptionValue.DisplayOrdinal for the exact chain), so leaving it as
    // Convert.ToString made AssertEquals compare the ordinal '2' against the control's
    // 'Pending Approval' and report a mismatch for the value the record actually held.
    public string ValueToString(object? value)
        => TestPageOptionValue.DisplayOrdinal(CurrentOption(), value, OptionCaptions())
           // #2795: BC's ALAssertEquals converts the EXPECTED value through here and compares it
           // ordinally against the control's Value, so this has to answer with the same word the
           // getter above does or AssertEquals(<Boolean>) can never match.
           ?? TestPageBooleanValue.FormatObject(value)
           // #2361: same reason, for a blank temporal — AssertEquals('') is its own corpus test.
           ?? TestPageBlankTemporalValue.FormatObject(value)
           ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

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

    // The source field's own declared Caption — see RecordPatches.TryGetParsedFieldCaption
    // for why this cannot go through NCLMetaField.FieldCaption.
    private string? TryGetMetaFieldCaption()
    {
        var tableId = _record.MetaTable.TableId;
        return tableId != 0 ? RecordPatches.TryGetParsedFieldCaption(tableId, _fieldNo) : null;
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

    public PageVariableTestField(RunnerPageInstance page, object expression, int controlId,
        TestPageValidationErrors? pageValidationErrors)
    {
        _page = page;
        _expression = expression;
        _controlId = controlId;
        _validationErrors = new TestFieldValidationErrors(pageValidationErrors);
    }

    // The Rec-bound sibling's ledger, for the same reason and read the same way — see
    // LiveNavTestField._validationErrors and TestFieldValidationErrors. A page-global control
    // refuses a write through its OnValidate exactly as a Rec-bound one does, so leaving this
    // half hardcoded would have made the same AL assertion answer differently depending only
    // on how the control happens to be bound.
    //
    // Constructed with the owning PAGE's ledger (#3009), same as the Rec-bound sibling — the
    // page counts a refusal whichever way the control that raised it is bound.
    private readonly TestFieldValidationErrors _validationErrors;

    public string Value
    {
        // An Option/Enum-bound control answers with its CAPTION, not the ordinal it stores —
        // the read-side complement of #1928 (issue #2055). LiveNavTestField.Value already does
        // this for a Rec-bound control; this class never got it, so `Format(Field.Value())` on
        // a page-variable enum control returned "1" instead of "OR" while the write direction
        // (SetValue, below) already resolved captions correctly.
        get => (CurrentOption() is { } option
                   ? TestPageOptionValue.Display(option, _page.TryGetOptionCaptions(_controlId, option))
                   : null)
               // #3406: the page-global half of the decimal-format rule — see the Rec-bound
               // sibling. A page-variable control carries the same Control<id>_Format
               // expression, so the two binding shapes must not answer differently.
               ?? TestPageNumericValue.Format(RunnerPageInstance.GetValue(_expression),
                                              _page.TryGetControlFormat(_controlId))
               // #2795: the page-global half of the same rule — see TestPageBooleanValue.Format.
               ?? TestPageBooleanValue.Format(RunnerPageInstance.GetValue(_expression))
               // #2361: the page-global half of the blank-temporal rule — see the Rec-bound
               // sibling. Base Application page 9807 binds WebServiceExpiryDate this way.
               ?? TestPageBlankTemporalValue.Format(RunnerPageInstance.GetValue(_expression))
               ?? Convert.ToString(ObjectValue, CultureInfo.InvariantCulture)
               ?? string.Empty;
        // appendRefreshSuffix: false — a page-global control stages no row edit, so there is
        // nothing for "Refresh to discard" to discard. Microsoft's Tests-SINGLESERVER
        // Codeunit134614 asserts the bare text with exact equality for exactly this binding
        // shape (verified mechanically to be page-variable-bound, not Rec-bound). This is the
        // half no service-tier run has confirmed yet — corpus PR #184 asks it.
        //
        // #3640: the Rec-bound sibling's restore-on-refusal applies here too. A page-variable
        // control's OnValidate is ordinary AL and can write Rec exactly as a Rec-bound one's
        // can, and a page-driven write is a page-driven write whichever way the CONTROL that
        // started it happens to be bound — so leaving this half out would make the same AL
        // observable depend on a binding detail the tier's claim does not mention. Only Rec is
        // restored: what a failed write leaves in a page GLOBAL is a separate claim no service
        // tier has measured, and inventing an answer for it is what
        // ask-the-corpus-before-claiming-bc-behavior.md forbids.
        set => _validationErrors.RunRecordingRefusal(
            () => TestPageWriteBuffer.RunRestoringOnRefusal(_page.Record, () =>
            {
                RunnerPageInstance.SetValue(_expression, ToBoundValue(value));
                _page.RaiseOnValidate(_controlId);
            }), appendRefreshSuffix: false);
    }

    public object? ObjectValue => LiveNavTestPage.Unwrap(RunnerPageInstance.GetValue(_expression));

    // The stored NavValue, not the unwrapped ClientObject — see LiveNavTestField.CurrentOption
    // for why: the option metadata (and, for an Enum, whether it IS one — see
    // TestPageOptionValue.EnumCaptions) rides on the NavOption itself.
    private NavOption? CurrentOption() => RunnerPageInstance.GetValue(_expression) as NavOption;

    /// <summary>
    /// Convert the string a test wrote into the NavValue the binding actually holds.
    /// AL's TestPage SetValue is string-typed for every control, so the target type has to
    /// come from the binding, not from the caller — writing a NavText into an Option
    /// binding throws deep inside the page's own generated setter
    /// ("Unable to cast object of type 'NavText' to type 'NavOption'"), which says nothing
    /// about the value that was wrong. A Boolean binding has the same shape of problem
    /// (#1837): a NavText written into it throws "The input string '...' was not in a
    /// correct format" instead of setting the field, so Boolean gets the same NavOption-style
    /// special case — see <see cref="TestPageBooleanValue"/>.
    ///
    /// Code and Date bindings (#2054) are the same shape of bug again. A `Code[20]` global's
    /// generated setter throws "Unable to cast object of type 'NavText' to type 'NavCode'",
    /// and a `Date` global's throws the same against 'NavDate' — Integer and Text globals
    /// round-trip fine only because their generated setters happen to accept a NavText and
    /// coerce it themselves, which Code's and Date's do not. NavCode carries the field's own
    /// declared length (`Code[20]`), so the replacement is built against the CURRENT bound
    /// value's own MaxLength rather than a guessed constant.
    /// </summary>
    private NavValue ToBoundValue(string value)
        => RunnerPageInstance.GetValue(_expression) switch
        {
            NavOption option => TestPageOptionValue.Resolve(option, value, _page.TryGetOptionCaptions(_controlId, option),
                $"TestPage SetValue (control {_controlId})"),
            NavBoolean => TestPageBooleanValue.Resolve(value, Caption),
            NavCode current => new NavCode(current.MaxLength, value),
            // #3384: NavDate was the only temporal arm here and accepted only the round-trip
            // spelling, throwing out-of-scope for text BC reads happily. NavDateTime and NavTime
            // had no arm at all, so a typed argument reached the page's own generated setter as
            // a NavText and threw InvalidCastException.
            //
            // A DECLINE has to be a typed refusal here, unlike the Rec-bound side. There, the
            // NavText fall-through reaches BC's own ALValidateAsync and BC raises the refusal
            // naming the value. A page variable has no validate behind it: the NavText goes
            // straight into the page's generated setter and comes back out as
            // "Unable to cast object of type 'NavText' to type 'NavDate'" from inside
            // NavFormSourceExpression — which names neither the control nor the value the test
            // wrote. #2054's NavDate arm did raise a typed refusal, and dropping to a bare cast
            // failure would have been a regression against it.
            NavDate or NavDateTime or NavTime =>
                TestPageTemporalValue.TryResolve(FieldType, value, out var temporal)
                    ? temporal!
                    : throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        $"TestPage SetValue (control {_controlId})",
                        $"testpage-temporal-value — '{value}' is not a {FieldType} this BC build "
                        + "can evaluate, and a page-variable control has no field validate "
                        + "behind it to refuse the value itself."),
            _ => ALCompiler.ToNavValue(value),
        };

    public string Name => Caption;
    public string Caption => _expression.GetType()
        .GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.GetValue(_expression) as string ?? string.Empty;

    // The real underlying NavType, not a constant. NavTestField.ALSetValue — the precompiled BC
    // method the AL compiler emits for every SetValue(<Boolean>) call on this control — asks
    // THIS property to pick a NavValueMetadata before converting the incoming value to a string
    // via ITestField.ValueToString (see TestPageBooleanValue's doc comment for the full chain).
    // A hardcoded NavType.Text made BC's own dispatch treat every page-variable control as text,
    // so a Boolean write got coerced through Text metadata into BC's "Yes"/"No" textual spelling
    // (NOT the "True"/"False" ValueToString itself would have produced) before ever reaching our
    // Value setter — which is why the var-bound and Rec-bound halves of #1837 threw two DIFFERENT
    // exceptions for the same SetValue(true) call: they disagreed about what string this control
    // even claimed to receive. A Date global (#2054) failed the SAME way for the SAME reason:
    // FieldType answering Text sent NavTestField.ALSetValue's DMY2Date(...) argument through
    // Text metadata instead of Date, and the text it came out as could not be cast back into
    // the Date binding. Code does not need an entry here — NavCode IS a NavStringValue, so
    // ALSetValue's own fast path (`value is NavStringValue`) skips FieldType/ValueToString
    // entirely for it and hands SetValue's literal straight to ToBoundValue above — but it is
    // listed anyway so a reader checking "does this table cover every case ToBoundValue does"
    // is not left wondering whether it was missed.
    public NavType FieldType => RunnerPageInstance.GetValue(_expression) switch
    {
        NavOption => NavType.Option,
        NavBoolean => NavType.Boolean,
        NavCode => NavType.Code,
        NavDate => NavType.Date,
        // #3384: without these two the control claimed Text, so BC's ALSetValue picked Text
        // metadata for a DateTime/Time argument before ToBoundValue ever saw it.
        NavDateTime => NavType.DateTime,
        NavTime => NavType.Time,
        // #2634/#2534's fix: a Decimal-typed page-global control has to answer NavType.Decimal
        // here too, the same as an Option/Boolean/Code/Date global already does above -- this
        // FieldType is what NavTestField.ALSetValue (BC's own precompiled dispatch) uses to pick
        // a NavValueMetadata before round-tripping through ValueToString, so leaving Decimal out
        // would have kept a Decimal-typed page variable dispatching as plain Text on the WRITE
        // side even after the READ side (Value, above) started formatting it correctly.
        NavDecimal => NavType.Decimal,
        _ => NavType.Text,
    };
    // BC's own NavTestField.CheckError reads all three around every control write, and
    // NavTestField.ALValidationErrorCount / ALGetValidationError hand the first and fourth
    // straight to AL. Hardcoded 0/"" made `ValidationErrorCount()` answer 0 after a refusal
    // real BC counts as 1, and made a refusal escape the setter raw instead of being wrapped
    // by BC in "Validation error for Field: ..." (#2900). See TestFieldValidationErrors.
    public int ValidationErrorCount => _validationErrors.Count;
    public long LastUsedValidationErrorId => _validationErrors.LastUsedId;
    public long MaxValidationErrorId => _validationErrors.MaxId;
    public int OptionCount => CurrentOption() is { } option ? TestPageOptionValue.Count(option) : 0;

    // See LiveNavTestField — a control bound to a page variable declares the same properties
    // as one bound to a record field, and they are read the same way.
    public bool Enabled  => _page.ControlEnabled(_controlId);
    public bool Editable => _page.ControlEditable(_controlId);
    public bool Visible  => _page.ControlVisible(_controlId);
    public bool HideValue => false;
    public bool ShowMandatory => false;

    public string GetValidationError(int index) => _validationErrors.Get(index);
    public void Activate() { }
    /// <summary>Run the control's OnLookup trigger — see LiveNavTestField.Lookup.</summary>
    public void Lookup()
    {
        var picked = _page.RaiseOnLookup(_controlId, NavText.Create(Value));
        if (picked != null) Value = picked.ToString();
    }
    public void Lookup(NavDataSet dataSet) => Lookup();
    /// <summary>Run the control's OnAssistEdit trigger — see LiveNavTestField.AssistEdit.</summary>
    public void AssistEdit() => _page.RaiseOnAssistEdit(_controlId);
    /// <summary>Run the control's OnDrillDown trigger — see LiveNavTestField.Drilldown.</summary>
    public void Drilldown() => _page.RaiseOnDrillDown(_controlId);
    public void Invoke() { }

    // An Option/Enum-bound control renders an ordinal as the text the control SHOWS, the same
    // spelling the Value getter answers with — issue #2367. BC's own ALAssertEquals/ALSetValue
    // strip an AL option value down to a bare ordinal before calling this (see
    // TestPageOptionValue.DisplayOrdinal for the exact chain), so leaving it as
    // Convert.ToString made AssertEquals compare the ordinal '2' against the control's
    // 'Pending Approval' and report a mismatch for the value the record actually held.
    // The Rec-bound sibling above had the identical gap; both are fixed together because both
    // Value getters already render captions, so either one left alone would keep disagreeing
    // with its own read side.
    public string ValueToString(object? value)
        => TestPageOptionValue.DisplayOrdinal(CurrentOption(), value,
               CurrentOption() is { } option ? _page.TryGetOptionCaptions(_controlId, option) : null)
           // #2795: the page-global half of the same rule — see the Rec-bound sibling above.
           ?? TestPageBooleanValue.FormatObject(value)
           // #2361: the page-global half of the blank-temporal rule.
           ?? TestPageBlankTemporalValue.FormatObject(value)
           ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    public string GetOption(int index)
        => CurrentOption() is { } option
            ? TestPageOptionValue.MemberAt(option, index,
                _page.TryGetOptionCaptions(_controlId, option))
            : string.Empty;
}
