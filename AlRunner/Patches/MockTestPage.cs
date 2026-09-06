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
    // Null for a page with no SourceTable (issue #2007) — a legal AL shape (StandardDialog
    // pickers/prompts bound to page globals). Every member that genuinely needs a row goes
    // through RequireRecord, which turns a would-be NRE into a named, loud refusal instead of
    // silently doing nothing; page-variable-bound field access never reaches here at all.
    private readonly NavRecord? _record;
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

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo)
        : this(record, controlIdToFieldNo, creatable: true, page: null) { }

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable)
        : this(record, controlIdToFieldNo, creatable, page: null) { }

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page)
        : this(record, controlIdToFieldNo, creatable, page, owner: null, pageId: 0) { }

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page, object? owner, int pageId)
    {
        _record = record;
        _controlIdToFieldNo = controlIdToFieldNo;
        _creatable = creatable;
        _page = page;
        _owner = owner;
        _pageId = pageId;
    }

    internal NavRecord? Record => _record;

    /// <summary>
    /// The record this operation genuinely needs, or a loud, named refusal instead of an NRE
    /// when the page has none (issue #2007: a page with no SourceTable — the StandardDialog
    /// shape — is legal AL, and only Rec-dependent members are affected; page-variable-bound
    /// field access resolves entirely through RunnerPageInstance's source-expression table and
    /// never calls this).
    /// </summary>
    protected internal NavRecord RequireRecord(string what)
        => _tornDown ? throw MakeTestPageNotOpenException()
        // The api carries no " — ": OutOfScopeMessage.TryParse cuts the api from the reason at
        // the FIRST one, so an api that spells the separator itself makes the untyped recovery
        // path report "TestPage page 60100" with "New() — testpage-modal-no-source-table — …"
        // as the reason. Same defect #2945 fixed for Feature Key Modify (#2999).
        : _record ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestPage page {_pageId} ({what})",
            "testpage-modal-no-source-table — this page has no SourceTable, so there is no "
            + "record-backed rowset for this operation. Controls bound to page variables are "
            + "supported; row navigation, filtering, Insert/Modify and Rec-bound field access "
            + "are not, because there is no record to act on. See docs/scope.md");

    // BC reports these in NavInsertDeniedPermissionException and friends. Answering 0/""
    // (the mock's values) is what produced "Insert is not allowed. Page = , Id = 0" — an
    // error that named no page at all.
    public override int PageId => _pageId;

    // TestPage.Editable() reaches here (NavTestPage.ALEditable => TestPage.RuntimeEditable).
    // A constant true made every `CurrPage.Editable(false)` invisible to the test that was
    // written to check it.
    public override bool RuntimeEditable => _staticEditable;

    // TestPage.Caption() (#1776). The base mock answered a constant empty string, which was
    // wrong for BOTH of a page's caption sources: the static `Caption = '…'` property AND a
    // runtime `CurrPage.Caption := '…'` assignment made from OnOpenPage. Both write the same
    // underlying NavForm.PageCaption — reading it here is what makes a single accessor answer
    // correctly whether or not the page ever touched CurrPage.Caption at all.
    public override string Caption => _page?.PageCaption ?? string.Empty;

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
        if (_tornDown) throw MakeTestPageNotOpenException();
        if (_parts.TryGetValue(controlId, out var cached)) return cached;

        if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
            Console.Out.WriteLine($"[MockTestPage.GetPart] controlId={controlId} pageId={_pageId} _page={(_page == null ? "null" : "set")} _page.Form={( _page?.Form == null ? "null" : _page.Form.GetType().FullName)}");

        // BOTH branches are runner gaps, which is why one factory serves them. The second one
        // reads like an AL-authoring error and is not: the AL compiler resolves a part by NAME
        // and emits its control id, so an id that reaches here always named a real part on the
        // real page. Finding no part for it means the runner's page metadata is incomplete.
        var definition = _page?.TryGetPartDefinition(controlId)
            ?? throw TestPageShapeGap.Part(
                $"TestPage part {controlId} (page {_pageId})",
                "the runner could not resolve this control to a subpage part"
                + (_page == null
                    ? "; no AL page object was built for the hosting page, so its part definitions "
                      + "are unavailable — see AlPageMetadataRegistry"
                    : "; the hosting page's metadata declares no part with this control id"));

        var partPageId = definition.PagePartID;
        if (_owner == null)
            throw TestPageShapeGap.Part(
                $"TestPage part {controlId} → page {partPageId}",
                "the hosting page was built without an ITreeObject owner, so the runner has "
                + "nothing to construct the part's own page under");

        var built = TestPageFactory.TryBuild(_owner, partPageId, out var why);

        // A PART is a page, so it gets the same three-way classification the TestPage handle
        // site gives a top-level page — see TestPageClientConstructionRule. This used to
        // collapse the first two answers: "TryBuild produced no record" was read as "this page
        // cannot be driven", and a part page that simply declares no SourceTable (a CardPart
        // whose controls bind to page globals — the info-box shape, ordinary legal AL) was
        // refused out-of-scope the moment a test touched it (issue #2195).
        //
        // THE REASON A RECORD-LESS PART IS SAFE. It is NOT "symmetric with #2090's host fix" —
        // that would be an argument from shape, and the host and the part are different
        // objects with different added behaviour. It is this:
        //
        //   The ONLY behaviour LiveNavTestPart adds over LiveNavTestPage is the SubPageLink.
        //   Every SubPageLink entry — field(), const() and filter() alike — names a field of
        //   the PART's OWN source table (link.FieldID is resolved against it — see SubPageLinks
        //   below). A part page that declares no source table therefore cannot express one,
        //   so `links` is necessarily EMPTY, ApplyLink has
        //   nothing to apply, and the wrapper degenerates to exactly LiveNavTestPage over a
        //   null record — the shape #2007 established, where every Rec-dependent member
        //   refuses BY NAME through RequireRecord instead of answering a default.
        //
        // "Necessarily", not "in the cases we tried": it is a property of what a SubPageLink
        // can refer to, which is why this does not need a per-part audit. Controls bound to
        // page globals resolve through RunnerPageInstance's source-expression table and never
        // reach a record at all, which is the whole point of the shape.
        //
        // Measured on real BC by corpus codeunit 60803 "Test Page NoSrc Part Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests commit ef52b7e9, PR #80), all eight
        // arms green on BC 27.5 and BC 28.3.
        //
        // FIXED (issue #2201): the part page object is now, where possible, the SAME
        // RunnerPageInstance the host's own AL reaches through CurrPage.<part> —
        // RunnerPageInstance.AdoptFromHost goes through BC's own NavForm.GetPart(int) on
        // the host, exactly the door the host's compiled AL uses. Only when that cannot
        // produce a live object (the host has no NavForm, the control names no part there,
        // or reifying the adopted object throws) does this fall back to the disconnected
        // instance TryBuild/TryBuildRecordless constructs, which is the ENTIRE previous
        // behaviour and stays exactly as faithful as it always was.
        NavRecord? partRecord;
        RunnerPageInstance? partPage;
        // Whether partPage came from AdoptFromHost — that path already raised the part's
        // OnOpenPage itself (once, at reification — see AdoptFromHost), so the fallback
        // raise below must not run a second time on an adopted instance.
        bool adopted;
        var partKind = TestPageClientConstructionRule.Resolve(
            recordBuilt: built != null,
            pageShapeKnown: RecordPatches.IsPageShapeKnown(partPageId),
            pageDeclaresSourceTable: RecordPatches.ResolvePageDeclaresSourceTableForAnyPage(partPageId));

        if (partKind == TestPageClientKind.LiveOverRecord)
        {
            partRecord = built!.Record;
            var fromHost = RunnerPageInstance.AdoptFromHost(_page?.Form, controlId, partPageId, partRecord, recordless: false);
            adopted = fromHost != null;
            partPage = fromHost ?? built.Page;
            // AdoptFromHost may have reused a record ALREADY bound on the adopted instance
            // (a SourceTableTemporary part the host already populated — see AdoptFromHost's
            // "alreadyLive" branch) instead of the fresh one just built above. This part's
            // OWN record must follow whichever one the live page object actually ended up
            // bound to, or navigation/Insert/Delete would act on an empty record nobody else
            // can see while the control tree reads the real one.
            if (adopted && fromHost!.Record is { } liveRecord) partRecord = liveRecord;
        }
        else if (partKind == TestPageClientKind.LiveRecordless)
        {
            partRecord = null;
            // No record and none needed. Both AdoptFromHost and TryBuildRecordless answering
            // null is a different failure — the runner has no metadata to build the part page
            // object from, so there would be no control tree either — and falls through to
            // the refusal below.
            var fromHost = RunnerPageInstance.AdoptFromHost(_page?.Form, controlId, partPageId, recordToBind: null, recordless: true);
            adopted = fromHost != null;
            partPage = fromHost ?? TestPageFactory.TryBuildRecordless(_owner, partPageId);
            if (partPage == null)
                throw TestPageShapeGap.Part(
                    $"TestPage part {controlId} → page {partPageId}",
                    PartNotLive(why));
        }
        else
        {
            throw TestPageShapeGap.Part(
                $"TestPage part {controlId} → page {partPageId}",
                PartNotLive(why));
        }

        // The parent record is only needed to evaluate FIELD SubPageLink pairs (issue #2053).
        // A part with no FIELD link never reads it — a CONST/FILTER link is evaluated against
        // a literal, and a FIELD link can only be declared against a parent SourceTable field,
        // so a SourceTable-less host (the Worksheet-dialog shape, legal AL) always lands in the
        // parent-less case. Demanding the record up front turned every part access on such a
        // host into a refusal the operation never required.
        var links = SubPageLinks(definition, partPageId);
        var part = new LiveNavTestPart(
            partRecord, RecordPatches.GetPageControlFieldMap(partPageId),
            RecordPatches.GetInsertAllowedForPage(partPageId), partPage, _owner, partPageId,
            parentRecord: LiveNavTestPart.AnyFieldLink(links) ? RequireRecord($"subpage part {controlId}") : null, links: links);
        // A part is never MarkOpened — BC opens the HOST, and the part comes up inside it —
        // so _staticEditable sat at its constructor default of true for every part, whatever
        // the host was opened as. That made a part of a read-only page report itself editable,
        // and (once the new-row line landed) would have offered a blank line on a page opened
        // with OpenView. Apply the same rule MarkOpened applies to a top-level page, with the
        // host's already-resolved editability standing in for the open mode.
        part.MarkPartOf(this);

        // OnOpenPage on the PART, and WHY IT IS RAISED HERE rather than anywhere more obvious.
        //
        // The obvious place is RunnerTestPageState.MarkOpened, which is where a top-level
        // page's OnOpenPage is raised, and where anyone looking for this will look first. It
        // cannot go there: MarkOpened runs when BC opens the HOST, and at that moment no part
        // exists — the runner builds parts LAZILY, on the first AL access, which is this
        // method. So this is the earliest moment a part's trigger CAN run, and since the part
        // is not observable before it, running it here is indistinguishable from BC's
        // "the subpage opens with its host".
        //
        // WHY IT IS PART OF THE #2195 FIX AND NOT A SEPARATE CONCERN. No part has ever had its
        // OnOpenPage raised, and that was invisible while every part had a source table: such
        // a part's observable state lives in the record, and the rowset is there with or
        // without the trigger. A part page with NO source table has no record — every one of
        // its controls is bound to a page global, and the part page's own AL is the ONLY thing
        // that ever puts a value in one. So lifting the out-of-scope refusal WITHOUT this
        // would have replaced a loud failure with a part whose every control reads blank, and
        // blank is indistinguishable from a legitimately empty value: the test goes green, or
        // fails one assertion later against a value it was never told was never computed.
        // That is precisely the silent default `.claude/rules/loud-failures.md` exists to
        // prevent, and it is why removing the throw without this line would have made the
        // runner LESS honest, not more.
        //
        // The corpus arms that read a specific value rather than merely "not refused" are what
        // pin it: codeunit 60803's controls read 'Hello', which only its OnOpenPage can set
        // (StefanMaron/BusinessCentral.AL.Language.Tests commit ef52b7e9, green on BC 27.5 and
        // BC 28.3).
        //
        // Raised BEFORE the part is cached so a re-entrant GetPart during the trigger cannot
        // observe a half-built part; raised after MarkPartOf so the trigger sees the
        // editability the host resolved.
        //
        // NOT raised again when `adopted` is true: AdoptFromHost already raised it, exactly
        // once, at the moment it reified the host's own shared instance (issue #2201) —
        // raising it a second time here would clobber whatever the host's own AL (or an
        // earlier TestPage touch) already wrote through that same instance.
        if (!adopted) part.RaiseOnOpenPage();

        // Position the part on its SubPageLink-matched row and run OnAfterGetRecord/
        // OnAfterGetCurrRecord — issue #2677, measured against real BC (corpus PR
        // StefanMaron/BusinessCentral.AL.Language.Tests#141): a linked part loads on EVERY
        // GetPart touch this method reaches (see ReloadLinkedRow's doc comment for why this
        // is deliberately NOT once-guarded — a GetPart touch normally happens only once per
        // part per TestPage anyway, since the `_parts` cache at the top of this method
        // short-circuits repeats; what actually keeps a linked part in sync across host
        // navigation is <see cref="LiveNavTestPage.Loaded"/> calling this again on every
        // parent row load — see that method).
        //
        // A recordless part (LiveRecordless branch) has no cursor — ReloadLinkedRow no-ops
        // on a null Record — so its OnOpenPage (just raised, or raised inside AdoptFromHost)
        // is the only trigger such a part gets, exactly as before.
        part.ReloadLinkedRow();

        _parts[controlId] = part;
        return part;
    }

    // How the page was closed. BC's RunHandlerWithException reads this off the page right
    // after a [ModalPageHandler] returns, and it is what RunModal() reports back to the AL
    // that opened the page. The mock answers a constant OK, so a handler that cancelled was
    // indistinguishable from one that confirmed — every AL `if RunModal() = Action::OK`
    // took the OK branch regardless.
    private FormResult? _invokedFormResult;

    public override FormResult FormResult => _formResult;

    /// <summary>
    /// How the page was closed: what a built-in action recorded, or — when the handler
    /// invoked nothing at all — what the platform substitutes for it.
    ///
    /// The substitute is MODE-DEPENDENT and the two halves cannot be derived from one another.
    /// Measured on real BC 28.4.53241.0 (corpus "MQC Tests", codeunit 60276, arms b and e): a
    /// handler that returns without invoking anything leaves a plain modal reporting OK and a
    /// LookupMode(true) modal reporting LookupCancel — so OnQueryClosePage sees OK on the one
    /// and LookupCancel on the other, and RunModal() returns the same. A flat OK default made
    /// every unattended lookup read as a confirmed pick.
    /// </summary>
    private FormResult _formResult
        => _invokedFormResult
           ?? (_page?.LookupMode == true ? FormResult.LookupCancel : FormResult.OK);

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
        // Torn down, OR the page has already been closed from AL while the handler was still
        // running -- CurrPage.Close() from an action's OnAction. A handler that then reaches
        // for the built-in OK()/Cancel() is asking a page that no longer exists to close
        // itself again, and real BC refuses it by name rather than closing twice: "The
        // TestPage is not open." (corpus codeunit 60296 "MQC Self Close Tests", measured on a
        // service tier). The instance the handler holds is not the one that performed the
        // close -- BC's ClosePage path builds its own -- so the local teardown flag cannot see
        // it and BC's own form state is what has to be asked (issue #3091).
        if (_tornDown || RunnerPageInstance.WasClosedFromAl(_page?.Form))
            throw MakeTestPageNotOpenException();
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
            _page._invokedFormResult = _result;
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
    ///
    /// Issue #1923: <c>_page</c> is null whenever the base page has no compiled type/captured
    /// metadata for the runner to build a RunnerPageInstance from — the case for a page that
    /// ships PRECOMPILED (e.g. Base App "Item Attributes"). A pageextension THIS bundle
    /// compiled can still own <paramref name="actionId"/>'s OnAction even though the base page
    /// itself is unreachable, so that case gets one more chance (ExtensionOnlyTestAction)
    /// before falling all the way back to the no-op mock.
    /// </summary>
    public override ITestAction GetAction(int actionId)
    {
        if (_tornDown) throw MakeTestPageNotOpenException();
        if (_page == null)
        {
            // ExtensionOnlyTestAction dispatches through a pageextension's OWN NavFormExtension
            // instance, which is built over the record — a page with no SourceTable at all
            // (this class's null-_record case) has nothing to build that from, so it falls
            // through to the no-op mock rather than the extension path.
            if (_record != null && _owner != null && RecordPatches.GetPageExtensionIdsForPage(_pageId).Count > 0)
                return new ExtensionOnlyTestAction(this, _owner, _record, _pageId, actionId);
            return base.GetAction(actionId);
        }
        if (!_liveActions.TryGetValue(actionId, out var action))
            _liveActions[actionId] = action = new LiveNavTestAction(this, _page, actionId);
        return action;
    }

    /// <summary>
    /// The SubPageLink as compiled entries. All three kinds AL can declare are applied
    /// (issue #2469): FIELD (<c>ReportId = field(ReportId)</c>) as a SetRange to the parent's
    /// current value, CONST (<c>Kind = const(Attachment)</c>) as a single-value filter on the
    /// literal, FILTER (<c>Status = filter(Open | Released)</c>) as the expression itself.
    /// Before this, CONST and FILTER refused out-of-scope by name — but they are ordinary AL
    /// (10.9% of Base Application 28.1's SubPageLink entries, measured in the issue), not an
    /// unsupported surface, and the refusal cost 19 Tests-ERM tests in one bucket alone.
    ///
    /// What arrives here is the COMPILER's representation, never AL text — measured on BC
    /// 28.1's compiler output for corpus codeunit 60324 "TSPL Tests": an option member is its
    /// ORDINAL (<c>const(Attachment)</c> → <c>1</c>, <c>filter(Open | Released)</c> →
    /// <c>1|2</c>), <c>const(Database::"TSPL Header")</c> is the table id, and
    /// <c>const('SPECIAL')</c> on a Code field is the bare text <c>SPECIAL</c>.
    /// RecordPatches.DependencyPageMetadataXml produces the same shape for a precompiled
    /// dependency's page, so one consumer serves both routes.
    ///
    /// A part field id of 0 (a dependency page whose part field name could not be resolved —
    /// see DependencyPageMetadataXml.EmitSubFormLinkXml) refuses by name for every kind rather
    /// than filtering on no field: an unfiltered part shows other rows' children, which is a
    /// wrong answer, not a missing one.
    /// </summary>
    /// <summary>
    /// The detail both "could not be driven live" refusals report. They are ONE shape reached
    /// down two branches — the recordless path and the fall-through — and they carried
    /// byte-identical reason text written out twice, so the same gap could drift into claiming
    /// two different things depending on which branch found it (#2999).
    /// </summary>
    private static string PartNotLive(string? why)
        => "the part's own page could not be driven live" + (why == null ? string.Empty : $" ({why})");

    private static SubPageLinkEntry[] SubPageLinks(
        Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition definition, int partPageId)
    {
        var links = new List<SubPageLinkEntry>();
        foreach (var link in definition.SubFormLink ?? new List<Microsoft.Dynamics.Nav.Types.Metadata.FilterDefinition>())
        {
            // Both of these are RUNNER gaps, not BC-shape gaps, and the distinction is measured
            // rather than assumed: DependencyPageMetadataXml.EmitSubFormLinkXml DELIBERATELY
            // writes FieldID 0 and a non-numeric FilterValue when it cannot resolve a field
            // NAME to an id, precisely so these two refusals fire. The read succeeded; the
            // answer is about the runner's own metadata reconstruction, which is the line
            // BcShapeGapException draws (#2995).
            if (link.FieldID <= 0)
                throw TestPageShapeGap.PartLink(
                    $"TestPage part → page {partPageId} SubPageLink ({link.FilterType})",
                    $"the part's own field this link constrains could not be resolved "
                    + $"(FieldID {link.FieldID}, {link.FilterType} '{link.FilterValue}')");
            switch (link.FilterType)
            {
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD:
                    if (!int.TryParse(link.FilterValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentFieldNo))
                        throw TestPageShapeGap.PartLink(
                            $"TestPage part → page {partPageId} SubPageLink",
                            $"a FIELD link's value must be the parent's field number, "
                            + $"but this one is '{link.FilterValue}'");
                    links.Add(new SubPageLinkEntry(link.FieldID, link.FilterType, parentFieldNo, string.Empty));
                    break;
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.CONST:
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FILTER:
                    links.Add(new SubPageLinkEntry(link.FieldID, link.FilterType, 0, link.FilterValue ?? string.Empty));
                    break;
                default:
                    // A BC SHAPE GAP, not a scope claim and not a runner gap — the one site in
                    // this file where the runner READ BC's own metadata and could not interpret
                    // what it held (#2946/#2995). FilterType has exactly FIELD/CONST/FILTER:
                    // measured on BC 28.1's Microsoft.Dynamics.Nav.Types.dll, and the runner's
                    // own EmitSubFormLinkXml writes only those three spellings, so a fourth
                    // value can ONLY have come from BC's compiled page metadata. That makes it a
                    // property of which BC build is on disk — it could be true on one matrix leg
                    // and false on another in the same run — which is exactly what may not be
                    // declarable as an expected out-of-scope surface. Refuse rather than treat
                    // it as one of the three and filter wrongly.
                    throw new AlRunner.Infrastructure.BcShapeGapException(
                        $"TestPage part → page {partPageId} SubPageLink",
                        "Microsoft.Dynamics.Nav.Types.Metadata.FilterType",
                        $"holds {link.FilterType}, which is not one of FIELD/CONST/FILTER; this part "
                        + $"links field {link.FieldID} by {link.FilterType} '{link.FilterValue}', and "
                        + "filtering it as any of the three would show the wrong rows rather than none");
            }
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

    // Set when an unhandled error propagates out of the page's own record-positioning
    // trigger (OnAfterGetRecord) while this TestPage is already open — see Loaded() below.
    //
    // Measured against a real BC service tier (27.5, 28.3, 28.4; issue #2656): an unhandled
    // error raised there tears down the TestPage's underlying client session. Every
    // subsequent call on the SAME TestPage variable then raises BC's own
    // "The TestPage is not open." — not the trigger's own error text — including the
    // navigation call itself, Close(), and a plain field read. Deliberately distinct from
    // _opened (which BC's own NavTestPageBase.Open()/Close() guards read): a torn-down page
    // must still make Close() forward into this class (real BC's Close() THROWS after
    // teardown, it does not silently no-op the way it would for a page that was simply never
    // opened), so _opened stays true and this flag alone gates the refusal.
    //
    // This is NOT a blanket "any unhandled trigger error tears the page down" rule — measured
    // the same way, an unhandled error from OnValidate (field validation) or OnAction
    // (action invocation) propagates with its own error text and leaves the page open. Only
    // Loaded() (the record-positioning trigger) sets this flag.
    private bool _tornDown;



    // Set only around the page-construction-time initial positioning call (MarkOpened /
    // RunnerTestClientSession.GetPage's own MoveFirst()). MarkOpened's caller wraps it in a
    // blanket `catch { }` that would swallow whatever Loaded() throws there; GetPage's is not
    // similarly guarded on the runner side (its caller is precompiled BC dispatch via
    // TestClientProxy<ITestPage>.Proxy, not audited here). Either way, teardown must not apply
    // during this call: the page never finished a first successful position, so treating a
    // failure there as "the page tore down" would leave every LATER, otherwise-unrelated call
    // on a freshly-adopted page wrongly answering "The TestPage is not open." -- for MarkOpened
    // specifically, that would follow a failure that never became AL-visible in the first
    // place (a pre-existing, separate gap: real BC's OpenView() propagates that first row's own
    // trigger error, catchable by asserterror, rather than swallowing it -- not this issue's
    // scope).
    private bool _suppressTeardownOnLoad;

    /// <summary>
    /// The page-construction-time initial positioning call -- see _suppressTeardownOnLoad.
    /// </summary>
    internal bool MoveFirstDuringOpen()
    {
        _suppressTeardownOnLoad = true;
        try { return MoveFirst(); }
        finally { _suppressTeardownOnLoad = false; }
    }

    /// <summary>
    /// Run the "a row became the page's current row" sequence for the row the page is ALREADY
    /// on, without moving the cursor — <see cref="Loaded"/>'s OnAfterGetRecord /
    /// OnAfterGetCurrRecord, the xRec before-image, and the linked-part refresh.
    ///
    /// Needed because a page handed to a [ModalPageHandler] / [PageHandler] is constructed
    /// already-open by BC's ShowDialog, so RunnerTestPageState.MarkOpened — the code that runs
    /// the open sequence for a page the AL test opened itself — never runs on that path.
    /// RunnerTestClientSession.GetPage compensated with MoveFirstDuringOpen, but only for a
    /// record nothing had positioned yet: a caller that opened the page ON a specific row
    /// (<c>PAGE.RunModal(id, Rec)</c>) must not have that row silently re-queried away (corpus
    /// CU60848 RunModal_OpensOnTheRecordSetByTheCaller). That guard is right about the CURSOR
    /// and wrong about the TRIGGERS: the row-load triggers belong to every row a page shows,
    /// however it came to be on it.
    ///
    /// Measured on Base Application page 403 "Purchase Order Statistics", whose totals are
    /// computed in RefreshOnAfterGetRecord() off OnAfterGetRecord and NOT in OnOpenPage: opened
    /// modally on a caller-positioned Purchase Header, it received OnOpenPage (raised by BC's
    /// own OpenForm inside RunnerModalDispatch.FormRunModal) but never OnAfterGetRecord, so
    /// every total it showed was its type default. See issue #2797.
    ///
    /// Shares MoveFirstDuringOpen's teardown suppression for the same reason: this is the
    /// page-construction-time row load, not a navigation call the AL test made, so an AL error
    /// raised in the trigger must propagate as itself rather than being converted into BC's
    /// "The TestPage is not open."
    /// </summary>
    internal bool MarkRowLoadedDuringOpen()
    {
        _suppressTeardownOnLoad = true;
        try { return Loaded(found: true); }
        finally { _suppressTeardownOnLoad = false; }
    }

    // Real BC's own exception for this ("The TestPage is not open.") is not part of the
    // runner's own type surface — construct BC's own NavNCLDialogException (the same
    // AL-catchable-by-asserterror mechanism every other faithful platform error in this file
    // uses; see e.g. HelperShims.MakeNavDrilldownActionNotSupportedException) with BC's exact
    // wording so `asserterror` + Assert.ExpectedError('The TestPage is not open') behaves the
    // same here as against a real service tier.
    private static System.Exception MakeTestPageNotOpenException(System.Exception? original = null)
    {
        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        const string msg = "The TestPage is not open.";
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null)
            {
                var ex = (System.Exception)ctor.Invoke(new object[] { msg });
                // Not AL-visible (asserterror / GetLastErrorText only see the outer message,
                // matching real BC) -- kept only so a runner-side stack trace can still show
                // what actually failed inside the trigger.
                if (original != null) ex.Data["OriginalTriggerError"] = original;
                return ex;
            }
        }
        return new System.InvalidOperationException(msg, original);
    }

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
        _staticEditableOverride = viewMode != Microsoft.Dynamics.Nav.Types.Metadata.ViewMode.View
                                  && (_page?.PageEditable ?? true);
    }

    // Set only by MarkOpened — i.e. only for a page the TEST opened, where the open MODE is
    // what decides editability. Null everywhere else, which is why _staticEditable below has
    // to have an answer of its own rather than a default.
    private bool? _staticEditableOverride;

    // The host, for a subpage part. A part is reached through its host and is editable only
    // if the host is; see _staticEditable.
    private LiveNavTestPage? _editabilityHost;

    /// <summary>
    /// The page's STATIC editability: the open mode (when the test opened it), narrowed by the
    /// page's own declared <c>Editable</c>, and by its host's when it is a part.
    ///
    /// This used to be a plain field defaulting to true, written only by MarkOpened — and
    /// MarkOpened only ever runs for a page the test opens ITSELF. Every page BC hands to a
    /// [ModalPageHandler] / [PageHandler], and every subpage part, therefore reported itself
    /// editable no matter what it declared: an <c>Editable = false</c> page opened through
    /// RunModal answered TestPage.Editable() = true, and (once the new-row line landed) would
    /// have offered a blank line to type into on a page nobody can type into.
    ///
    /// Computed rather than snapshotted because a part is built lazily, on first access, and
    /// nothing orders that against its host being opened.
    /// </summary>
    private bool _staticEditable
        => TestPageNewRowLineRule.ResolveStaticEditable(
            _staticEditableOverride, _editabilityHost?._staticEditable, _page?.PageEditable ?? true);

    /// <summary>
    /// Bind a subpage part to its host for editability. Deliberately does NOT touch _opened:
    /// that flag drives BC's Open/Close guards, and a part is opened and closed with its host.
    /// </summary>
    internal void MarkPartOf(LiveNavTestPage host) => _editabilityHost = host;

    /// <summary>Run the page's OnOpenPage — see RunnerTestPageState.MarkOpened.</summary>
    internal void RaiseOnOpenPage() => _page?.RaiseOnOpenPage();

    /// <summary>
    /// Reach every subpage PART this page declares, the way <see cref="RunnerTestPageState.MarkOpened"/>
    /// calls it: right after the host's own OnOpenPage, before the host's first row is found.
    /// Issue #2677, corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141 — real BC
    /// materialises a page's declared FactBoxes as part of opening, with nothing in the
    /// host's own AL ever referencing <c>CurrPage.&lt;part&gt;</c>. <c>GetPart</c> raises the
    /// part's OWN OnOpenPage and, via <c>ReloadLinkedRow</c>, attempts its initial row load —
    /// which finds nothing yet (the host's own record is not positioned until MoveFirst runs
    /// right after this returns), so the part's OnAfterGetRecord/OnAfterGetCurrRecord fires
    /// for the first time from <see cref="Loaded"/>'s own refresh once the host DOES have a
    /// row, not from here.
    ///
    /// Each control is isolated in its own try/catch: a part the runner cannot build (a
    /// precompiled Base App page the runner has no metadata for, an unsupported shape) must
    /// not prevent the HOST from opening, or every card carrying one unbuildable FactBox
    /// would refuse OpenView entirely. An AL test that genuinely touches such a part still
    /// gets the normal named refusal through <see cref="GetPart"/> — this only skips the
    /// EAGER attempt, it does not swallow the refusal a real touch would raise.
    /// </summary>
    internal void EagerlyBuildParts()
    {
        if (_page == null) return;
        foreach (var controlId in _page.AllPartControlIds())
        {
            try { GetPart(controlId); }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                    Console.Out.WriteLine($"[MockTestPage.EagerlyBuildParts] control {controlId} on page {_pageId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public override bool IsOpened() => _opened;

    // TestPage.New() reaches ITestPage.InsertEmptyRow. BC's client model is "start a blank
    // row now, persist it once the cursor leaves it (or the page closes)" — the SetValue
    // calls in between write into the record buffer. The base mock no-ops, which silently
    // dropped every insert made through a TestPage; a LIVE page has a real record, so it
    // must initialise the buffer and remember to flush it.
    private bool _pendingNewRow;

    public override void InsertEmptyRow(bool beforeCurrent)
    {
        // A page with no SourceTable has no rowset to insert into at all — refuse by name
        // before touching any of the state below, rather than NRE-ing inside CaptureInsertPosition.
        RequireRecord("New()");

        // New() from the new-row line starts the row explicitly; the draft bookkeeping is
        // superseded by the CaptureInsertPosition below, and its saved return position must
        // not survive to drag the cursor back off the row being created.
        _onNewRowLine = false;
        _newRowLineReturnPosition = null;

        FlushPendingNewRow();   // starting a second row persists the first

        // The rows around the insert decide the new row's AutoSplitKey number, and the row
        // the cursor sits on is about to be wiped by NewRecord's ALInit — so the position is
        // read NOW and the number computed from it at flush time (ProposeAutoSplitKey).
        CaptureInsertPosition();

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
            // Non-null: guaranteed by the RequireRecord guard at the top of this method.
            _record!.ALInit();
            // The tail of NavForm.NewRecordAsync is `OldRecord.ALAssign(SourceTable)`, and
            // TryNewRecord runs it on the page path. Record-only mode never reaches BC's
            // method at all, so the snapshot RowChangedSinceNewRecord compares against has to
            // be taken here or the gate below would measure this row against some earlier one.
            _record!.OldRecord.ALAssign(_record);
        }

        _pendingNewRow = true;
    }

    /// <summary>
    /// BC's own "is this row worth saving" gate, lifted from <c>NavForm.SaveRecordAsync</c>:
    /// <c>!SafeSourceTable.CompareAllNormalFields(SafeSourceTable.OldRecord, null)</c>. When it
    /// answers false, SaveRecordAsync falls straight through to its UpdateRequest and writes
    /// NOTHING — no SplitKey, no OnInsertRecord, no Insert.
    ///
    /// The comparison works because <c>NewRecordAsync</c> ends with
    /// <c>OldRecord.ALAssign(SourceTable)</c>, taken AFTER <c>InitializeFieldsFromFilters</c>
    /// and AFTER <c>OnNewRecord</c>. So the baseline is the row exactly as <c>New()</c> left it,
    /// and only a write the test itself made can move it — which is what makes a row nobody
    /// filled in disappear when the card closes, while <c>New()</c> + <c>SetValue</c> + close
    /// still writes a row.
    ///
    /// <para><c>fieldsInitializedFromFilters</c> is passed as null deliberately, and it is not a
    /// simplification: in <c>CompareAllNormalFields</c> that set FORCES a difference rather than
    /// excluding one, so passing it would report every filter-stamped row as changed and save
    /// it. Which of the two SaveRecordAsync overloads the close path behaves like is settled by
    /// measurement, not by reading: corpus CU60648
    /// <c>New_NothingTouched_IsDiscardedWhenTheCardCloses</c> does <c>New()</c> on a part whose
    /// linked field IS in the primary key — so the stamp definitely happened — and real BC
    /// 27.0 through 28.4 still reports the row gone. That is only possible with
    /// <c>detectChangeFromFieldsInitializedFromFilters: false</c>, which is what the no-argument
    /// <c>SaveRecordAsync()</c> (the one <c>NavForm.UpdateCoreAsync</c> uses) passes.</para>
    ///
    /// <para>Not applied to <see cref="FlushPendingModify"/>, and that is BC's asymmetry rather
    /// than an omission: on the modify half SaveRecordAsync ORs the comparison with
    /// <c>calledFromALCode &amp;&amp; RecordImplementation.HasChangedFields</c>, and
    /// <c>_pendingModify</c> is only ever set by <see cref="MarkEdited"/> — i.e. exactly when a
    /// control assigned a field. Whether BC's <c>HasActualChangedValues()</c> also demands the
    /// value actually MOVED is unmeasured; see issue #3055.</para>
    /// </summary>
    private bool RowChangedSinceNewRecord()
    {
        // Non-null: only reached from FlushPendingNewRow, gated by _pendingNewRow, which is
        // only set after InsertEmptyRow's RequireRecord guard (or by MarkEdited, which is only
        // wired to a Rec-bound control and so implies a record too).
        var record = _record!;
        return !record.CompareAllNormalFields(record.OldRecord, null);
    }

    internal void FlushPendingNewRow()
    {
        if (!_pendingNewRow) return;
        _pendingNewRow = false;
        // A row New() started and nothing wrote to is not persisted — BC discards it rather
        // than inserting a blank line, so a subpage part that showed 2 rows still shows 2.
        // See RowChangedSinceNewRecord for the mechanism and what measured it.
        //
        // The captured insert position is dropped with the row: it describes bounds read at
        // THIS New()'s cursor, and leaving it armed would offer them to the next insert, which
        // may be on another row or another part entirely.
        if (!RowChangedSinceNewRecord()) { _insertPositionCaptured = false; return; }
        // AutoSplitKey, in BC's own order: SplitKey, then OnInsertRecord, then the record's
        // Insert (NavForm.SaveRecordAsync / NavForm.InsertAsync(belowXRec) both do exactly
        // this). Skipping it left the last primary-key field at its Init() default, so a page
        // whose whole numbering scheme is AutoSplitKey — every editable line grid in BC —
        // wrote its first row at line no. 0 and could not write a second one at all: the same
        // key, so the insert failed on a duplicate. It is a no-op inside BC's own guard for a
        // page that does not declare the property.
        ProposeAutoSplitKey();
        _page?.SplitKey();
        // OnInsertRecord is the page's last word before the row exists, and its RETURN VALUE
        // is a veto — a page can refuse the insert outright. Running it and discarding the
        // answer would be worse than not running it: the row lands anyway, but now it also
        // carries whatever the trigger wrote on its way to saying no.
        if (_page != null && !_page.RaiseOnInsertRecord(false)) return;
        // runApplicationTrigger: true. Inserting a row from a page runs the table's OnInsert, the
        // same as Rec.Insert(true) — that trigger is where a table assigns its number series,
        // stamps its own derived fields, and enforces what it will not accept. Passing false
        // wrote a row the table had never agreed to.
        // Non-null: _pendingNewRow is only ever set true by InsertEmptyRow, which refuses by
        // name first when the page has no record — see RequireRecord there.
        _record!.ALInsertAsync(DataError.TrapError, true, false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Abandon an in-progress new row without writing it — how Cancel closes. Clears the
    /// captured insert position for the same reason FlushPendingNewRow's discard branch does:
    /// the bounds belong to the row being thrown away, and an armed capture would be consumed
    /// by whatever inserts next.
    /// </summary>
    internal void DiscardPendingNewRow()
    { _pendingNewRow = false; _pendingModify = false; _onNewRowLine = false; _newRowLineReturnPosition = null; _insertPositionCaptured = false; }

    // BC's AutoSplitKey increment. Named NavForm.AutoSplitKeyIncrement there, and the same
    // literal in the client's AutoKeyGenerator — both sides of the wire agree on 10000.
    private const int AutoSplitKeyIncrement = 10000;

    /// <summary>
    /// Do the CLIENT half of AutoSplitKey: work out the key the new row should get and offer it
    /// to BC's <c>NavForm.SplitKey()</c> as <c>AutoKeyValue</c>. SplitKey still owns the answer —
    /// it validates the proposal against the table and falls back to its own arithmetic if the
    /// key is taken — but without a proposal it has nothing to compute from.
    ///
    /// WHY THE RUNNER HAS TO DO THIS AT ALL
    ///   SplitKey's inputs are all client-supplied: <c>AutoKeyValue</c>, and the
    ///   <c>InsertLowerBoundBookmark</c> / <c>InsertUpperBoundBookmark</c> pair naming the rows
    ///   the new one is being inserted between. On a service tier those come off the repeater's
    ///   loaded rows (<c>NavRecordStateHandler.GetUpperAndLowerRowEntryBookmarks</c> and
    ///   <c>AutoKeyGenerator.GenerateKey</c>) and travel in <c>NavRecordState</c>. This class IS
    ///   the client, so all three were null on every insert and
    ///   <c>CalculateAutoSplitKeyValue(null, null)</c> answered a flat 10000 — the same constant
    ///   for every row, derived from no data at all. On an empty grid that is one interval low;
    ///   on a grid whose rows start anywhere else it puts the new row BEFORE them (a grid holding
    ///   a line at 50000 got 10000, not 60000).
    ///
    /// WHAT BC'S CLIENT COMPUTES
    ///   <c>AutoKeyGenerator.CalculateNumericKeyValue</c> is
    ///   <c>rangeStart + (draftRowsBefore + 1) * 10000</c>, where <c>rangeStart</c> is the key of
    ///   the nearest NON-draft row before the insertion point (0 when there is none) and
    ///   <c>draftRowsBefore</c> counts the unsaved rows between the two.
    ///
    /// WHY AN EMPTY GRID STARTS AT 20000 AND NOT 10000
    ///   Because <c>draftRowsBefore</c> is 1 there, not 0. An insertable repeater always carries a
    ///   trailing blank row past its data — <c>DraftLinePattern.MakeDraftLines</c> adds one as soon
    ///   as the binding manager is filled, including when it filled with nothing — and
    ///   <c>TestPageProxy.InsertEmptyRow</c> inserts the test's row AFTER the current one
    ///   (<c>InsertBehavior = RowUpdateBehavior.After</c>, whatever <c>beforeCurrent</c> says). On
    ///   an empty grid the current row is that placeholder, so the test's first row is the SECOND
    ///   draft and takes the second interval: 0 + 2 * 10000. The placeholder itself is never
    ///   persisted — nothing edits it — which is why no row at 10000 ever appears. On a grid that
    ///   already has data the current row is a real one, the placeholder sits after the new row,
    ///   and the count is 0: last + 1 * 10000. Both are measured on real BC 27.5 and 28.3 by
    ///   corpus CU60922.
    ///
    /// THE RUNNER'S INSERTION POINT
    ///   The row the cursor sits on when New() is called, read by
    ///   <see cref="CaptureInsertPosition"/> before NewRecord wipes it: <c>rangeStart</c> is
    ///   that row's key (the last row of the filtered set when the page holds no cursor),
    ///   <c>rangeEnd</c> is the next row of the same parent when the insert lands mid-grid,
    ///   and the placeholder draft is counted where the measurements put it — BEFORE the
    ///   insert on an empty grid (the 20000), AFTER it when the insert is at the end of a
    ///   non-empty rowset. That last count is load-bearing and was measured, not derived: a
    ///   grid holding one line at -10000 numbers the next row -6667 on real BC 27.5/28.3
    ///   (corpus CU60929) — the range up to zero split in THREE, the trailing placeholder
    ///   taking the third share. Mid-grid the placeholder sits beyond <c>rangeEnd</c> and
    ///   does not participate, which the measured -1 for a -10000..10000 insert pins.
    /// </summary>
    private void ProposeAutoSplitKey()
    {
        if (_page == null || !_page.NeedsAutoSplitKey) return;
        _page.SetAutoKeyValue(ClientAutoKeyValue());
    }

    // The insert position CaptureInsertPosition read at New() time, consumed at flush time.
    // Null bounds are meaningful (no saved row on that side), so a separate flag records
    // whether a capture happened at all.
    private object? _insertRangeStart;
    private object? _insertRangeEnd;
    private int _insertDraftRowsBefore;
    private int _insertDraftRowsAfter;
    private bool _insertPositionCaptured;

    /// <summary>
    /// Read the rows around the insertion point — the client half of AutoSplitKey that must
    /// run at New() time, because NewRecord's ALInit erases the cursor row it reads.
    /// </summary>
    private void CaptureInsertPosition()
    {
        _insertPositionCaptured = false;
        if (_page == null || !_page.NeedsAutoSplitKey) return;
        // Non-null: only reached from InsertEmptyRow, which refuses by name first when the
        // page has no record — see RequireRecord there.
        var record = _record!;
        // The AutoSplitKey field is the LAST field of the primary key — BC picks it the same
        // way inside SplitKey, so a page whose key shape the runner read differently would
        // number a different field than BC validates.
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return;
        var keyFieldNo = primaryKey.KeyFieldsList[primaryKey.KeyFieldCount - 1].FieldNo;

        _insertRangeStart = null;
        _insertRangeEnd = null;
        _insertDraftRowsBefore = 0;
        _insertDraftRowsAfter = 0;

        // Cloned with reset:false so it carries the page's filters (a subpage part's
        // SubPageLink above all: without it this would walk the lines of SOME OTHER header)
        // and cannot disturb the cursor the page is on.
        using var probe = record.CloneRecord(record.Parent, reset: false, keepCompany: true);

        // "The cursor sits on a saved row" is decided the way SplitKey itself decides it — a
        // row with the cursor's ALRecordId exists. With no cursor row the client viewport's
        // insert goes after the LAST row of the set (BC's own ALFindLast over the page's
        // filters); with no rows at all the grid is empty.
        var positioned = probe.ExistsAsync(probe.ALRecordId).AsTask().GetAwaiter().GetResult()
            || probe.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (positioned)
        {
            _insertRangeStart = Unwrap(probe.GetFieldValue(keyFieldNo));
            _insertRangeEnd = NextRowKeyInSequence();
            // At the end of the rowset the trailing blank placeholder row sits AFTER the
            // insert and shares the range; mid-grid it sits beyond rangeEnd and does not.
            // Measured, not derived: -6667 (not -5000) after a single line at -10000.
            _insertDraftRowsAfter = _insertRangeEnd == null ? 1 : 0;
        }
        else
        {
            // Empty grid: the placeholder is the row the insert lands AFTER, so it burns the
            // first interval — the measured 20000 for a first line (corpus CU60922).
            _insertDraftRowsBefore = 1;
        }
        _insertPositionCaptured = true;

        // The next row of the SAME parent, or null when the cursor row ends its sequence —
        // the prefix-compare mirror of NavForm.IsPositionedAtEndOfSequence: iteration is
        // unfiltered primary-key order, so "next row belongs to another parent" shows as its
        // other key fields changing.
        object? NextRowKeyInSequence()
        {
            var prefix = new object?[primaryKey.KeyFieldCount - 1];
            for (var i = 0; i < prefix.Length; i++)
                prefix[i] = Unwrap(probe.GetFieldValue(primaryKey.KeyFieldsList[i].FieldNo));
            if (probe.ALNext() <= 0) return null;
            for (var i = 0; i < prefix.Length; i++)
                if (!Equals(Unwrap(probe.GetFieldValue(primaryKey.KeyFieldsList[i].FieldNo)), prefix[i]))
                    return null;
            return Unwrap(probe.GetFieldValue(keyFieldNo));
        }
    }

    private object? ClientAutoKeyValue()
    {
        if (!_insertPositionCaptured) return null;
        _insertPositionCaptured = false;
        // Non-null: only reached from ProposeAutoSplitKey/FlushPendingNewRow, both gated by
        // _pendingNewRow, which is only set by InsertEmptyRow after its RequireRecord guard.
        var record = _record!;
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return null;
        var keyFieldNo = primaryKey.KeyFieldsList[primaryKey.KeyFieldCount - 1].FieldNo;

        // The key field's CLR type steers the arithmetic, read off the freshly initialised
        // buffer so the proposal is typed like the field: SplitKey feeds it to
        // NavValue.CreateNavValueFromObject, which converts per the field's NCL type, and an
        // Int32 offered for a BigInteger or Decimal key is a different value than BC's
        // client would have sent.
        var draftRowCount = _insertDraftRowsBefore + 1 + _insertDraftRowsAfter;
        return Unwrap(record.GetFieldValue(keyFieldNo)) switch
        {
            int => Box(CalculateClientAutoKey<int>(
                (int?)_insertRangeStart, (int?)_insertRangeEnd, draftRowCount, _insertDraftRowsBefore)),
            long => Box(CalculateClientAutoKey<long>(
                (long?)_insertRangeStart, (long?)_insertRangeEnd, draftRowCount, _insertDraftRowsBefore)),
            decimal => Box(CalculateClientAutoKey<decimal>(
                (decimal?)_insertRangeStart, (decimal?)_insertRangeEnd, draftRowCount, _insertDraftRowsBefore)),
            // GUID: BC's client and SplitKey both just mint a fresh Guid, so no proposal adds
            // nothing. Unsupported key types: SplitKey must be the one to throw, so the AL
            // sees BC's message.
            _ => null,
        };

        static object? Box<T>(T? value) where T : struct => value.HasValue ? value.Value : null;
    }

    /// <summary>
    /// Verbatim port of the client's <c>AutoKeyGenerator.CalculateNumericKeyValue</c>
    /// (Microsoft.Dynamics.Nav.Client.UI.dll) — the algorithm that decides what number a new
    /// grid row gets on a real service tier. Ported rather than invoked because constructing
    /// the real generator needs a live client ColumnBinder; the arithmetic itself is
    /// self-contained. Adjudicated against real BC 27.5/28.3 by corpus CU60922 and CU60929:
    /// append, empty-grid, wide-gap cap, zero-crossing and the placeholder-in-the-divisor
    /// cases are all pinned by measurement.
    ///
    /// Null means "no proposal", which is a real answer and not a failure: the client raises
    /// AutoKeyException there (key space exhausted, overflow), and SplitKey's own bound
    /// arithmetic answers instead.
    /// </summary>
    private static T? CalculateClientAutoKey<T>(
        T? rangeStart, T? rangeEnd, int draftRowCount, int index)
        where T : struct, System.Numerics.INumber<T>
    {
        var hasStart = rangeStart.HasValue;
        var hasEnd = rangeEnd.HasValue;
        var isDecimal = typeof(T) == typeof(decimal);
        checked
        {
            try
            {
                var inc = T.CreateChecked(AutoSplitKeyIncrement);
                if (!hasStart && !hasEnd)
                    return Step(T.Zero, inc, false);
                if (hasStart && !hasEnd && rangeStart!.Value >= T.Zero)
                    return Step(rangeStart.Value, inc, false);
                if (hasEnd && !hasStart && rangeEnd!.Value <= T.Zero)
                    return Step(rangeEnd.Value, -inc, false);

                var slots = T.CreateChecked(draftRowCount + 1);
                var lowerBound = hasStart ? rangeStart!.Value : T.Min(T.Zero, rangeEnd!.Value - slots);
                var upperBound = hasEnd ? rangeEnd!.Value : T.Max(T.Zero, rangeStart!.Value + slots);
                if (lowerBound >= upperBound) return null;
                var crossesZero = lowerBound < T.Zero && upperBound > T.Zero;
                if (!isDecimal && crossesZero)
                {
                    var negRoom = T.Zero - lowerBound;
                    var posRoom = upperBound - T.Zero;
                    if (negRoom >= slots && hasStart && !hasEnd)
                        upperBound = T.Zero;
                    else if (posRoom >= slots && hasEnd && !hasStart)
                        lowerBound = T.Zero;
                    else
                    {
                        var range = upperBound - lowerBound;
                        if (range < slots + T.One)
                        {
                            if (!hasStart)
                                lowerBound -= range - upperBound;
                            else
                            {
                                if (hasEnd) return null;
                                upperBound += range + lowerBound;
                            }
                        }
                    }
                }
                var delta = T.Min(
                    (upperBound - lowerBound - ((crossesZero && !isDecimal) ? T.One : T.Zero)) / slots,
                    inc);
                if (!isDecimal && delta < T.One) return null;
                if (delta <= T.Zero) return null;
                return Step(lowerBound, delta, crossesZero);
            }
            catch (OverflowException)
            {
                return null;
            }

            T Step(T lowerBound, T delta, bool compensateForZero)
            {
                var value = lowerBound + T.CreateChecked(index + 1) * delta;
                if (compensateForZero)
                {
                    if (isDecimal && value == T.Zero)
                        value -= delta / T.CreateChecked(2);
                    else if (!isDecimal && value >= T.Zero)
                        value += T.One;
                }
                return value;
            }
        }
    }

    // The same client model as _pendingNewRow, for the other half of editing: a SetValue on an
    // EXISTING row writes into the record buffer, and the row is persisted when the cursor
    // leaves it or the page closes.
    //
    // Without this, every edit a TestPage made to an existing row was silently discarded. That
    // is worse than it sounds: the page keeps answering with the value that was set, so a test
    // that writes a field and reads it back through the page PASSES, and only a test that goes
    // to the table notices. Tests of the first shape were green while asserting nothing.
    private bool _pendingModify;

    /// <summary>
    /// A control is ABOUT to write to the record. Called by the field before it validates —
    /// which is the only moment at which the implicit new-row line can still be turned into
    /// the row BC would have started.
    ///
    /// <para>Typing into the draft line is what creates a record on a repeater, and the
    /// platform step that creates it is the SAME one <c>New()</c> runs:
    /// <c>NavForm.NewRecordAsync</c>, which resets the buffer, copies the page's single-valued
    /// filters onto the primary-key fields (<c>RecordImplementation.InitRecordFromFilters</c>)
    /// and raises OnNewRecord. So the promotion goes through <see cref="InsertEmptyRow"/>,
    /// the same entry point <c>New()</c> uses — including
    /// <see cref="LiveNavTestPart.InsertEmptyRow"/>'s SubPageLink stamping when the page is a
    /// linked part.</para>
    ///
    /// <para>WHY BEFORE THE VALIDATE, NOT AFTER (issue #2923). <c>MarkEdited</c> below runs
    /// after the control's write, and it used to be the whole promotion: it flipped
    /// <c>_pendingNewRow</c> and left the buffer exactly as <see cref="EnterNewRowLine"/> had
    /// blanked it — key fields cleared, link values sitting unread in the record's filters.
    /// The typed field's own OnValidate therefore ran against a row with no key. On a linked
    /// document part that is fatal rather than cosmetic: <c>Sales Line</c>'s first OnValidate
    /// reaches <c>TestStatusOpen</c> → <c>GetSalesHeader</c> → <c>TestField("Document No.")</c>
    /// and raises "Document No. must have a value" — 35 tests of Microsoft's Tests-SMB bucket,
    /// on the commonest shape in BC test code (<c>SalesQuote.SalesLines.First()</c> on an empty
    /// part, then <c>SetValue</c>).</para>
    ///
    /// <para>Reading the draft line still answers blank, including in the column a SubPageLink
    /// constrains — nothing here runs until a WRITE arrives. Both halves are measured upstream
    /// on real BC (corpus codeunit 60996 "TPDL Tests",
    /// StefanMaron/BusinessCentral.AL.Language.Tests): the draft line of a linked part reads
    /// blank in the linked column, and the row a write starts on it carries the link's value
    /// early enough that the typed field's OnValidate already sees it.</para>
    /// </summary>
    internal void PromoteNewRowLineForWrite()
    {
        if (!_onNewRowLine) return;
        // beforeCurrent: false — the draft line is the LAST row of the rowset, so the row it
        // becomes is inserted after the data, which is also what BC's own TestPageProxy asks
        // for (InsertBehavior = RowUpdateBehavior.After, whatever beforeCurrent says).
        // Virtual on purpose: a part must reach LiveNavTestPart's override.
        InsertEmptyRow(beforeCurrent: false);
    }

    /// <summary>A control wrote to the record. Called by the field, which owns no page state.</summary>
    internal void MarkEdited()
    {
        // The new-row line is normally already gone by the time this runs — the field calls
        // PromoteNewRowLineForWrite() before validating, and that turns the draft line into a
        // pending insert. This branch stays for any write that reaches the record without
        // going through a LiveNavTestField setter: the row still has to become an insert
        // rather than a Modify of a row that is not in the table. It does NOT do the
        // link-stamping half — a write that never announced itself cannot be given one — so
        // the two paths are not equivalent and the pre-write call above is the one that
        // matters.
        if (_onNewRowLine)
        {
            _onNewRowLine = false;
            _newRowLineReturnPosition = null;
            _pendingNewRow = true;
            return;
        }

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

        // Non-null: _pendingModify is only ever set by MarkEdited, which is only wired to a
        // LiveNavTestField — a Rec-bound control, which cannot exist unless the page has a
        // record (RecordPatches.GetPageControlFieldMap returns empty for a page with no
        // SourceTable). A page-variable-bound field (PageVariableTestField) never calls it.
        var record = _record!;

        // SystemModifiedAt/By are stamped by a Cecil prepend on NavRecord.ALModifyAsync — the
        // CODE-driven entry point this method deliberately does NOT use (see below). Real BC
        // stamps them in the data layer, so they move on a page write too; call the same helper
        // the prepend calls so switching entry points does not silently freeze them.
        BcRuntime.StampSystemFieldsOnModify(record);

        // ModifyAsync, NOT ALModifyAsync — and the difference is the whole xRec contract.
        //
        //   NavRecord.ALModifyAsync  (what AL `Rec.Modify()` lowers to) opens with
        //       OldRecord.ALAssign(this)
        //   before delegating to ModifyAsync, so a code-driven Modify deliberately makes xRec
        //   MIRROR Rec — there is no before-image on that path (corpus CU60179
        //   OnModify_xRec_MirrorsRecValues_WhenCalledFromCode pins exactly that).
        //
        //   NavForm.SaveRecordAsync — BC's own page-write path — skips that assignment and calls
        //       SafeSourceTable.ModifyAsync(DataError.ThrowError, runApplicationTrigger: true,
        //                                   runGlobalTrigger: true)
        //   directly, precisely so the before-image the form snapshotted when it loaded the row
        //   (SnapshotBeforeImage below) survives into the table's OnModify. That is why a
        //   PAGE-driven Modify sees the PREVIOUS value in xRec (corpus CU60235
        //   Record_Modify_FromPage_xRecHoldsPreviousValue).
        //
        // Same three arguments BC passes, for the same reasons: ThrowError, because a Modify
        // that cannot be performed is something the user of a real client would be told about —
        // trapping it turned "this page is not positioned on a row" into an edit that appeared
        // to succeed and quietly went nowhere; and both trigger flags on, because a page write
        // runs the table's OnModify and the global-trigger hook exactly like Rec.Modify(true).
        record.ModifyAsync(DataError.ThrowError, true, true).GetAwaiter().GetResult();
    }

    // Order matters at every flush point: an in-progress new row is finished by an Insert, an
    // edited existing row by a Modify, and only one of the two is ever pending.
    private void FlushRow() { FlushPendingNewRow(); FlushPendingModify(); }

    /// <summary>
    /// Persist whatever row the page is in the middle of editing — BC's NavForm.SaveRecord,
    /// the "the cursor is leaving this row" step.
    ///
    /// Every OTHER leave-the-row moment in this class already does this (the four cursor
    /// moves, Close, Dispose, the built-in OK action); invoking a page ACTION is the one that
    /// did not, and it is the moment BC's client is most obviously at: the client sends the
    /// edited row to the server before it runs the action, which is why an AL action reads
    /// <c>Rec</c> as a row that exists. Without it the action ran against a row that was still
    /// only a buffer — its AutoSplitKey field unassigned and no row of its own in the table —
    /// so an OnAction that looked the row up, or passed its key to a posting routine, silently
    /// found nothing.
    /// </summary>
    internal void SaveCurrentRow() { FlushParts(); FlushRow(); }

    // BC routes TestPage teardown through both Close() and Dispose() depending on whether
    // the AL test calls Close() explicitly or lets the variable go out of scope. Flush on
    // both so a New() is never silently discarded.
    //
    // Parts flush with their host: an AL test closes the CARD, never the part, so a row
    // started with Card.Lines.New() has no other moment at which it could be persisted.
    public override void Close()
    {
        // A torn-down page (see _tornDown / Loaded()) raises "The TestPage is not open."
        // instead of closing -- measured on real BC, Close() does NOT silently no-op here.
        if (_tornDown) throw MakeTestPageNotOpenException();

        // OnQueryClosePage's veto is the one part of the close sequence the runner cannot
        // model: BC would leave the page open and hand control back to the user, which has no
        // meaning in a test that has already asked for the close. Refusing by name beats both
        // alternatives — closing anyway hides that the page objected, and hanging is worse.
        if (_page != null && !_page.RaiseOnClosePage(_formResult))
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                // No " — " in the api — see RequireRecord. The CLAIM is unchanged and stays a
                // permanent scope boundary (#2999 lists it among the fourteen): BC leaves the
                // page open awaiting a user, so a [TryFunction] reading false is BC's outcome.
                $"TestPage page {_pageId} (OnQueryClosePage)",
                "testpage-close-veto — the page's OnQueryClosePage returned false, which in BC "
                + "leaves the page open awaiting the user. See docs/scope.md");
        FlushParts(); FlushRow(); _opened = false;

        // The triggers above are this page's close, so BC's own form state has to agree that
        // it happened — otherwise IsOpen stays true and whoever else is holding the form runs
        // the close a second time. ForceClose raises nothing, which is exactly right here:
        // the triggers have already run once (issue #3091).
        _page?.ForceCloseForm();
    }
    public override void Dispose() { FlushParts(); FlushRow(); }

    private void FlushParts()
    {
        foreach (var part in _parts.Values)
            if (part is LiveNavTestPage live) live.FlushRow();
    }

    public override ITestField GetField(int id)
    {
        if (_tornDown) throw MakeTestPageNotOpenException();

        // A control whose OWN Visible, or that of any group enclosing it, is the compile-time
        // LITERAL false is dead-code-eliminated on real BC — it never exists on the runtime
        // page at all. Returning null here is what makes that faithful: the caller is
        // NavTestPageBase.GetField(int,bool) (a precompiled BC method, not ours), and when
        // ITestPage.GetField answers null it raises BC's own NavTestFieldNotFoundException
        // ("The field with ID = ... is not found on the page.") itself — so this control gets
        // the EXACT exception real BC raises, not a runner-invented one. A Visible bound to a
        // variable/expression is never eliminated this way, even while it is currently false;
        // see RunnerPageInstance.ControlIsCompileTimeEliminated for the literal-vs-expression
        // distinction and the ancestor walk.
        if (_page?.ControlIsCompileTimeEliminated(id) == true) return null!;

        // A control bound to a Rec field resolves against the record, as before. Non-null:
        // _controlIdToFieldNo is only ever populated (RecordPatches.GetPageControlFieldMap)
        // for a page that declares a SourceTable, so a hit here implies _record is set.
        if (_controlIdToFieldNo.TryGetValue(id, out var tableFieldNo))
        {
            // Keyed by CONTROL id, not by table field number. A page may show one field
            // through more than one control -- twice under different conditions, or once in
            // each of two groups with different visibility -- and each of those controls
            // carries its own Visible / Editable / Enabled. Keying by field number handed the
            // second control the instance built for the first, which holds the FIRST
            // control's id, so every property read answered for the wrong control.
            //
            // Real BC keeps them apart: corpus test "TPSF Tests" (codeunit 60263) opens a
            // card with two controls over one Text field, the second declaring
            // Editable = false, and reads them independently on all 8 BC versions.
            //
            // Sharing the instance bought nothing. LiveNavTestField holds only readonly
            // state -- the record, the field number, the page, the control id and the
            // edited callback -- and every value it reads or writes goes to the record, so
            // two instances over one field see each other's writes exactly as one did.
            // _pageVariableFields beside it is already keyed this way.
            if (!_fields.TryGetValue(id, out var field))
                _fields[id] = field =
                    new LiveNavTestField(_record!, tableFieldNo, _page, id,
                        MarkEdited, PromoteNewRowLineForWrite);
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
        throw TestPageShapeGap.ControlBinding(
            $"TestPage control {id}",
            "this control is bound neither to a field of the page's "
            + $"source table nor to a page variable the runner could resolve (table "
            + $"{_record?.MetaTable?.TableName ?? "?"}"
            + (_page == null
                ? "; no AL page object was built for this page, so page-variable-bound controls "
                  + "cannot be resolved — see AlPageMetadataRegistry"
                : "; the page object has no source expression for this control id")
            + ")");
    }

    // Every cursor move leaves the in-progress new row, so it must be persisted first —
    // otherwise navigating away from a New() silently discards it. Parts flush too: moving
    // the parent re-links every part to a different row, so a row started in a part must be
    // persisted while the link that stamped its key is still the current one.
    //
    // An empty result still lands on the implicit new-row line as a SIDE EFFECT, mirroring
    // MoveNext() past the last data row (see EnterNewRowLine). The RETURN VALUE stays false —
    // corpus CU60743 EmptyEditableList_FirstReturnsFalse pins that an explicit First() call on
    // an empty editable, insert-allowed page must still report false, so this only changes
    // internal cursor state, never what First() answers. What it fixes is issue #2392: BC's own
    // ApprovalCommentsHandler opens such a page and writes a field directly, with no New() or
    // First() of its own — the page-construction sites that position a page at open time (see
    // RunnerTestClientSession.GetPage, RunnerTestPageState.MarkOpened) call this so that write
    // has a row to land on instead of silently targeting nothing (corpus CU60743
    // EmptyEditableList_SetValueWithoutNewOrFirst_InsertsARow, validated against a real service
    // tier on all 8 supported BC versions).
    public override bool MoveFirst()
    {
        var record = RequireRecord("MoveFirst()");
        FlushParts(); FlushRow(); LeaveNewRowLine();
        var found = record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (!found) EnterNewRowLine(record);
        return Loaded(found);
    }
    public override bool MoveLast() { var record = RequireRecord("MoveLast()"); FlushParts(); FlushRow(); LeaveNewRowLine(); return Loaded(record.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult()); }

    /// <summary>
    /// Advance to the next row the CLIENT has, which past the last data row of an editable,
    /// insert-allowed repeater is the implicit new-row line — see EnterNewRowLine.
    /// </summary>
    public override bool MoveNext()
    {
        var record = RequireRecord("MoveNext()");
        FlushParts(); FlushRow();

        // Already parked on the new-row line: it is the LAST row of the rowset, so this is
        // where the walk ends. Restore the cursor to the data row it came from first, so a
        // page left at the end is still positioned on a real record rather than on the
        // blank buffer EnterNewRowLine installed.
        if (_onNewRowLine) { LeaveNewRowLine(); return false; }

        if (record.ALNextAsync().GetAwaiter().GetResult() != 0) return Loaded(true);
        return EnterNewRowLine(record);
    }

    public override bool MovePrevious()
    {
        var record = RequireRecord("MovePrevious()");
        FlushParts(); FlushRow();

        // Stepping back off the new-row line lands on the last data row — the row the cursor
        // was on when it walked onto the blank line. It is restored rather than re-sought
        // because ALNextAsync(-1) has nothing to step back FROM: the record buffer holds an
        // Init()ed row that is not in the table.
        if (_onNewRowLine) { LeaveNewRowLine(); return Loaded(true); }

        return Loaded(record.ALNextAsync(-1).GetAwaiter().GetResult() != 0);
    }

    /// <summary>
    /// Advance to the next DATA row only, never onto the new-row line.
    ///
    /// The blank line belongs to the client's presentation of the rowset, so it is what
    /// TestPage.Next() must walk onto — but it is not a record, and every INTERNAL scan
    /// wants rows that exist. Sharing MoveNext() for both would let a search match the blank
    /// line on any field the caller happened to be looking for an empty value in, and report
    /// a row that is not in the table.
    /// </summary>
    private bool MoveNextDataRow()
    {
        var record = RequireRecord("MoveNext()");
        FlushParts(); FlushRow();
        if (_onNewRowLine) { LeaveNewRowLine(); return false; }
        return Loaded(record.ALNextAsync().GetAwaiter().GetResult() != 0);
    }

    /// <summary>
    /// Whether this page shows the implicit new-row line: the trailing blank row an editable,
    /// insert-allowed repeater always carries past its data, which is what a user types into
    /// to create a record.
    ///
    /// BC's client appends it in <c>DraftLinePattern.MakeDraftLines</c> — the same trailing
    /// draft row CaptureInsertPosition already has to account for when it computes an
    /// AutoSplitKey. It is part of the rowset the client hands the test framework, so
    /// <c>TestPage.Next()</c> walks onto it and answers true; the controls there read blank
    /// because the line is an Init()ed buffer, not a record.
    ///
    /// The gating is BOTH conditions, and each one was measured on a real service tier
    /// (corpus CU60743): a page opened with OpenView, a page with Editable = false, and a
    /// page with InsertAllowed = false all answer false to that last Next(). _staticEditable
    /// already combines the open mode with the page's declared Editable (see MarkOpened), and
    /// _creatable is the page's declared InsertAllowed — so the two flags the client gates
    /// the draft line on are exactly the two this class already tracks.
    /// </summary>
    private bool ShowsNewRowLine => TestPageNewRowLineRule.ShowsNewRowLine(_staticEditable, _creatable);

    // Set while the cursor sits on the new-row line, with the position of the data row it
    // walked on from — the blank line is a buffer, so the real cursor has to be remembered
    // somewhere in order to be restored when the walk steps off it.
    private bool _onNewRowLine;
    private string? _newRowLineReturnPosition;

    // Set while FindRowFromTableFieldValues (GoToRecord's underlying mechanism) is scanning
    // candidate rows one at a time via repeated MoveFirst/MoveNextDataRow calls — issue
    // #2677. Each intermediate stop DOES run this page's own OnAfterGetRecord (matching real
    // BC, measured: a GoToRecord that has to search fires the host's OnAfterGetCurrRecord for
    // every row the scan lands on before the target). A linked subpage part's refresh must
    // NOT piggyback on every one of those intermediate stops the same way — measured
    // (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141): the part re-fires ONLY
    // for the row the scan actually SETTLES on, never for a row merely passed through while
    // searching. See Loaded's own guard and FindRowFromTableFieldValues's explicit refresh
    // once a match is confirmed.
    private bool _suppressPartRefreshDuringScan;

    /// <summary>
    /// Park the cursor on the new-row line: blank the record buffer so every control reads
    /// empty, having first saved the position of the data row being left.
    ///
    /// Deliberately NOT Loaded(): no row was fetched, so there is no OnAfterGetRecord to
    /// raise and no before-image to snapshot. Deliberately NOT _pendingNewRow either — the
    /// client only turns the draft line into a record once someone types into it, so merely
    /// walking a page must not insert a blank row (corpus CU60743
    /// NewRowLine_LeftUntouched_InsertsNothing, and CU60996 for the linked-part case).
    /// <see cref="PromoteNewRowLineForWrite"/> is where typing promotes it — BEFORE the
    /// write's own validate, so the row the trigger sees is the one BC's NewRecord would
    /// have handed it, link values and all (#2923).
    /// </summary>
    private protected bool EnterNewRowLine(NavRecord record)
    {
        if (!ShowsNewRowLine) return false;

        _newRowLineReturnPosition = record.ALGetPosition(useCaptions: false);

        // The rows either side of the insertion point decide the AutoSplitKey number, and
        // ALInit is about to wipe the row the cursor is on — so the position is captured
        // now, exactly as InsertEmptyRow does, in case a SetValue promotes this line into a
        // real insert later.
        CaptureInsertPosition();

        // BC'S NavForm.NewRecord, MINUS THE SAVE. Measured on all 8 BC legs, corpus codeunit
        // 60996 (runs 33995429394 and 33997895349), that the draft line of a linked part:
        //
        //   * reads the SubPageLink's value in the linked PRIMARY-KEY column, not blank
        //     (LinkedPart_DraftLine_ReadsTheLinkValueInTheLinkedKeyColumn — the first run
        //     answered 'H1' where this file had asserted blank);
        //   * has ALREADY run the page's OnNewRecord before anyone types
        //     (LinkedPart_DraftLine_HasRunTheOnNewRecordTrigger — the second run answered
        //     'NEWREC' where this file had asserted blank);
        //   * still reads 0 in the AutoSplitKey column
        //     (LinkedPart_DraftLine_ReadsZeroInTheAutoSplitKeyColumn), and writes nothing while
        //     nobody types (LinkedPart_DraftLineLeftUntouched_InsertsNothing).
        //
        // Those four together are exactly NewRecord and nothing after it: ALInit, copy the
        // page's single-valued filters onto the primary key
        // (RecordImplementation.InitRecordFromFilters), raise OnNewRecord — while SplitKey,
        // OnInsertRecord and the Insert all belong to NavForm.SaveRecord, which is where
        // FlushPendingNewRow does them. So the client starts the record when the blank line
        // becomes current; it just never saves it.
        //
        // This is the SAME call InsertEmptyRow makes for New(). The runner used to do a subset
        // of it by hand here — ALInit, then clear every primary-key field — which left a linked
        // part's key column blank and its OnNewRecord unrun.
        //
        // Deliberately NOT _pendingNewRow (that is what makes walking a page insert nothing)
        // and deliberately NO ALValidateAsync of what the filter copy wrote. The validate step
        // is NavForm.NewRecordAsync's second half, which the promotion path
        // (LiveNavTestPart.InsertEmptyRow -> ValidateStampedFields) runs when a write actually
        // starts the row.
        if (!(_page?.TryNewRecord(belowXRec: true) ?? false))
        {
            // Record-only mode: no page to ask, so BC's filter step never runs. Do the two
            // halves by hand — ALInit is AL's Init(), which deliberately PRESERVES the primary
            // key, so without the clear the draft line reported the key of the row just walked
            // off; without the copy back it reads blank where the page's filter says otherwise.
            //
            // ClearFieldValue per key field rather than NavRecord.Clear(): Clear() is AL's
            // Clear(Rec), which also drops filters and the current key — and the page's filters
            // are what make the rowset the page's own (a part's SubPageLink above all).
            // Blanking the buffer must not silently widen what the page is showing.
            record.ALInit();
            var primaryKey = record.MetaTable?.PrimaryKey;
            if (primaryKey != null)
                for (var i = 0; i < primaryKey.KeyFieldCount; i++)
                {
                    var keyFieldNo = primaryKey.KeyFieldsList[i].FieldNo;
                    record.ClearFieldValue(keyFieldNo);
                    if (TryGetSingleFilterValue(record, keyFieldNo, out var fromFilter))
                        record.SetFieldValue(keyFieldNo, fromFilter);
                }
        }

        _onNewRowLine = true;
        return true;
    }

    /// <summary>
    /// Step off the new-row line, putting the record buffer back on the data row the cursor
    /// came from. Every cursor move that is not "advance onto the blank line" goes through
    /// here, so the blank buffer can never outlive the one position it is valid at.
    /// </summary>
    private void LeaveNewRowLine()
    {
        if (!_onNewRowLine) return;
        _onNewRowLine = false;
        var position = _newRowLineReturnPosition;
        _newRowLineReturnPosition = null;
        if (!string.IsNullOrEmpty(position)) _record!.ALSetPosition(position);
    }

    /// <summary>
    /// Drop the new-row line WITHOUT restoring the position it saved — for the one case where
    /// that position is not valid to go back to: a linked part being re-pointed at a different
    /// parent row (<see cref="LiveNavTestPart.ReloadLinkedRow"/>). The saved position names a
    /// row of the OLD link's rowset, and the caller re-finds against the new one immediately,
    /// so restoring it would put the buffer on a row the part no longer shows.
    ///
    /// Kept distinct from <see cref="LeaveNewRowLine"/> because the flag itself must still be
    /// cleared either way: <c>Loaded()</c> does not touch it, so a part that walked onto its
    /// draft line and then had its parent move would otherwise sit on a real row while still
    /// claiming to be on the blank line — and the next write would insert instead of modify.
    /// </summary>
    private protected void AbandonNewRowLine()
    {
        _onNewRowLine = false;
        _newRowLineReturnPosition = null;
    }

    /// <summary>The one value a field's current filter selects, or false when the filter is
    /// not a single value (BC's <c>GetRangeMin</c>/<c>GetRangeMax</c> raise for a filter that
    /// is not a range; a range whose ends differ is not a single value either).
    ///
    /// On the base class rather than on <see cref="LiveNavTestPart"/> because BOTH users of
    /// BC's filter-copy rule need it: the part's New() stamping, and
    /// <see cref="EnterNewRowLine"/>'s draft line. The rule is about the record's FILTERS, not
    /// about a SubPageLink — so reading it off the filters covers const/filter/field links and
    /// a plain filtered page with one mechanism, and answers "nothing to copy" for an
    /// unfiltered page without needing a special case.</summary>
    private protected static bool TryGetSingleFilterValue(NavRecord record, int fieldNo, out NavValue value)
    {
        try
        {
            var min = record.ALGetRangeMin(fieldNo);
            var max = record.ALGetRangeMax(fieldNo);
            if (min != null && min.Equals(max)) { value = min; return true; }
        }
        catch (NavBaseException)
        {
            // Not a range: a multi-value expression (1|2), an open-ended one (>1), or a
            // wildcard. BC's own InitRecordFromFilters stamps nothing for these either.
        }
        value = null!;
        return false;
    }

    /// <summary>
    /// A row just became the page's current row — run the page's OnAfterGetRecord, exactly
    /// as BC does after every load. That trigger is where a page derives its per-row state
    /// (the variable behind <c>Editable = …</c>, <c>CurrPage.Editable(…)</c>), so skipping it
    /// froze every page at whatever state its first row left behind.
    ///
    /// <c>protected</c> (not <c>private</c>) so <see cref="LiveNavTestPart"/> can drive its
    /// own SubPageLink-matched row through the identical path a top-level page's
    /// MoveFirst/MoveNext/GoToBookmark already use — see issue #2677's
    /// <c>ReloadLinkedRow</c>.
    /// </summary>
    protected bool Loaded(bool found)
    {
        if (found)
        {
            try
            {
                _page?.RaiseOnAfterGetRecord();
            }
            // NavBaseException only -- matches real BC's own teardown, NstDataAccess.Abort
            // (NavBaseException exception), which only wraps a genuine AL-catchable error
            // (Error(), TestField, a table trigger's own refusal, ...). A RunnerOutOfScopeException
            // (plain System.Exception, never NavBaseException -- see NavDotNetPatches.cs) or a
            // genuine runner NRE must NOT be relabelled as "The TestPage is not open.": that
            // would hide an OOS surface's real reason, or a runner bug, behind a fake BC message
            // (.claude/rules/loud-failures.md).
            catch (NavBaseException ex)
            {
                // See _suppressTeardownOnLoad: the page-construction-time initial position is
                // not a teardown-worthy call. Let the original exception propagate unmodified,
                // exactly as it did before this fix (into a blanket `catch {}` at the call site).
                if (_suppressTeardownOnLoad) throw;

                // Real BC (measured 27.5/28.3/28.4, issue #2656): an unhandled AL error here
                // tears the TestPage down. The original error's own text never reaches the AL
                // caller -- what propagates out of this call (and every later one on the same
                // variable) is BC's own "The TestPage is not open." The original is kept as
                // diagnostic data (see MakeTestPageNotOpenException); it is not AL-visible
                // (asserterror / GetLastErrorText only see the outer message), matching what
                // real BC surfaces.
                _tornDown = true;
                throw MakeTestPageNotOpenException(ex);
            }
            SnapshotBeforeImage();
            // Issue #2677: NOT during a FindRowFromTableFieldValues scan — see
            // _suppressPartRefreshDuringScan's doc comment and that method's own explicit
            // refresh once a match is confirmed.
            if (!_suppressPartRefreshDuringScan)
                RefreshLinkedParts();
        }
        return found;
    }

    /// <summary>
    /// Refresh every linked subpage part to THIS page's current row — issue #2677, measured
    /// on real BC (corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141): a linked
    /// subpage part (FactBox-style, SubPageLink to this page's key) refreshes to the NEW
    /// current row every time this page's own row changes — GoToRecord on the host re-fires
    /// the part's OnAfterGetRecord/OnAfterGetCurrRecord for the row just arrived at, and does
    /// NOT re-fire it for the row just left. Only linked parts refresh here: an unlinked part
    /// shows its own table's full rowset, independent of this page's current row, and BC's
    /// own re-sync behaviour for that shape is unmeasured — see LiveNavTestPart.HasLinks.
    /// </summary>
    private void RefreshLinkedParts()
    {
        foreach (var part in _parts.Values)
            if (part is LiveNavTestPart { HasLinks: true } linkedPart)
                linkedPart.ReloadLinkedRow();
    }

    /// <summary>
    /// Take the page's before-image of the current row — what the table's <c>OnModify</c> reads
    /// as <c>xRec</c> when the edit is driven from a page.
    ///
    /// This is the tail of BC's own <c>NavForm.AfterGetRecordAsync</c> AND of
    /// <c>NavForm.AfterGetCurrRecordAsync</c> — both end with
    /// <c>OldRecord.ALAssign(SourceTable)</c>, and <c>NavForm.OldRecord</c> is literally
    /// <c>SafeSourceTable.OldRecord</c>, so the target is this record's own xRec slot. Those two
    /// are exactly the pair of triggers RaiseOnAfterGetRecord above fires, which is why the
    /// snapshot belongs here and nowhere else: "a row became the current row" is the only moment
    /// BC takes it, and nothing on the page-write path overwrites it (see FlushPendingModify),
    /// so by the time OnModify runs xRec still holds the row AS FETCHED.
    ///
    /// Without this the page had no before-image at all: <c>ALModifyAsync</c>'s own
    /// <c>OldRecord.ALAssign(this)</c> was the only thing that ever populated xRec, which is
    /// what made a page-driven Modify report the NEW value as the old one.
    /// </summary>
    // Non-null: only ever called from Loaded(true), which every MoveXxx/GoToBookmark caller
    // reaches through RequireRecord first.
    private void SnapshotBeforeImage() => _record!.OldRecord.ALAssign(_record);

    // useCaptions: false — NavRecord.ALGetPosition()'s default (useCaptions: true) encodes
    // the position string using field CAPTIONS, and ALSetPosition decodes it through the
    // same SETVIEW-style filter parser TableViewParser.ParseTableFilters uses for AL filter
    // views, which resolves each token by caption. On a table with two fields sharing a
    // caption (legal AL) that decode throws BC's own NavNCLFieldNotFoundException
    // ("... is ambiguous between multiple fields ...") instead of positioning — real BC
    // does not throw here (issue #2515). Positioning by field NUMBER, exactly like every
    // other cursor move in this class (ALSetPosition/GetFieldValue take field numbers, never
    // captions), sidesteps the ambiguous caption lookup entirely. Both overloads are real
    // BC's own public API on NavRecord; this only picks the one that matches how the rest of
    // the runner already talks to a record.
    public override object? GetBookmark() => RequireRecord("GetBookmark()").ALGetPosition(useCaptions: false);

    public override bool GoToBookmark(object bookmark)
    {
        if (bookmark is not string position || string.IsNullOrEmpty(position)) return false;
        // Jumping to a bookmark is a cursor move like any other, so it steps off the blank
        // line first — otherwise the flag would survive onto a real row and the NEXT
        // MoveNext() would end the walk early.
        LeaveNewRowLine();
        RequireRecord("GoToBookmark()").ALSetPosition(position);
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

        var record = RequireRecord("locating a row");

        // Capture the ORIGINAL row's own primary-key field numbers and values (not just a
        // position string) before scanning moves the cursor away from it. A not-found result
        // must restore the exact row the page was on — including every NON-key field it was
        // showing — and NavRecord.ALSetPosition (real BC engine code, unmodified) only writes
        // the primary-key columns of the record buffer, leaving non-key columns holding
        // whatever the internal scan below last read (issue #2537: GoToRecord(existing row A)
        // then GoToRecord(absent row) left the page's non-key field reading row C's value
        // under key A, because the scan's last MoveNextDataRow landed on C before failing).
        // Re-finding the original row through the SAME MoveFirst/MoveNextDataRow path the
        // search below already uses is what refreshes a row's non-key columns correctly (they
        // go through NavRecord.ALFindFirstAsync/ALNextAsync, not the key-only SetPosition), so
        // the restore reuses that exact mechanism instead of a raw position write.
        var hasCurrent = !string.IsNullOrEmpty(record.ALGetPosition(useCaptions: false));
        int[]? originalKeyFieldNos = null;
        object?[]? originalKeyValues = null;
        if (hasCurrent)
        {
            var originalPrimaryKey = record.MetaTable?.PrimaryKey;
            if (originalPrimaryKey != null && originalPrimaryKey.KeyFieldCount > 0)
            {
                originalKeyFieldNos = originalPrimaryKey.KeyFieldsList.Select(f => f.FieldNo).ToArray();
                originalKeyValues = originalKeyFieldNos.Select(fieldNo => ReadClientObject(fieldNo)).ToArray();
            }
        }

        // Scan the WHOLE rowset, always starting from the first (or last, when searching
        // backward) row — never from wherever the page happens to be positioned. `forward`
        // is a direction, not "resume from the cursor": BC's client locates the requested
        // row anywhere in the rowset. Starting at the current row silently failed to find
        // any row BEHIND the cursor, so navigating C -> A returned false even though A is
        // on the page (tests/runner-extras/testpage-gotorecord GoToRecord_MovesBetweenRows).
        //
        // Issue #2677: the scan below runs Loaded(true) — and so this page's own
        // OnAfterGetRecord — for every intermediate row it passes through before landing on
        // the target, matching BC's own measured behaviour. A linked subpage part must NOT
        // piggyback on those intermediate stops; _suppressPartRefreshDuringScan holds that
        // off, and the one explicit RefreshLinkedParts() call below — once a match is
        // confirmed, for that row only — is what a linked part actually re-fires for.
        _suppressPartRefreshDuringScan = true;
        try
        {
            var hasRow = forward ? MoveFirst() : MoveLast();

            while (hasRow)
            {
                if (Matches(fieldNos, values))
                {
                    _suppressPartRefreshDuringScan = false;
                    RefreshLinkedParts();
                    return true;
                }
                // MoveNextDataRow, not MoveNext: a search wants rows that EXIST. Walking the
                // scan onto the new-row line would let any request for an empty value "find"
                // the blank line and report a row that is not in the table.
                hasRow = forward ? MoveNextDataRow() : MovePrevious();
            }

            if (originalKeyFieldNos != null)
            {
                // Re-find the original row by its own primary key, walking forward from the
                // top exactly like the search above — this goes through a real MoveFirst/
                // MoveNextDataRow load, refreshing every field (not just the key) from the
                // row's own stored values, instead of a raw key-only ALSetPosition. Still
                // suppressed: this restores the SAME row the page (and its parts) were
                // already showing before the failed search started, so there is nothing new
                // for a linked part to refresh to.
                hasRow = MoveFirst();
                while (hasRow)
                {
                    if (Matches(originalKeyFieldNos, originalKeyValues!)) break;
                    hasRow = MoveNextDataRow();
                }
            }
            return false;
        }
        finally
        {
            _suppressPartRefreshDuringScan = false;
        }
    }

    // ITestFilter.SetFilter/GetFilter are handed a TABLE FIELD NUMBER, not a control id:
    // AL's `TestPage.Filter.SetFilter(Field, ...)` resolves the field reference itself and
    // BC passes the field number straight through. Routing these through the control map
    // was wrong in both directions — it would mistranslate a field number that happens to
    // collide with a control id, and it rejected small, perfectly valid field numbers as
    // "not a control" (Pageworks SetFilter(3, …) on PageworksPartial).
    public override void SetFilter(int fieldNo, string filterValue)
    {
        RequireRecord("SetFilter()").ALSetFilter(fieldNo, filterValue);
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
        => RequireRecord("GetFilter()").ALGetFilter(fieldNo);

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
            + $"source table ({_record?.MetaTable?.TableName ?? "?"}), so it cannot be used to "
            + "locate a row. See docs/scope.md");
    }

    private bool Matches(int[] fieldNos, object[] values)
    {
        for (var i = 0; i < fieldNos.Length; i++)
            if (!ValuesEqual(ReadClientObject(fieldNos[i]), Unwrap(values[i])))
                return false;
        return true;
    }

    private object? ReadClientObject(int fieldNo) => Unwrap(RequireRecord("field access").GetFieldValue(fieldNo));

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
            ?? throw TestPageShapeGap.OptionValue(
                context,
                "the control is bound to an Option with no option metadata, so a value cannot "
                + "be resolved by name");

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

        // Issue #1928, decided against real-BC evidence (StefanMaron/BusinessCentral.AL.
        // Language.Tests#50, run against a real BC service tier on two BC versions): an
        // Enum-typed control's TestPage.SetValue resolves ONLY by the declared Caption and
        // REFUSES the member name — SetValue('Block') against `value(1; Block) { Caption =
        // 'Blocks'; }` throws "Your entry of 'Block' is not an acceptable value for
        // 'Kind'.", not a successful set. So for an Enum-backed metadata (IsEnum), the
        // member-name fallback below must NOT run — accepting a spelling real BC rejects is
        // exactly the silent divergence loud-failures.md forbids, and it is what shipped as
        // a ghost test in tests/runner-extras/page-enum-control-modal before this fix.
        //
        // The plain `Option` primitive is a SEPARATE, unverified question — no real-BC
        // evidence either way distinguishes caption-vs-member resolution for it, so its
        // historical member-name fallback stays as-is; only Enum's is removed here.
        var isEnumBacked = metadata.IsEnum;
        if (!isEnumBacked)
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
            isEnumBacked
                ? $"testpage-option-value — '{value}' is not an acceptable value. An "
                  + "Enum-typed control resolves TestPage.SetValue by its declared Caption "
                  + "only, never by the member name (real BC's own behavior — see issue "
                  + "#1928) — "
                  + (captions != null
                      ? $"acceptable captions are [{string.Join(", ", captions)}]"
                      : "the enum declares no captions")
                  + $". Member names ([{string.Join(", ", options)}]) are NOT accepted. "
                  + "See docs/scope.md"
                : $"testpage-option-value — '{value}' is not one of the option's values "
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
        => option.NavOptionMetadata is { } metadata
            ? DisplayOrdinal(metadata, option.Value, captions)
            : null;

    /// <summary>
    /// The same text <see cref="Display"/> produces, for a BARE ORDINAL rather than for a
    /// NavOption that already carries its own metadata (issue #2367).
    ///
    /// <c>NavTestField.ALAssertEquals</c> and <c>ALSetValue</c> — the real, precompiled BC
    /// methods the AL compiler emits for <c>TestPage.&lt;field&gt;.AssertEquals(&lt;option&gt;)</c>
    /// and <c>SetValue(&lt;option&gt;)</c> — never hand an AL Option/Enum value to
    /// <see cref="ITestField"/> as-is. They round-trip it through
    /// <c>NavValue.CreateNavValueFromObject(NavValueMetadata.DefaultMetadata(FieldType), value)</c>,
    /// whose <c>NavNclType.NavOption</c> arm rebuilds the value against the DEFAULT option
    /// metadata, and then hand the resulting <c>ClientObject</c> — a bare ordinal, with the
    /// field's own member/caption table gone — to <see cref="ITestField.ValueToString"/>.
    ///
    /// So <c>ValueToString</c> is where the control's own option table has to be put back.
    /// The metadata comes from the value the control currently holds, which is the control's
    /// option set; only the ordinal comes from the caller.
    /// </summary>
    internal static string? DisplayOrdinal(NavOption? current, object? value, string[]? captions)
        => current?.NavOptionMetadata is { } metadata && TryAsOrdinal(value, out var ordinal)
            ? DisplayOrdinal(metadata, ordinal, captions)
            : null;

    private static string? DisplayOrdinal(object metadata, int ordinal, string[]? captions)
    {
        var index = IndexOfOrdinal(metadata, ordinal);
        if (index < 0) return null;

        if (captions != null && index < captions.Length) return captions[index];
        var options = Members(metadata);
        return index < options.Length ? options[index] : null;
    }

    // Deliberately narrow. An ordinal is what BC's own round trip leaves behind for an
    // Option/Enum, and nothing else on this path is one: a string is not accepted here
    // because ALSetValue's `value is NavStringValue` fast path never reaches ValueToString,
    // so a string arriving would mean some OTHER caller with an unexamined contract, and
    // silently reinterpreting its text as an option would be exactly the kind of guess
    // loud-failures.md exists to prevent. Anything unrecognised falls back to the caller's
    // own Convert.ToString, i.e. to the behaviour before #2367.
    private static bool TryAsOrdinal(object? value, out int ordinal)
    {
        switch (value)
        {
            case NavOption option: ordinal = option.Value; return true;
            case int i:            ordinal = i;            return true;
            case short s:          ordinal = s;            return true;
            case byte b:           ordinal = b;            return true;
            case sbyte sb:         ordinal = sb;           return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                                   ordinal = (int)l;       return true;
            default:               ordinal = 0;            return false;
        }
    }

    /// <summary>
    /// An Enum-typed control's per-value captions, sourced from the enum's OWN metadata.
    ///
    /// Unlike the <c>Option</c> primitive, an AL <c>Enum</c> has no page-level
    /// <c>OptionCaption</c> property to declare, so
    /// <see cref="AlRunner.Patches.RunnerPageInstance.TryGetOptionCaptions"/>'s
    /// <c>ControlDefinition.OptionCaptionML</c> lookup is always empty for it (verified via
    /// <c>AL_RUNNER_TRACE_PAGE_METADATA=2</c> against an Enum-bound page-variable control:
    /// <c>OptionCaption='' OptionCaptionML=''</c>). Real BC computes an Enum's captions
    /// from its own metadata instead — see issue #1928's real-BC evidence: a real service
    /// tier's <c>TestPage.SetValue</c> on an Enum control resolves by the declared
    /// <c>Caption</c> and REFUSES the member name (the exact opposite of what this runner
    /// did before this fix).
    ///
    /// <c>IsEnum</c>/<c>GetOrdinals()</c>/<c>GetCaptionFromIndex(int)</c> are public virtuals
    /// on <c>NCLOptionMetadata</c> (decompiled: <c>Microsoft.Dynamics.Nav.Ncl.dll</c>), which
    /// <c>AlEnumOptionMetadata</c> (EnumMetadataPatches.cs) overrides from the SAME
    /// emit-captured <c>(name, options[], indexes[], captions[])</c> tuple already used, and
    /// already accepted as faithful, for <c>Enum::"X".Ordinals()/.Names()</c> via
    /// <c>NCLEnumMetadata_CreateByIdAlAware</c>. The result is built in
    /// <c>GetOrdinals()</c> order, which is the SAME order <see cref="Ordinals"/>'s reflection
    /// (over a different, private accessor) already returns for the same metadata instance —
    /// both walk the one <c>(options[], indexes[])</c> pair the AL emit captured — so a
    /// caption at index i here lines up with the member at index i in <see cref="Members"/>,
    /// which is what <see cref="Resolve"/> and <see cref="Display"/> index into.
    ///
    /// Returns null for a plain <c>Option</c> value (<c>IsEnum</c> is false there) or when
    /// no bound value is available — the caller falls back to member-name display/resolution,
    /// same as when a control declares no <c>OptionCaption</c> at all.
    /// </summary>
    internal static string[]? EnumCaptions(NavOption? option)
    {
        if (option?.NavOptionMetadata is not { IsEnum: true } metadata) return null;

        var ordinals = new System.Collections.Generic.List<int>();
        foreach (var ordinal in metadata.GetOrdinals()) ordinals.Add(ordinal);

        var captions = new string[ordinals.Count];
        for (var i = 0; i < ordinals.Count; i++)
            captions[i] = metadata.GetCaptionFromIndex(ordinals[i]);
        return captions;
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

/// <summary>
/// Boolean values as a TestPage sees them, on either shape of control: a page-variable-bound
/// one (<c>field(Flag; ShowFlag)</c> where <c>ShowFlag: Boolean</c>) or a Rec-bound one
/// (<c>field(Flag; Rec.Flag)</c> where the source table field is <c>Boolean</c>) — see issue
/// #1870, the Rec-bound half of #1837 that #1869 (the page-variable half) left open.
///
/// <c>NavTestField.ALSetValue</c> — the real, precompiled BC method the AL compiler emits for
/// every <c>TestPage.&lt;field&gt;.SetValue(&lt;Boolean&gt;)</c> call — never hands a NavValue
/// straight to <see cref="ITestField"/>. For anything that is not itself already a
/// <c>NavStringValue</c> it round-trips through <see cref="ITestField.FieldType"/> (to pick a
/// <c>NavValueMetadata</c>) and then <see cref="ITestField.ValueToString"/> (both OUR OWN mock
/// methods) to turn the boolean back into a string before ever reaching <see cref="ITestField.Value"/>'s
/// setter — see the doc comment on <see cref="PageVariableTestField.FieldType"/> for why that
/// matters here. <see cref="LiveNavTestField.FieldType"/> is sourced from the source table
/// field's own declared type instead, but reaches the same <c>NavType.Boolean</c> answer for a
/// <c>Boolean</c> field, so the round trip is identical on both sides.
///
/// Because both ends of that round trip are code THIS runner owns (<see cref="ITestField.ValueToString"/>
/// always answers with <c>Convert.ToString(boolValue)</c>, i.e. exactly "True" or "False"), accepting
/// only that spelling here is not a narrowing of what <c>SetValue(&lt;Boolean&gt;)</c> can express —
/// it is the ONLY spelling that overload ever produces. Anything else (a literal
/// <c>SetValue('Yes')</c>, locale spellings, ...) is a genuinely separate, upstream-unvalidated
/// question about what real BC's own text-to-Boolean evaluate accepts on this surface, so it stays
/// out of scope here and throws loudly rather than guessing.
/// </summary>
/// <summary>
/// Enforces a field's declared <c>MinValue</c>/<c>MaxValue</c> AL properties on a TestPage
/// control write (issue #2495). Measured against real BC (28.1 / 28.4, see #2490's arm A2):
/// a Decimal field with <c>MinValue = 0;</c> raises
/// <c>Validation error for Field: &lt;caption&gt;,  Message = 'The value must be greater than
/// or equal to 0. Value: -1.00. (Select Refresh to discard errors)'</c> from a TestPage
/// SetValue, while the SAME write via <c>Rec.Validate</c> or a plain field assignment raises
/// nothing at all — this is a client/page-layer check, not a table-trigger one, so it must
/// stay out of NavRecord.ALValidateAsync (which Rec.Validate also calls).
///
/// <para>Since #2900 this raises only the CORE of that message. Two layers are added around it
/// by code that is not this helper's: <see cref="TestFieldValidationErrors"/> appends
/// <c>" (Select Refresh to discard errors)"</c> when it records the refusal (BC's client does
/// that, measured on corpus run 34002487601), and BC's own <c>NavTestField.CheckError</c> then
/// wraps the result in <c>Validation error for Field: &lt;name&gt;,  Message = '…'</c>. The
/// AL-visible string is the same one #2490 measured; it is now composed rather than
/// assembled here, which is what stops the suffix appearing twice.</para>
///
/// <para>Only numeric field types are checked — MinValue/MaxValue is meaningless on Text/Code/
/// Boolean/etc., and AL does not let those types declare it.</para>
///
/// <para>The bound text is read via <see cref="RecordPatches.TryGetParsedFieldMinMax"/> — the
/// parse-time source, not the constructed <c>NCLMetaField</c> — because NCLMetaField
/// (Microsoft.Dynamics.Nav.Runtime) does not expose MinValue/MaxValue on the built runtime
/// object at all (confirmed empirically: no Min/MaxValue-named member of any accessibility),
/// even though <c>MetaField</c> (Microsoft.Dynamics.Nav.Types.Metadata, what the runner's
/// NclMetaTableBuilder constructs FROM) does carry them. Same rationale as
/// <see cref="RecordPatches.TryGetParsedFieldCaption"/> reading Caption straight from the parsed
/// table rather than through NCLMetaField's own getter.</para>
/// </summary>
internal static class TestPageMinMaxValue
{
    internal static void Check(NCLMetaTable table, int fieldNo, NavType fieldType, string rawValue, string caption)
    {
        if (fieldType is not (NavType.Decimal or NavType.Integer or NavType.BigInteger)) return;
        if (!table.TryGetFieldByNo(fieldNo, out var field) || field == null) return;

        var (minText, maxText) = RecordPatches.TryGetParsedFieldMinMax(table.TableId, fieldNo);
        if (string.IsNullOrEmpty(minText) && string.IsNullOrEmpty(maxText)) return;

        // AL's TestPage SetValue is string-typed for every control, and the generic
        // ALCompiler.ToNavValue(value) path taken above (there is no per-type conversion for a
        // Decimal/Integer control here, unlike the Boolean special-case) always produces a
        // NavText — so the bound check parses the RAW string the test wrote, exactly as BC's own
        // client-side field validation would before ever constructing a typed NavValue.
        if (!decimal.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) return;

        var isInteger = fieldType is NavType.Integer or NavType.BigInteger;

        if (!string.IsNullOrEmpty(minText) && decimal.TryParse(minText, NumberStyles.Any, CultureInfo.InvariantCulture, out var min)
            && value < min)
            throw MakeError(isInteger, "greater than or equal to", minText!, value);

        if (!string.IsNullOrEmpty(maxText) && decimal.TryParse(maxText, NumberStyles.Any, CultureInfo.InvariantCulture, out var max)
            && value > max)
            throw MakeError(isInteger, "less than or equal to", maxText!, value);
    }

    // The offending VALUE renders with the field's decimal places (2 by default for a Decimal,
    // none for an Integer/BigInteger — measured against real BC, #2490's arm A2: "-1.00"). The
    // BOUND is echoed as BC declared it (the raw MinValue/MaxValue AL text, e.g. "0"), NOT
    // reformatted to the field's decimal places — also measured in #2490's arm A2, where the
    // bound reads "0" while the value reads "-1.00" for the identical Decimal field.
    private static string FormatValue(decimal d, bool isInteger)
        => isInteger ? d.ToString("0", CultureInfo.InvariantCulture)
                     : d.ToString("0.00", CultureInfo.InvariantCulture);

    private static System.Exception MakeError(bool isInteger, string comparison, string boundText, decimal value)
    {
        // The BARE message only. BC's own NavTestField.CheckError wraps whatever an ITestField
        // records into "Validation error for Field: {Name},  Message = '{recorded}'" using
        // Lang.TestValidationException, so building that wrapper here too would double it
        // (#2900). The AL-visible string is unchanged; it is composed one layer out now.
        var msg = $"The value must be {comparison} "
            + $"{boundText}. Value: {FormatValue(value, isInteger)}.";

        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }
}

/// <summary>
/// A Decimal-typed control always renders with the field's decimal places -- default 2,
/// the same convention #2490 measured against real BC and <see cref="TestPageMinMaxValue.FormatValue"/>
/// already codifies for the error-message text -- regardless of how many decimal digits the
/// underlying .NET <c>decimal</c>'s own <c>Scale</c> happens to carry (issues #2634 / #2534).
///
/// A record field reaches its stored value through <see cref="Microsoft.Dynamics.Nav.Runtime.NavRecord"/>'s
/// own Validate/Insert path, which is BC's own precompiled code and out of this runner's
/// control -- so a Rec-bound Decimal control can format correctly today by construction, if
/// BC's own write path already normalises the stored scale. A page-GLOBAL <c>Decimal</c>
/// (<c>Values: array[20] of Decimal;</c>) gets no such round trip at all: a plain assignment
/// like <c>Values[i] := i * 10;</c> stores a .NET decimal with Scale 0, and
/// <c>Convert.ToString(10m)</c> answers "10" where real BC's page layer answers "10.00". Both
/// <see cref="LiveNavTestField.Value"/> and <see cref="PageVariableTestField.Value"/> read
/// through this helper so neither binding shape can silently regress relative to the other --
/// exactly the LiveNavTestField/PageVariableTestField pairing pattern <see cref="TestPageBooleanValue"/>
/// and <see cref="TestPageOptionValue"/> already use.
///
/// Only <see cref="NavDecimal"/> needs special handling: <see cref="NavInteger"/> and
/// <see cref="NavBigInteger"/> already round-trip correctly through
/// <c>Convert.ToString</c> because an integral CLR type never carries a fractional Scale to
/// lose in the first place -- matching the "0" (no decimals) half of
/// <see cref="TestPageMinMaxValue.FormatValue"/>'s own convention without any code needed here.
/// </summary>
internal static class TestPageNumericValue
{
    internal static string? Format(NavValue? navValue)
        => navValue is NavDecimal d
            ? Convert.ToDecimal(d.ClientObject, CultureInfo.InvariantCulture)
                .ToString("0.00", CultureInfo.InvariantCulture)
            : null;
}

internal static class TestPageBooleanValue
{
    /// <summary>
    /// How a Boolean control RENDERS. Issue #2795: real BC answers "Yes"/"No", measured on all
    /// eight BC legs of the corpus CI (27.0 through 28.4, run 33967745688 on corpus PR #150 —
    /// <c>Actual:&lt;Yes&gt;</c> on every failing leg, no other value anywhere in the run) and
    /// pinned upstream by <c>BooleanFieldControl_ReadsAsYesOrNo</c>. The runner answered
    /// <c>Convert.ToString(bool)</c>, i.e. "True"/"False".
    ///
    /// <para>Read through by BOTH <see cref="LiveNavTestField"/> (a Rec-bound control) and
    /// <see cref="PageVariableTestField"/> (a page-global one), the same pairing
    /// <see cref="TestPageNumericValue"/> and <see cref="TestPageOptionValue"/> already use, so
    /// neither binding shape can drift from the other.</para>
    ///
    /// <para>It is also what <c>ValueToString</c> must answer, and that is not a nicety.
    /// <c>NavTestField.ALAssertEquals</c> — BC's own precompiled method — converts a non-string
    /// expected value through <c>testField.ValueToString</c> and then compares it ORDINALLY
    /// against the control's value:</para>
    /// <code>
    ///   value = NavValue.CreateNavValueFromObject(NavValueMetadata.DefaultMetadata(testField.FieldType), value);
    ///   text  = testField.ValueToString(value.ClientObject);
    ///   if (string.CompareOrdinal(ALValue, text) != 0) throw ...
    /// </code>
    /// <para>So changing the getter alone would have broken every
    /// <c>AssertEquals(&lt;Boolean&gt;)</c> — "Yes" against "True" — which passes today only
    /// because both halves are wrong in the same way.</para>
    /// </summary>
    internal static string? Format(NavValue? navValue)
        => navValue is NavBoolean b
            ? (Convert.ToBoolean(b.ClientObject, CultureInfo.InvariantCulture) ? "Yes" : "No")
            : null;

    /// <summary>The same rendering for an already-unwrapped CLR value, as ValueToString sees it.</summary>
    internal static string? FormatObject(object? value)
        => value is bool b ? (b ? "Yes" : "No") : null;

    /// <summary>
    /// The inverse: the text a TestPage write carries, back to a Boolean.
    ///
    /// <para>Accepts "Yes"/"No" ONLY. That is what <see cref="FormatObject"/> now produces, so
    /// <c>SetValue(&lt;Boolean&gt;)</c> round-trips through it — see the chain in
    /// <c>NavTestField.ALSetValue</c>, where a non-string value goes out through
    /// <c>ValueToString</c> and comes back in through this.</para>
    ///
    /// <para><b>"True"/"False" is refused, and that is measured, not assumed.</b> An earlier
    /// version of this fix accepted it, reasoning that it is the spelling AL's own
    /// <c>Evaluate</c> takes for a Boolean. Corpus PR #163 put the question in front of a
    /// service tier and all eight BC legs answered identically:</para>
    /// <code>
    ///   Validation error for Field: RecTrue,  Message = 'Your entry of 'False' is not an
    ///   acceptable value for 'Rec True'. (Select Refresh to discard errors)'
    /// </code>
    /// <para>So this is not an unsupported surface the runner should refuse as out of scope —
    /// BC has a defined answer for it, and the runner's job is to give the same one. Hence a
    /// validation error in BC's own shape rather than a <c>RunnerOutOfScopeException</c>, built
    /// the same way <see cref="TestPageMinMaxValue.MakeError"/> already builds that shape for a
    /// MinValue/MaxValue refusal.</para>
    ///
    /// <para>One fidelity gap, stated rather than hidden: BC puts the control's declared NAME in
    /// the <c>Field:</c> slot and its CAPTION in the quoted target ("RecTrue" and "Rec True"
    /// above). This runner's <c>ITestField.Name</c> answers the caption, so both slots read the
    /// caption here. A test asserting the message as a substring — as the corpus one does — is
    /// unaffected; one asserting it verbatim would see the difference.</para>
    /// </summary>
    internal static NavValue Resolve(string value, string caption)
    {
        if (string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase)) return NavBoolean.Create(true);
        if (string.Equals(value, "No", StringComparison.OrdinalIgnoreCase)) return NavBoolean.Create(false);

        throw MakeNotAcceptableError(value, caption);
    }

    /// <summary>
    /// BC's own refusal for a value a control will not take:
    /// <c>Your entry of '{value}' is not an acceptable value for '{caption}'.</c>
    /// <para>Two layers are deliberately absent here (#2900), because neither is this helper's
    /// to add: <see cref="TestFieldValidationErrors"/> appends
    /// <c>" (Select Refresh to discard errors)"</c> when it records the refusal, and BC's own
    /// <c>NavTestField.CheckError</c> then wraps it in <c>Validation error for Field: {name},
    /// Message = '…'</c> — including the double space after the comma, which is BC's and not a
    /// typo. The composed result is the string corpus PR #163 measured on all eight legs.</para>
    /// </summary>
    private static System.Exception MakeNotAcceptableError(string value, string caption)
    {
        // The BARE message only — see TestPageMinMaxValue.MakeError for why the
        // "Validation error for Field: ..." wrapper is BC's to add and no longer ours (#2900).
        var msg = $"Your entry of '{value}' "
            + $"is not an acceptable value for '{caption}'.";

        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) return (System.Exception)ctor.Invoke(new object[] { msg });
        }
        return new System.InvalidOperationException(msg);
    }
}

/// <summary>
/// Date values as a page-variable TestPage control sees them (issue #2054).
///
/// A <c>Date</c> global is not a <c>NavStringValue</c>, so <c>NavTestField.ALSetValue</c> (the
/// real, precompiled BC method the AL compiler emits for every <c>SetValue(&lt;Date&gt;)</c>
/// call) round-trips it through OUR OWN <see cref="PageVariableTestField.FieldType"/> (now
/// correctly answering <c>NavType.Date</c> — see that property's doc comment) and OUR OWN
/// <c>ValueToString</c> before it ever reaches <see cref="ITestField.Value"/>'s setter. Both
/// ends of that round trip are code this runner owns: <c>ValueToString</c> for this class is
/// the generic <c>Convert.ToString(value, CultureInfo.InvariantCulture)</c>, which — once
/// FieldType stops lying about the type — is handed a plain <c>DateTime</c>
/// (<c>NavDate.ClientObject</c>) and renders it via .NET's InvariantCulture general date/time
/// pattern (e.g. "12/31/2026 00:00:00"). <see cref="Resolve"/> only needs to invert THAT exact
/// spelling, the same way <see cref="TestPageBooleanValue"/> only needs to invert "True"/"False".
/// </summary>
internal static class TestPageDateValue
{
    internal static NavValue Resolve(string value, string context)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                context,
                $"testpage-date-value — '{value}' is not the round-trip spelling TestPage "
                + "SetValue(Date) itself produces (InvariantCulture general date/time format). "
                + "See docs/scope.md");

        // NavDate.Create requires DateTimeKind.Local (its private ctor throws
        // NavNCLDateInvalidException otherwise) — DateTime.Parse without an explicit style
        // always returns Unspecified, so it must be stamped before handing it back.
        return NavDate.Create(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
    }
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

    // Told BEFORE this field writes, so the page can turn its implicit new-row line into a
    // real started row while the write's own OnValidate can still see it — see
    // LiveNavTestPage.PromoteNewRowLineForWrite (#2923). Separate from _onEdited because the
    // order is the whole point: _onEdited runs after the validate and is far too late to give
    // the row its keys.
    private readonly Action? _onBeforeEdit;

    public LiveNavTestField(NavRecord record, int fieldNo)
        : this(record, fieldNo, page: null, controlId: 0, onEdited: null, onBeforeEdit: null) { }

    public LiveNavTestField(NavRecord record, int fieldNo, RunnerPageInstance? page, int controlId,
        Action? onEdited, Action? onBeforeEdit)
    {
        _record = record;
        _fieldNo = fieldNo;
        _page = page;
        _controlId = controlId;
        _onEdited = onEdited;
        _onBeforeEdit = onBeforeEdit;
    }

    // The refusals this control has recorded, read back by ValidationErrorCount /
    // GetValidationError below. See TestFieldValidationErrors for BC's own contract: the
    // ITestField setter RECORDS a refusal, it does not throw it — NavTestField.CheckError
    // (BC's own precompiled code, wrapping every SetValue) is what raises it afterwards, and
    // it can only do that if the ledger survives the write (#2900).
    private readonly TestFieldValidationErrors _validationErrors = new();

    public string Value
    {
        // An option field answers with its MEMBER NAME, not the ordinal it stores. Returning the
        // ordinal made every comparison against a member name fail while looking like a data
        // problem ("expected <Mid>, got <0>") rather than a missing option table.
        get => (CurrentOption() is { } option
                   ? TestPageOptionValue.Display(option, OptionCaptions())
                   : null)
               ?? TestPageNumericValue.Format(_record.GetFieldValue(_fieldNo) as NavValue)
               // #2795: "Yes"/"No", not Convert.ToString's "True"/"False".
               ?? TestPageBooleanValue.Format(_record.GetFieldValue(_fieldNo) as NavValue)
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

        // Issue #1870 — the Rec-bound half of #1837 that #1869 (the page-variable half)
        // left open. FieldType (sourced from the source table field's own declared type,
        // see TryGetMetaFieldType) answers Boolean for a `field(Flag; Rec.Flag)` control
        // over a `Boolean` table field; falling through to ALCompiler.ToNavValue(value)
        // there always produced a NavText, which NavTestField.ALSetValue's own Boolean
        // ALValidateAsync then rejected with "The value 'True' can't be evaluated into
        // type Boolean" — the same shape of bug TestPageBooleanValue already fixed for
        // PageVariableTestField.
        var navValue = CurrentOption() is { } option
            ? TestPageOptionValue.Resolve(option, value, OptionCaptions(),
                $"TestPage SetValue (field {_fieldNo})")
            : FieldType == NavType.Boolean
                ? TestPageBooleanValue.Resolve(value, Caption)
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

        _onEdited?.Invoke();
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
    public void Activate() { }

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
    public void AssistEdit() { }

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

    public PageVariableTestField(RunnerPageInstance page, object expression, int controlId)
    {
        _page = page;
        _expression = expression;
        _controlId = controlId;
    }

    // The Rec-bound sibling's ledger, for the same reason and read the same way — see
    // LiveNavTestField._validationErrors and TestFieldValidationErrors. A page-global control
    // refuses a write through its OnValidate exactly as a Rec-bound one does, so leaving this
    // half hardcoded would have made the same AL assertion answer differently depending only
    // on how the control happens to be bound.
    private readonly TestFieldValidationErrors _validationErrors = new();

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
               ?? TestPageNumericValue.Format(RunnerPageInstance.GetValue(_expression))
               // #2795: the page-global half of the same rule — see TestPageBooleanValue.Format.
               ?? TestPageBooleanValue.Format(RunnerPageInstance.GetValue(_expression))
               ?? Convert.ToString(ObjectValue, CultureInfo.InvariantCulture)
               ?? string.Empty;
        // appendRefreshSuffix: false — a page-global control stages no row edit, so there is
        // nothing for "Refresh to discard" to discard. Microsoft's Tests-SINGLESERVER
        // Codeunit134614 asserts the bare text with exact equality for exactly this binding
        // shape (verified mechanically to be page-variable-bound, not Rec-bound). This is the
        // half no service-tier run has confirmed yet — corpus PR #184 asks it.
        set => _validationErrors.RunRecordingRefusal(() =>
        {
            RunnerPageInstance.SetValue(_expression, ToBoundValue(value));
            _page.RaiseOnValidate(_controlId);
        }, appendRefreshSuffix: false);
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
            NavDate => TestPageDateValue.Resolve(value, $"TestPage SetValue (control {_controlId})"),
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
    public void AssistEdit() { }
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
           ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    public string GetOption(int index)
        => CurrentOption() is { } option
            ? TestPageOptionValue.MemberAt(option, index,
                _page.TryGetOptionCaptions(_controlId, option))
            : string.Empty;
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
/// A page action driven live: Invoke() runs the page's own OnAction trigger, on the page
/// instance the TestPage is driving, so the trigger sees the current row.
///
/// Visible/Enabled come from the action's own declared properties, which are constants or
/// expressions evaluated against the page's live state — so an action gated on the current
/// row (<c>Enabled = RowEditable</c>) reports differently as the cursor moves.
/// </summary>
internal sealed class LiveNavTestAction : ITestAction
{
    private readonly LiveNavTestPage _testPage;
    private readonly RunnerPageInstance _page;
    private readonly int _actionId;

    public LiveNavTestAction(LiveNavTestPage testPage, RunnerPageInstance page, int actionId)
    {
        _testPage = testPage;
        _page = page;
        _actionId = actionId;
    }

    /// <summary>
    /// Save the current row, then run OnAction — BC's order, and the order matters. A real
    /// client sends the row it is on to the server before it invokes an action, so the AL in
    /// OnAction reads a <c>Rec</c> that exists in the table, with its AutoSplitKey field
    /// assigned. Dispatching straight to the trigger let the action see the field values a
    /// test had just set while the row itself was still nowhere.
    /// </summary>
    public void Invoke()
    {
        _testPage.SaveCurrentRow();
        _page.RaiseOnAction(_actionId);
    }

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
/// One entry of a part's SubPageLink, in the shape BC's compiler writes into
/// <c>InfopartPageDefinition.SubFormLink</c>: the PART's field it constrains, the kind, and
/// either the PARENT's field number (FIELD) or the compiled literal / filter expression
/// (CONST / FILTER) — see <c>MockTestPage.SubPageLinks</c> for the representation.
/// </summary>
internal readonly record struct SubPageLinkEntry(
    int PartFieldNo, Microsoft.Dynamics.Nav.Types.Metadata.FilterType Kind, int ParentFieldNo, string Value);

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
    // Null only when _links has no FIELD entry (issue #2053: a part on a SourceTable-less host
    // has no parent record and needs none; a CONST/FILTER-only part never reads one either) —
    // every read below sits inside a FIELD case, so a null parent is never dereferenced.
    private readonly NavRecord? _parentRecord;
    private readonly SubPageLinkEntry[] _links;

    /// <param name="record">The part page's own source-table cursor, or null when the part
    /// page declares NO SourceTable (issue #2195) — a CardPart bound to page globals, the
    /// info-box shape. Nothing in THIS class needs it in that case, and the reason is a
    /// property of SubPageLink rather than an observation about the parts seen so far: the
    /// only behaviour this class adds over LiveNavTestPage is the link, every SubPageLink
    /// entry names a field of the part's OWN source table, so a part with no source table
    /// cannot express one and <see cref="_links"/> is necessarily empty. Everything else is
    /// the base class's null-record path, where each Rec-dependent member refuses by name.</param>
    /// <param name="parentRecord">The host's current-row cursor; required only when
    /// <paramref name="links"/> carries a FIELD entry (<see cref="AnyFieldLink"/>).</param>
    public LiveNavTestPart(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page, object owner, int pageId,
        NavRecord? parentRecord, SubPageLinkEntry[] links)
        : base(record, controlIdToFieldNo, creatable, page, owner, pageId)
    {
        _parentRecord = parentRecord;
        _links = links;
    }

    public bool Enabled => true;
    public bool Visible => true;

    /// <summary>Whether any entry is a FIELD link — the only kind that reads the parent's row.</summary>
    internal static bool AnyFieldLink(SubPageLinkEntry[] links)
    {
        foreach (var link in links)
            if (link.Kind == Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD) return true;
        return false;
    }

    /// <summary>Filter the part's rowset to what its SubPageLink selects for the parent's
    /// current row: a FIELD entry to the parent's current value, a CONST entry to its literal,
    /// a FILTER entry to its expression (issue #2469).</summary>
    private void ApplyLink()
    {
        // Nothing to apply, and nothing to demand: an unlinked part shows its own table's
        // full rowset. The early return is what lets a part page with NO SourceTable exist
        // at all (issue #2195) — such a part cannot carry any SubPageLink, so it always
        // lands here, and without the return the RequireRecord below would refuse EVERY
        // cursor move on it naming "subpage link", a link the part does not have. The move
        // itself still refuses by its own name through the base class when the AL genuinely
        // asks a record-less part to navigate.
        if (_links.Length == 0) return;

        // Past this point the part is linked, which is only expressible against the part's
        // own source table — so it has one, and this is a guaranteed hit used for its record
        // rather than for its refusal.
        var record = RequireRecord("subpage link");
        foreach (var link in _links)
        {
            switch (link.Kind)
            {
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD:
                    record.ALSetRange(link.PartFieldNo, _parentRecord!.GetFieldValue(link.ParentFieldNo));
                    break;
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.CONST:
                    record.ALSetFilter(link.PartFieldNo, ConstFilterExpression(record, link.PartFieldNo, link.Value));
                    break;
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FILTER:
                    // Already in BC's filter grammar (the compiler wrote option members as
                    // ordinals; DependencyPageMetadataXml re-quoted AL identifiers) — BC's own
                    // filter parser, the one SetFilter uses, reads it. A malformed expression
                    // raises BC's own NavInvalidFilterExpressionException naming the text.
                    record.ALSetFilter(link.PartFieldNo, link.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// The compiled CONST literal as a filter expression BC's own filter parser reads as that
    /// ONE value. A Text/Code field gets the literal quoted: the compiler writes
    /// <c>const('SPECIAL')</c> as the bare text <c>SPECIAL</c>, and a bare literal is parsed as
    /// an EXPRESSION — a value containing <c>|</c>, <c>..</c>, <c>(</c> or <c>&amp;</c> would be
    /// read as operators, and an EMPTY literal would clear the filter (SetFilter's own rule for
    /// <c>''</c>) instead of selecting the blank value. Every other type — an option ordinal,
    /// a number, a boolean, a date — is handed over as written, exactly the text an AL
    /// SetFilter call would pass. A field the part's table does not declare is left to
    /// SetFilter, which refuses it with BC's own error naming the field number.
    /// </summary>
    private static string ConstFilterExpression(NavRecord record, int fieldNo, string value)
    {
        var navType = record.MetaTable.TryGetFieldByNo(fieldNo, out var field) ? field.FieldNavType : (NavType?)null;
        var quote = value.Length == 0 || navType is NavType.Text or NavType.Code;
        return quote ? "'" + value.Replace("'", "''") + "'" : value;
    }

    public override bool MoveFirst() { ApplyLink(); return base.MoveFirst(); }
    public override bool MoveLast() { ApplyLink(); return base.MoveLast(); }
    public override bool MoveNext() { ApplyLink(); return base.MoveNext(); }
    public override bool MovePrevious() { ApplyLink(); return base.MovePrevious(); }

    /// <summary>True when this part carries a FIELD SubPageLink — i.e. its rowset depends on
    /// the PARENT's current row. This is the signal <see cref="LiveNavTestPage.Loaded"/> uses
    /// to decide whether a parent row-load should refresh this part too (issue #2677). A part
    /// with no link, or with only CONST/FILTER links, shows a rowset independent of the
    /// parent's cursor and is never re-positioned by a parent's cursor move; its own initial
    /// row-load still happens once, from GetPart.</summary>
    internal bool HasLinks => AnyFieldLink(_links);

    /// <summary>
    /// Position this part on the row matching its SubPageLink and, if one exists, run its
    /// OnAfterGetRecord/OnAfterGetCurrRecord — the row-load a real BC FactBox/subpage part
    /// gets automatically, both when its host opens AND every time the host's own cursor
    /// moves to a different row. Issue #2677, corpus PR
    /// StefanMaron/BusinessCentral.AL.Language.Tests#141 (8 BC legs, OBS probes measuring a
    /// SubPageLink-bound CardPart): with NOTHING ever touching <c>CurrPage.&lt;part&gt;</c> or
    /// <c>TestPage.&lt;part&gt;</c>, opening the host alone produces
    /// <c>HostOpen;PartOpen;HostAGCR;PartAGCR</c> — the part's OnOpenPage runs right after the
    /// host's, and its OnAfterGetRecord/OnAfterGetCurrRecord runs right after the host's own,
    /// entirely unprompted. A later touch adds nothing (already loaded). Navigating the HOST
    /// to a different row (GoToRecord) re-fires the part's trigger for the NEW row and does
    /// NOT re-fire it for the row just left. Before this fix nothing EVER positioned a part's
    /// own cursor at all — <c>TestPageFactory.TryBuild</c> hands back a BLANK, unfetched
    /// record, and only an explicit MoveXxx/GoToBookmark call on the PART ITSELF (which
    /// nothing makes on its behalf) ever reached <see cref="LiveNavTestPage.Loaded"/> — so a
    /// part whose entire per-row state comes from that trigger (the common FactBox-summary
    /// shape) stayed at its field defaults for the page's whole life.
    ///
    /// Deliberately reuses <c>Loaded(bool)</c> rather than <c>MoveFirst()</c>: MoveFirst()
    /// also flushes pending parts/rows — a state change appropriate to an AL-driven cursor
    /// move, not to a parent row simply becoming current. Only the two steps a parent move
    /// really does are taken: run the FOUND case's trigger, or park on the draft line.
    ///
    /// THE NOT-FOUND CASE IS NOT "SHOW NOTHING" (#2923). It used to be, and that was the
    /// remaining half of #2392 applied to parts: a client renders an editable, insert-allowed
    /// repeater with no matching rows as exactly one row — its blank new-row line — and a
    /// write with no <c>New()</c> and no <c>First()</c> of its own lands there. Corpus
    /// codeunit 60743 <c>EmptyEditableList_SetValueWithoutNewOrFirst_InsertsARow</c> measured
    /// that on a real service tier for a standalone page; codeunit 60996
    /// <c>EmptyLinkedPart_WriteWithoutFirst_ValidateSeesTheLinkedKey</c> measures it for a
    /// LINKED part, which is the shape Microsoft's own document tests use. Leaving the part
    /// unpositioned sent that write into a record nothing had ever positioned.
    ///
    /// <c>AbandonNewRowLine</c> first, because this method is re-entered on every parent move:
    /// a draft line the part was parked on belongs to the parent being left, and
    /// <c>Loaded()</c> would not have cleared it.
    ///
    /// NOT once-guarded: every call re-applies the link filter and re-finds, which is exactly
    /// what makes a GoToRecord-driven refresh work. A repeat call for the SAME still-current
    /// row (a second control read with no intervening parent move) re-runs
    /// OnAfterGetRecord/OnAfterGetCurrRecord too — unmeasured against BC for that specific
    /// case (probe 2 only read a control, which triggers a fresh <c>GetPart</c> lookup that
    /// the `_parts` cache already short-circuits before reaching here at all, so it never
    /// re-entered this method a second time for the SAME touch).
    ///
    /// A record-less part (<see cref="LiveNavTestPage.Record"/> null, the page-globals-only
    /// CardPart shape from #2195) has no cursor to position and nothing here to do — its
    /// OnOpenPage is the only trigger such a part gets.
    /// </summary>
    internal void ReloadLinkedRow()
    {
        if (Record is not { } record) return;
        ApplyLink();
        AbandonNewRowLine();
        var found = record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
        Loaded(found);
        if (!found) EnterNewRowLine(record);
    }

    public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)
    {
        ApplyLink();
        return base.FindRowFromTableFieldValues(fieldNos, values, forward);
    }

    /// <summary>
    /// Start a new row already carrying the link's values — for the fields BC actually
    /// carries them onto, which is NOT every linked field.
    ///
    /// <c>ApplyLink</c> above has just put every entry on the record as a filter, and
    /// <c>base.InsertEmptyRow</c> then asks BC's own <c>NavForm.NewRecord</c> to start the row
    /// (<c>RunnerPageInstance.TryNewRecord</c>), which runs
    /// <c>RecordImplementation.InitRecordFromFilters</c>. That method — Ncl 28.1,
    /// <c>InitRecordFromFilters(includeNonPrimaryKeyFields, includeIdenticalFilters,
    /// includeNonPrimaryKeyFieldsForFilterGroups)</c> — copies a field's filter onto the new
    /// record only when the filter is <c>FilterExpressionType.Equal</c> (exactly one value)
    /// AND one of: the field is part of the PRIMARY KEY, the page sets
    /// <c>PopulateAllFields</c>, or the caller names the filter's group.
    /// <c>NavForm.NewRecordAsync(bool)</c> passes <c>Array.Empty&lt;int&gt;()</c> for the
    /// groups, so on a TestPage it comes down to key membership.
    ///
    /// This loop therefore applies the same key-membership gate. Without it the runner
    /// stamped every single-valued link onto the new row regardless of the key, which real BC
    /// does not do: measured on all 8 BC legs of corpus codeunit 60324 "TSPL Tests", a
    /// <c>New()</c> through a part linked <c>Kind = const(Attachment)</c>, where Kind is not
    /// part of the line table's key, produced a row with Kind still at Comment — outside the
    /// part's own filter, which BC then reported as "The view is filtered, and the entry is
    /// outside the filter". The corpus pins both directions: the same const on a table whose
    /// key CONTAINS the field IS stamped.
    ///
    /// The loop is not redundant with BC's own step even for the fields it does write. It
    /// covers the record-only fallback in <c>LiveNavTestPage.InsertEmptyRow</c>, where there
    /// is no page to ask and BC's filter step never runs; where BC's step did run it writes
    /// the same values again, which is a no-op. A FIELD entry is read from the parent's
    /// current row; a CONST/FILTER entry is read back through BC's own range accessors on the
    /// filter <c>ApplyLink</c> just set, so a single-value filter answers the same typed value
    /// for min and max (an option ORDINAL arrives as a NavOption, not as the text "1") and a
    /// multi-value or open-ended one raises BC's own error and stamps nothing — the
    /// <c>Equal</c> half of the same rule, decided by BC rather than re-derived from text.
    /// </summary>
    public override void InsertEmptyRow(bool beforeCurrent)
    {
        ApplyLink();
        base.InsertEmptyRow(beforeCurrent);
        if (_links.Length == 0) return;
        var record = RequireRecord("subpage link");
        var primaryKeyFieldNos = PrimaryKeyFieldNos(record);
        // What was actually stamped, in stamping order — BC's own
        // `fieldsInitializedFromFilters`, which is the exact set its validate step runs over.
        var stamped = new List<(int FieldNo, NavValue Value)>();
        foreach (var link in _links)
        {
            // Not part of the primary key: BC leaves it at its Init() value, so the runner
            // must too — a stamped value here would put a row inside a filter BC would have
            // reported as outside it.
            if (!primaryKeyFieldNos.Contains(link.PartFieldNo)) continue;
            switch (link.Kind)
            {
                case Microsoft.Dynamics.Nav.Types.Metadata.FilterType.FIELD:
                    var linked = _parentRecord!.GetFieldValue(link.ParentFieldNo);
                    record.SetFieldValue(link.PartFieldNo, linked);
                    stamped.Add((link.PartFieldNo, linked));
                    break;
                default:
                    if (TryGetSingleFilterValue(record, link.PartFieldNo, out var single))
                    {
                        record.SetFieldValue(link.PartFieldNo, single);
                        stamped.Add((link.PartFieldNo, single));
                    }
                    break;
            }
        }
        ValidateStampedFields(record, stamped);
    }

    /// <summary>
    /// Run OnValidate on the fields the link just stamped — <c>NavForm.NewRecordAsync</c>'s
    /// second step, which the runner did not perform at all (issue #2551, gap 2).
    ///
    /// <para>BC's body is two steps in order: copy the link's values onto a freshly reset
    /// buffer, then
    /// <c>if (ValidateFieldsInOnNewRecord) SourceTable.ValidateFieldsAsync(fieldsInitializedFromFilters, ...)</c>
    /// — OnValidate on exactly the fields step 1 copied, and nothing else. The runner performed
    /// step 1 and stopped, so a field carrying a value from the link arrived RAW: its own
    /// OnValidate never ran, and anything that trigger derives stayed at its Init() default
    /// while the field itself already held the linked value. That is a wrong answer rather than
    /// a missing feature, which is why it is fixed rather than declared out of scope.</para>
    ///
    /// <para><c>ValidateFieldsInOnNewRecord</c> is a plain auto-property with no setter anywhere
    /// in Ncl, so nothing in the decompiled runtime says which way it is set — only a service
    /// tier can answer it. It is answered: corpus codeunit 60653 "NRB Tests"
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#150) measured on all eight BC legs that a
    /// New() through a field(...) link DOES run the stamped field's OnValidate. So the flag is
    /// set by whatever drives a TestPage's New(), and the runner validates unconditionally here
    /// rather than modelling a flag nothing it can see ever writes.</para>
    ///
    /// <para>Copy-then-validate, not validate-during-copy: BC hands its validate step the whole
    /// set after the copy loop finishes, so an OnValidate on the first stamped field already
    /// sees the others in place. Validating inside the loop would show it a half-stamped row.</para>
    ///
    /// <para>Deliberately NOT wrapped in the <c>CurrFieldNo</c> assignment that
    /// <c>ValueControl.SetValue</c> uses (#2705). That one models a PAGE-ORIGINATED write, and
    /// BC's step here is <c>SourceTable.ValidateFieldsAsync</c> — a record-level call, the same
    /// shape as <c>Rec.Validate</c>, which real BC leaves CurrFieldNo at 0 for. No corpus test
    /// pins CurrFieldNo during New(), so this follows the mechanism rather than guessing.</para>
    ///
    /// <para>Errors propagate. An OnValidate that refuses the linked value is BC refusing to
    /// start the row, and swallowing it here would hand the test a row real BC never creates.</para>
    /// </summary>
    private static void ValidateStampedFields(NavRecord record, List<(int FieldNo, NavValue Value)> stamped)
    {
        foreach (var (fieldNo, value) in stamped)
            record.ALValidateAsync(fieldNo, value, null).GetAwaiter().GetResult();
    }

    /// <summary>The field numbers making up the record's primary key — the membership test
    /// <c>NCLMetaField.FieldIsPartOfPrimaryKey</c> answers inside BC, read off the same
    /// <c>MetaTable.PrimaryKey</c> the AutoSplitKey path already uses so both agree on the key
    /// shape. Empty for a table whose key metadata is unavailable, which makes the caller stamp
    /// nothing rather than stamp on a guess.</summary>
    private static HashSet<int> PrimaryKeyFieldNos(NavRecord record)
    {
        var fieldNos = new HashSet<int>();
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null) return fieldNos;
        for (var i = 0; i < primaryKey.KeyFieldCount; i++)
            fieldNos.Add(primaryKey.KeyFieldsList[i].FieldNo);
        return fieldNos;
    }

}
