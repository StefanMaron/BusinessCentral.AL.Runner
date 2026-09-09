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
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;

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

    // The page object a SUBCLASS needs: LiveNavTestPart re-positions a linked part through the
    // host page's own OnFindRecord (issue #3439), and _page itself is private.
    private protected RunnerPageInstance? PageInstance => _page;

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

        // A part whose own Visible — or that of any group enclosing it — is the compile-time
        // LITERAL false is not rendered into the test page's control tree at all, exactly as
        // an eliminated FIELD control is not (see LiveNavTestPage.GetField, which has done
        // this since #1778). Returning null is what makes that faithful: the caller is
        // NavTestPageBase.GetPart(int,bool), a precompiled BC method, and when ITestPage.GetPart
        // answers null it raises BC's own NavTestPartNotFoundException ("The part with ID = ...
        // was not found on the page.") itself — so this part gets the EXACT exception real BC
        // raises, not a runner-invented one, and not a RunnerOutOfScopeException, which would
        // wrongly classify implementable BC behaviour as out of scope.
        //
        // Measured on a real service tier by corpus codeunit 60346
        // (StefanMaron/BusinessCentral.AL.Language.Tests#227, all 8 cloud legs): the suite's
        // first revision asserted the PAIR — a Visible = true part answering true and a
        // Visible = false part answering false on one open host — and every leg falsified it
        // identically with "The part with ID = 1318487454 was not found on the page." The
        // suite's own fixture comment records the conclusion: reaching a part declared
        // Visible = false errors; it does not yield a handle reporting false.
        //
        // NOTE what this does NOT say. NavTestPart genuinely overrides ALVisible/ALEnabled in
        // the IL as `return testPart.Visible` / `return testPart.Enabled`, reading the part
        // control's own metadata — those overrides are real and are not being contradicted.
        // What the tier establishes is that AL cannot REACH a control whose Visible would be
        // false, so those overrides can never be observed returning false. The claim here is
        // about reachability, not about what the accessors return.
        //
        // A Visible bound to a variable or an expression is never eliminated this way, even
        // while it currently evaluates false — see
        // RunnerPageInstance.ControlIsCompileTimeEliminated for the literal-vs-expression
        // distinction and the ancestor walk, which this shares with the field path rather
        // than re-deriving.
        if (_page?.ControlIsCompileTimeEliminated(controlId) == true) return null!;

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
    ///
    /// <para>The non-lookup half is PAGE-TYPE-dependent as well, which is issue #3284: the OK
    /// above is what a <c>Worksheet</c> reports, not what every page reports. See
    /// <see cref="UnattendedCloseResult(string?)"/>.</para>
    /// </summary>
    private FormResult _formResult
        => _invokedFormResult
           ?? (_page?.LookupMode == true
               ? FormResult.LookupCancel
               : UnattendedCloseResult(RecordPatches.TryGetAnyPageType(_pageId)));

    /// <summary>
    /// What <c>RunModal()</c> and <c>OnQueryClosePage</c> report for a NON-lookup page the
    /// handler closed by returning without invoking anything.
    ///
    /// <para>MEASURED ON A REAL SERVICE TIER (BC 28.4.53241.0), corpus codeunit 60338
    /// "TBA Tests" arms i-l, plus "MQC Tests" (60276) arm b for the Worksheet row:
    /// <c>StandardDialog</c>, <c>PromptDialog</c> and <c>ConfirmationDialog</c> report
    /// <b>Cancel</b>; <c>NavigatePage</c> and <c>Worksheet</c> report <b>OK</b>. Issue #3284
    /// — before it, every page type answered OK, so AL written as
    /// <c>if Dlg.RunModal() = Action::OK then</c> took the confirming branch here and the
    /// cancelling one on BC, with nothing raised to say so.</para>
    ///
    /// <para>This is NOT derivable from <see cref="HasDialogCancelAffordance(string?)"/> and
    /// must not be folded into it: a <c>ConfirmationDialog</c> reports Cancel here while
    /// refusing <c>Cancel()</c> as a built-in (arms g and k), and a <c>NavigatePage</c> reports
    /// OK while refusing it (arms e and l). Two independent facts about the same PageType.</para>
    ///
    /// <para>An UNKNOWN PageType keeps OK, for the same reason the affordance rule keeps its
    /// permissive answer: null means the page is not in this run's inventory, not that it is
    /// a dialog.</para>
    /// </summary>
    internal static FormResult UnattendedCloseResult(string? pageType)
        => pageType != null
           && (string.Equals(pageType, "StandardDialog", StringComparison.OrdinalIgnoreCase)
               || string.Equals(pageType, "PromptDialog", StringComparison.OrdinalIgnoreCase)
               || string.Equals(pageType, "ConfirmationDialog", StringComparison.OrdinalIgnoreCase))
            ? FormResult.Cancel
            : FormResult.OK;

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
    ///
    /// <para>Plain <c>Cancel</c> and plain <c>OK</c> each carry a SECOND condition on top of
    /// that, and the two are not each other's mirror: a page has a built-in Cancel only when
    /// its PageType gives the client dialog chrome to put one on
    /// (<see cref="HasDialogCancelAffordance(string?)"/>), while OK is refused on a
    /// ConfirmationDialog and on a PromptDialog that declares its own
    /// (<see cref="HasPlainOkAffordance(string?, bool)"/>).</para>
    /// </summary>
    private bool Offers(FormResult formResult)
        => OffersBuiltInAction(
            formResult,
            // Null, not false: no RunnerPageInstance means the page's lookup mode is UNKNOWN
            // (a precompiled page the runner could not build one for), which is a different
            // input from "opened as a normal page" and must not collapse into it.
            lookupMode: _page?.LookupMode,
            pageType: RecordPatches.TryGetAnyPageType(_pageId),
            // False for a precompiled page the AL parser never saw, which keeps today's
            // permissive OK for those rather than inventing a refusal from a lookup miss.
            declaresSystemActionOk: RecordPatches.PageDeclaresSystemAction(_pageId, "OK"));

    /// <summary>
    /// The decision behind <see cref="Offers"/>, as a pure function of the three inputs, so it
    /// can be pinned without a loaded BC runtime (the shape
    /// <c>TestPageClientConstructionRule</c> uses for the same reason).
    /// <paramref name="lookupMode"/> is null when the page's mode is unknown.
    /// </summary>
    internal static bool OffersBuiltInAction(
        FormResult formResult, bool? lookupMode, string? pageType, bool declaresSystemActionOk = false)
    {
        // Asked before the lookup test on purpose: a lookup page must answer NULL for plain
        // Cancel either way, so BC's FindBuiltInAction(Cancel, LookupCancel) falls through to
        // LookupCancel. The two conditions agree there and only the non-lookup case differs.
        // The same holds for the OK pair below.
        if (formResult == FormResult.Cancel && !HasDialogCancelAffordance(pageType)) return false;
        if (formResult == FormResult.OK && !HasPlainOkAffordance(pageType, declaresSystemActionOk)) return false;

        if (lookupMode is not bool lookup) return true;
        return formResult switch
        {
            FormResult.OK or FormResult.Cancel => !lookup,
            FormResult.LookupOK or FormResult.LookupCancel => lookup,
            _ => true,
        };
    }

    /// <summary>
    /// Whether this page's PageType gives the client a real Cancel affordance, which is what
    /// BC requires before <c>TestPage.Cancel()</c> resolves to anything. A page without one
    /// gets <c>NavTestActionNotFoundException</c>: "The built-in action = Cancel is not found
    /// on the page." — and that is NOT the same rule as plain OK, which every non-lookup page
    /// has.
    ///
    /// <para>MEASURED ON A REAL SERVICE TIER, all eight cloud legs, and it is the correction of
    /// a wrong prediction. Corpus "MQC Tests" (codeunit 60276) first carried an arm asserting
    /// that a cancelled PLAIN modal reports Action::Cancel, by symmetry with the OK/LookupOK
    /// pair. BC refused it, and the arm is now
    /// <c>PlainModal_HasNoBuiltInCancelAction</c> — <c>Cancel().Invoke()</c> on
    /// "MQC Trace Modal" (PageType = Worksheet) raises rather than closing the page
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#192). The corpus records the same refusal
    /// on two further shapes: a plain Card opened with <c>OpenNew()</c>
    /// (TestPageRecordTriggers.al) and the precompiled List page "Error Messages"
    /// (TestPageModalHandler_PrecompiledPage.al). Its positive side is
    /// <c>PageType = StandardDialog</c>, which every green <c>Cancel().Invoke()</c> in the
    /// corpus and in tests/runner-extras is aimed at — and TestPageModalHandler_ModalPage.al
    /// states the finding directly: "an action literally named Cancel is not enough on a plain
    /// Card-type modal (still 'not found' even when declared); PageType = StandardDialog is
    /// what actually gives the client OK/Cancel chrome."</para>
    ///
    /// <para>The set was previously reasoned out from a builder fact — <c>FormState.RunModal</c>
    /// is assigned in exactly two of BC's UI builders, <c>NavigatePageBuilder</c> and
    /// <c>StandardDialogBuilder</c> — and issue #3131 recorded NavigatePage and
    /// ConfirmationDialog as inferences to be measured. They have been (issue #3283, corpus
    /// codeunit 60338 "TBA Tests" arms a, b, e, g), and the NavigatePage inference was WRONG:
    /// a NavigatePage refuses <c>Cancel()</c> — its chrome is Back/Next/Finish — while
    /// <c>PromptDialog</c>, which the builder fact does not cover at all, offers one. The
    /// ConfirmationDialog inference held; it refuses both Cancel and OK.</para>
    ///
    /// <para>PromptDialog offers a plain Cancel whether or not it declares
    /// <c>systemaction(Cancel)</c>, and whatever its <c>PromptMode</c>, because
    /// <c>PromptDialogBuilder.BeginBuildActionBar</c> calls
    /// <c>PageBuilder.AddActionBar(form, context, FormResult.Cancel)</c> unconditionally. That
    /// explains the measurement; the service-tier arms are the evidence for it.</para>
    ///
    /// <para>An UNKNOWN PageType keeps today's permissive answer rather than refusing: null
    /// from <c>TryGetAnyPageType</c> means the page is not in this run's inventory at all, not
    /// that it declares no dialog chrome, and turning that into a "not found" would refuse
    /// pages on the strength of a lookup miss.</para>
    /// </summary>
    internal static bool HasDialogCancelAffordance(string? pageType)
        => pageType == null
           || string.Equals(pageType, "StandardDialog", StringComparison.OrdinalIgnoreCase)
           || string.Equals(pageType, "PromptDialog", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this page gives the client a plain <c>OK</c> — the condition on
    /// <c>TestPage.OK()</c>, which is nearly-but-not-quite "every non-lookup page".
    ///
    /// <para>MEASURED ON A REAL SERVICE TIER (BC 28.4.53241.0), corpus codeunit 60338
    /// "TBA Tests". Two page shapes refuse it, and neither is predictable from the Cancel
    /// rule:</para>
    ///
    /// <list type="bullet">
    /// <item><c>ConfirmationDialog</c> (arm h) — its chrome is Yes/No, so it has neither
    /// built-in. The runner used to answer OK here for every non-lookup page, so AL that BC
    /// refuses closed the page instead: a silent wrong answer, not a visible one.</item>
    /// <item>A <c>PromptDialog</c> that DECLARES <c>systemaction(OK)</c> (arms c and d). The
    /// declaration REPLACES the built-in rather than adding to it —
    /// <c>PromptDialogBuilder.BuildPromptActions</c> adds an <c>ExitAction</c> for OK only on
    /// the else-branch — so the same page without the declaration does offer it. This is the
    /// one row here that is not a function of PageType alone.</item>
    /// </list>
    ///
    /// <para><paramref name="declaresSystemActionOk"/> is false for a page the AL parser never
    /// saw (precompiled dependency), which keeps the permissive answer for those; see
    /// <c>RecordPatches.PageDeclaresSystemAction</c>.</para>
    /// </summary>
    internal static bool HasPlainOkAffordance(string? pageType, bool declaresSystemActionOk)
    {
        if (pageType == null) return true;
        if (string.Equals(pageType, "ConfirmationDialog", StringComparison.OrdinalIgnoreCase)) return false;
        if (declaresSystemActionOk
            && string.Equals(pageType, "PromptDialog", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// AL's <c>SomePage.View()</c> — the page's built-in "open read-only" page-mode action.
    /// Before #3185 this answered the base mock's no-op action, and could not even be reached:
    /// NavTestPageBase.ALView() wraps it in TestClientProxy.Proxy, which NclCecilRewrite's
    /// hard-coded method list did not strip, so every call raised "The UISessionManager was
    /// expected to be initialized." See AlRunner/Patches/BuiltInPageModeAction.cs.
    /// </summary>
    public override ITestAction View() => BuiltInPageModeActionFor(viewMode: true);

    /// <summary>AL's <c>SomePage.Edit()</c> — the editable twin of <see cref="View"/>.</summary>
    public override ITestAction Edit() => BuiltInPageModeActionFor(viewMode: false);

    /// <summary>
    /// The built-in page-mode action for one of the two modes. Which of BC's three shapes it
    /// is — open a card, switch this page's own mode, or do nothing — is decided here, and
    /// each shape is measured on a real service tier: corpus codeunit 60479 "TPMS Tests"
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#317) plus 60461 "TPVE Tests" for the
    /// card-opening one. BuiltInPageModeAction.cs carries the table.
    ///
    /// <para>The action EXISTS in every one of those shapes: BC answers Visible = true
    /// throughout and expresses "this does not apply here" through Enabled instead. So the
    /// only refusals left below are the two shapes where BC has no action object at all.</para>
    /// </summary>
    private ITestAction BuiltInPageModeActionFor(bool viewMode)
    {
        if (_tornDown || RunnerPageInstance.WasClosedFromAl(_page?.Form))
            throw MakeTestPageNotOpenException();

        var actionName = viewMode ? "View" : "Edit";
        var cardPageId = RecordPatches.TryGetAnyCardPageId(_pageId);

        // The decision itself is a pure function of these four inputs, so every row of the
        // measured table is pinned without a BC runtime — BuiltInPageModeActionRule.cs.
        var shape = BuiltInPageModeActionRule.ResolveShape(
            cardPageId, RecordPatches.TryGetAnyPageType(_pageId), viewMode, DeclaredPageEditable);

        switch (shape)
        {
            // Null PageType means the page is in neither source's inventory, so the runner
            // cannot tell which of BC's two no-CardPageId shapes applies — a list's do-nothing
            // action, or a non-list's in-place switch. They differ in whether the page's
            // editability moves, which is exactly what the caller is about to read, so picking
            // one would be a silent wrong answer.
            case BuiltInPageModeShape.RefuseUnknownPageType:
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage.{actionName}() on page {_pageId}",
                    $"not-yet-implemented — page {_pageId} declares no CardPageId and its PageType "
                    + "is not in this run's inventory, so the runner cannot tell whether BC would "
                    + "switch this page's mode in place (not a List) or do nothing (a List). "
                    + "Tracked by issue #3735");

            // A page declaring Editable = false has no built-in Edit action at all, and BC does
            // not report that as an AL error: TestPage.Edit() hands back a NavTestAction wrapping
            // a null client action, so .Invoke() and .Visible() each raise a bare
            // NullReferenceException from NavTestAction.ALInvoke()/ALVisible() — caught by
            // neither asserterror nor a [TryFunction] (measured on BC 28.4.53241.0 both ways;
            // corpus 60479's file header records it, which is why no arm there asserts it).
            // Refusing by name is a DELIBERATE divergence: a bare NRE inside Ncl names neither
            // the page nor the reason. See docs/limitations.md#testpage-page-mode-no-edit-action.
            case BuiltInPageModeShape.RefuseNoEditAction:
                throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                    $"TestPage.Edit() on page {_pageId}",
                    $"testpage-page-mode-no-edit-action — page {_pageId} declares Editable = false, "
                    + "so BC's UI builder creates no built-in Edit action for it and real BC raises "
                    + "a bare System.NullReferenceException from NavTestAction.ALInvoke(). The "
                    + "runner refuses by name instead of reproducing an exception that names "
                    + "nothing");

            case BuiltInPageModeShape.OpenCard:
                return new BuiltInPageModeAction(
                    this, _record, cardPageId, viewMode, BuiltInPageModeActionKind.OpenCard);

            case BuiltInPageModeShape.NoTarget:
                return new BuiltInPageModeAction(
                    this, _record, targetPageId: 0, viewMode, BuiltInPageModeActionKind.NoTarget);

            default:
                return new BuiltInPageModeAction(
                    this, _record, targetPageId: 0, viewMode, BuiltInPageModeActionKind.InPlaceSwitch);
        }
    }

    /// <summary>The page's declared <c>Editable</c>, true for a page with no metadata here.</summary>
    internal bool DeclaredPageEditable => _page?.PageEditable ?? true;

    /// <summary>This page's current static editability — what <c>TestPage.Editable()</c> answers.</summary>
    internal bool StaticEditableNow => _staticEditable;

    /// <summary>
    /// The in-place half of a built-in page-mode action: the page already open changes mode,
    /// nothing opens, and OnOpenPage does not run again (corpus 60479
    /// CardOpenedReadOnlyIsMadeEditableInPlaceByItsEditAction asserts the open count stays 1).
    ///
    /// <para>It writes the same field, by the same formula, that <see cref="MarkOpened"/> writes
    /// for a page the test opened — which is what makes the switch work for a page a
    /// [ModalPageHandler] was handed too, where that field starts null
    /// (ResolveStaticEditable takes the override in preference to everything else). BC does this
    /// through PageModeAggregator.ChangePageMode on a client LogicalForm; that type lives in
    /// Microsoft.Dynamics.Nav.Client.UI.dll and acts on a form the runner never builds, so there
    /// is no BC machinery here to reuse.</para>
    /// </summary>
    internal void SwitchViewModeInPlace(bool viewMode)
        => _staticEditableOverride = !viewMode && DeclaredPageEditable;

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

            // On BC this invoke IS the close attempt -- see AttemptHandlerDrivenClose.
            _page.AttemptHandlerDrivenClose(_result);
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
                // matching real BC) -- carried so the runner can still NAME what actually
                // failed inside the trigger. Issue #3189: this used to be written under a key
                // nothing read, which made 132 Tests-SMB failures report a message with no
                // cause anywhere in the run. Infrastructure.MaskedTriggerErrorDiagnosis reads
                // it into the failure's diagnosis line, and MissingTestDataDiagnosis walks
                // through it so the mask does not hide ITS evidence either.
                if (original != null)
                    ex.Data[Infrastructure.MaskedTriggerErrorDiagnosis.ConvertedErrorDataKey] = original;
                return ex;
            }
        }
        // Same contract on the fallback path: the converted error is reachable both as the
        // InnerException and under the key the diagnosis reads, so which construction path ran
        // cannot change what the failure reports.
        var fallback = new System.InvalidOperationException(msg, original);
        if (original != null)
            fallback.Data[Infrastructure.MaskedTriggerErrorDiagnosis.ConvertedErrorDataKey] = original;
        return fallback;
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

    /// <summary>
    /// Turn the current position into a pending insert.
    ///
    /// <para>SKIPS THE PLATFORM'S NEW-RECORD STEP WHEN THE ROW IS ALREADY STARTED (#3029). Two
    /// callers arrive on a draft line <see cref="EnterNewRowLine"/> has already started: a
    /// write promoting it (<see cref="PromoteNewRowLineForWrite"/>) and a <c>New()</c> on a
    /// part that opened over an empty rowset. Neither creates a SECOND row — both commit to
    /// the one the blank line already stands for — so re-running the step would raise the
    /// page's OnNewRecord twice for one row AND re-blank the buffer, discarding what that
    /// trigger wrote.</para>
    ///
    /// <para>Read off <c>_newRowLineRecordStarted</c> rather than passed in by each caller.
    /// Both spellings were built and mutation-tested; the parameter turned out to be dead,
    /// because the state it duplicated is exactly the state the callers would have had to
    /// consult in order to set it. One source of truth is what stops the two from disagreeing.
    /// Everything else the entry point does is still owed on these paths — the flush of a
    /// previous pending row, the insert-position capture that feeds AutoSplitKey, a part's
    /// SubPageLink stamping and its validate step — so only the one call is skipped.</para>
    /// </summary>
    public override void InsertEmptyRow(bool beforeCurrent)
    {
        // A page with no SourceTable has no rowset to insert into at all — refuse by name
        // before touching any of the state below, rather than NRE-ing inside CaptureInsertPosition.
        RequireRecord("New()");

        // New() from the new-row line starts the row explicitly; the draft bookkeeping is
        // superseded by the CaptureInsertPosition below, and its saved return position must
        // not survive to drag the cursor back off the row being created.
        //
        // NEW() ON A STARTED DRAFT LINE IS THE SAME ROW (#3029). A part that opened over an
        // empty rowset is already parked on its draft line, and the platform has already run
        // its new-record step for it. New() there does not create a SECOND row — it commits to
        // the one the blank line already stands for, exactly as typing into it does. So the
        // same fact the promotion passes explicitly is also true when the caller did not say
        // so, and is read off the latch rather than demanded of every caller.
        var alreadyStarted = _onNewRowLine && _newRowLineRecordStarted;

        _onNewRowLine = false;
        _newRowLineReturnPosition = null;
        // The draft line is being consumed either way, so whatever it started is now this row's
        // — the NEXT draft line owes its own new-record step (#3029).
        _newRowLineRecordStarted = false;

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
        // alreadyStarted: the draft line being promoted already ran this exact step when the
        // cursor landed on it, so running it again would raise the page's OnNewRecord a second
        // time for one row AND re-blank the buffer, discarding what that trigger wrote (#3029).
        if (!alreadyStarted && !(_page?.TryNewRecord(!beforeCurrent) ?? false))
        {
            // Record-only mode: no page to ask, so no filters and no trigger to run either.
            // Non-null: guaranteed by the RequireRecord guard at the top of this method.
            _record!.ALInit();
            // The tail of NavForm.NewRecordAsync is `OldRecord.ALAssign(SourceTable)`, and
            // TryNewRecord runs it on the page path. Record-only mode never reaches BC's
            // method at all, so the snapshot RowValuesChangedSinceLoad compares against has to
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
    /// <para>Applied to <see cref="FlushPendingModify"/> too, since issue #3055 — one gate, both
    /// halves, which is also SaveRecordAsync's own shape. The open question recorded here used
    /// to be whether the OR's second arm (<c>calledFromALCode &amp;&amp;
    /// RecordImplementation.HasChangedFields</c>) let a same-value write through, since
    /// <c>_pendingModify</c> is set by <see cref="MarkEdited"/> on any assignment. It does not:
    /// that arm reaches <c>MutableRecordBuffer.HasActualChangedValues()</c>, which returns
    /// <c>false</c> unless some modified field fails <c>IsChangedValueSameAsOriginalValue</c>.
    /// Both arms are value comparisons, so neither writes a row that did not move.
    /// <c>docs/testpage-write-gate.md</c> has the decompiled bodies and what measured them.</para>
    /// </summary>
    private bool RowValuesChangedSinceLoad()
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
        // See RowValuesChangedSinceLoad for the mechanism and what measured it.
        //
        // The captured insert position is dropped with the row: it describes bounds read at
        // THIS New()'s cursor, and leaving it armed would offer them to the next insert, which
        // may be on another row or another part entirely.
        if (!RowValuesChangedSinceLoad()) { _insertPositionCaptured = false; return; }
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
        // The row is now the page's own row, so it is also its own before-image — BC's
        // NavForm.InsertAsync does exactly this, under exactly this guard
        // (`if (SourceTable.HasBeenInserted) OldRecord.ALAssign(SourceTable)`). Without it the
        // next write on the same page instance would compare against, and report as xRec, the
        // blank row New() started (issue #3440).
        if (_record!.HasBeenInserted) SnapshotBeforeImage();
    }

    /// <summary>
    /// Abandon an in-progress new row without writing it — how Cancel closes. Clears the
    /// captured insert position for the same reason FlushPendingNewRow's discard branch does:
    /// the bounds belong to the row being thrown away, and an armed capture would be consumed
    /// by whatever inserts next.
    /// </summary>
    internal void DiscardPendingNewRow()
    { _pendingNewRow = false; _pendingModify = false; _onNewRowLine = false; _newRowLineReturnPosition = null; _insertPositionCaptured = false; _newRowLineRecordStarted = false; }

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
        //
        // alreadyStarted: EnterNewRowLine ran the platform's new-record step when the cursor
        // landed on this line — that is the measured BC behaviour (corpus codeunit 60996,
        // 8 legs). Typing does not start the row a second time; it decides that the row already
        // started will be SAVED. Passing false here raised the page's OnNewRecord twice for one
        // row and re-blanked the buffer under the trigger's own output (#3029).
        //
        // Virtual on purpose: a part must reach LiveNavTestPart's override, whose SubPageLink
        // stamping and validate step are still owed on this path.
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
        if (!_pendingNewRow) { _pendingModify = true; return; }

        InsertOnCompletePrimaryKey();
    }

    /// <summary>
    /// Write a page-driven insert as soon as the row's PRIMARY KEY is complete, rather than
    /// holding it back until the page is left — issue #3441.
    ///
    /// Measured on real BC 28.4 and adjudicated on eight cloud legs (corpus codeunit 60636
    /// <c>NewAndInsertRecordEvents_PageDrivenInsert_FireForTheKeyOnly</c>): typing the key of a
    /// new row on a <c>DelayedInsert = false</c> list inserts the row THERE, so
    /// <c>OnInsertRecord</c> and <c>OnInsertRecordEvent</c> see the key set and every later
    /// control still blank, and the next control write is an ordinary page-driven MODIFY with
    /// its own trigger and event. The runner deferred the whole thing to the flush, so the
    /// insert trigger saw a finished row and the modify never happened at all.
    ///
    /// <para>Three limits. A page that saves one RECORD rather than rows keeps the
    /// flush-on-leave timing — see RunnerPageInstance.WritesRowsAsTheyAreCompleted, where both
    /// directions are measured. <c>DelayedInsert = true</c> keeps it too, which is that
    /// property's own definition. And "complete" is BC's own emptiness test —
    /// <c>NavValue.IsZeroOrEmpty</c>, what <c>NavForm.SplitKey</c> uses on the last key field —
    /// so a page whose last key field is filled in BY <c>AutoSplitKey</c> reads as incomplete
    /// while that field is still 0 and keeps the deferred path. Every editable line grid in BC
    /// is that shape, and moving them would change insert timing for the draft-line tests
    /// (corpus 60358/60648) on no evidence.</para>
    /// </summary>
    private void InsertOnCompletePrimaryKey()
    {
        // No page: record-only mode has no DelayedInsert property to read and no page triggers
        // to get the timing wrong, so it keeps the flush-time insert.
        if (_page == null || _page.DelaysInsertUntilTheRowIsLeft) return;
        // A Card saves its one record when the page is left, not when its key is typed — corpus
        // codeunit 60844 Close_WithoutOK_StillPersistsTheNewRow asserts the row is absent right
        // up to Close(), and says in its own message that it is there to catch an eager insert.
        if (!_page.WritesRowsAsTheyAreCompleted) return;
        if (!PrimaryKeyIsComplete(_record!)) return;
        // The same call the flush points make, so the insert keeps BC's order — the write gate,
        // SplitKey, OnInsertRecord's veto, then the record's own Insert — and clears
        // _pendingNewRow, which is what makes the NEXT control write a Modify.
        FlushPendingNewRow();
    }

    /// <summary>Every primary-key field holds a value. <c>NavValue.IsZeroOrEmpty</c> is BC's own
    /// spelling of "this key field has not been filled in" — <c>NavForm.SplitKey</c> tests the
    /// last key field with it before computing an AutoSplitKey value.</summary>
    private static bool PrimaryKeyIsComplete(NavRecord record)
    {
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null || primaryKey.KeyFieldCount == 0) return false;
        for (var i = 0; i < primaryKey.KeyFieldCount; i++)
            if (record.GetFieldValue(primaryKey.KeyFieldsList[i].FieldNo).IsZeroOrEmpty)
                return false;
        return true;
    }

    internal void FlushPendingModify()
    {
        if (!_pendingModify) return;
        _pendingModify = false;

        // A row whose values did not actually MOVE is not written, so its OnModify does not run
        // (issue #3055). _pendingModify only records that a control assigned a field; BC's
        // SaveRecordAsync decides on the values themselves, and both arms of its gate are value
        // comparisons — CompareAllNormalFields against OldRecord, ORed with
        // RecordImplementation.HasChangedFields, which reaches
        // MutableRecordBuffer.HasActualChangedValues and returns false unless some modified
        // field fails IsChangedValueSameAsOriginalValue. See docs/testpage-write-gate.md.
        //
        // Before the veto, not after, because that is where BC puts it: SaveRecordAsync returns
        // without ever reaching RaiseOnModifyRecordAsync when the comparison finds nothing. A
        // page whose OnModifyRecord has a side effect must not get it for a write that is not
        // happening.
        if (!RowValuesChangedSinceLoad()) return;

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

        // The write has landed, so the row IS the before-image from here on. BC gets this from
        // the client: SaveRecordAsync ends by raising UpdateRequest(RecordSaved), the client
        // re-reads, and AfterGetCurrRecordAsync's tail assigns OldRecord. This method is the
        // runner's own write path and never reaches SaveRecordAsync, so it takes the snapshot
        // itself — see RunnerPageInstance.RefreshBeforeImageAfterSave for the other half, which
        // covers CurrPage.SaveRecord()/Update(true). Without both, a second write in one page
        // session reported the value from before the FIRST write as its xRec (issue #3440), and
        // the RowValuesChangedSinceLoad gate above measured that same stale row.
        SnapshotBeforeImage();
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

        // Two ways BC refuses a close, and they are not the same question — see
        // RunnerPageInstance.CloseRefusal.
        if (_page != null && !_page.RaiseOnClosePage(_formResult, out var refusal))
        {
            // An AL error the trigger raised, consumed by a declared [MessageHandler]. MEASURED
            // on a real service tier (corpus codeunit 60602 "QCM Query Close Msg Tests",
            // StefanMaron/BusinessCentral.AL.Language.Tests#272, green on all eight cloud legs
            // and on the Windows nightly): Close() returns normally, the message reaches the
            // handler exactly once on this route, and the page is left OPEN.
            //
            // Returning here is the whole of that: the tear-down below is skipped, so _opened
            // stays true, no row is flushed by the close, and BC's own form state is untouched
            // — the test's TestPage variable keeps working, which is what a real tier leaves it
            // holding. It is deliberately NOT a refusal any more; raising one here would be the
            // runner erroring on a path BC completes without an error (issue #3179).
            if (refusal == RunnerPageInstance.CloseRefusal.ErrorShownAsMessage) return;

            // A plain veto (the trigger returned false) on the EXPLICIT TestPage.Close() path.
            // Still a refusal, and still a permanent scope boundary (#2999 lists it among the
            // fourteen): BC leaves the page open awaiting a user, and unlike the arm above no
            // service tier has been asked what a test observes afterwards. A [TryFunction]
            // reading false is BC's outcome.
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                // No " — " in the api — see RequireRecord.
                $"TestPage page {_pageId} (OnQueryClosePage)",
                "testpage-close-veto — the page's OnQueryClosePage returned false, which in BC "
                + "leaves the page open awaiting the user. See docs/scope.md");
        }
        // The EXPLICIT close route's flush. The modal route does not come through here at all
        // -- it reaches Dispose() instead (see there) -- and the two ROUTES nevertheless agree
        // about an uncommitted subpage-part row, because both end in a flush. That agreement is
        // real BC's, not a runner convention: corpus codeunit 60420 "TPMF Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests#311, merged 22e226c4) drives one page
        // through both routes and both persist the row, green on all eight cloud legs of run
        // 34345468218. So neither call site may lose its flush; issue #3682.
        FlushParts(); FlushRow(); _opened = false;

        // The triggers above are this page's close, so BC's own form state has to agree that
        // it happened — otherwise IsOpen stays true and whoever else is holding the form runs
        // the close a second time. ForceClose raises nothing, which is exactly right here:
        // the triggers have already run once (issue #3091).
        _page?.ForceCloseForm();
    }
    // The MODAL route's flush, and the only one it has. A [ModalPageHandler] never calls
    // Close(): BC wraps the handler in a scope and disposes the page handle as the refcount
    // drops, which lands here -- NavTestExecution.TestHandleModalForm -> NavTestPageHandle
    // .Dispose -> TreeObjectReferenceHandler.Dispose -> NavTestPage.Dispose -> this.
    //
    // Equivalent to what BC does, and measured rather than assumed: corpus codeunit 60420
    // "TPMF Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#311, merged 22e226c4) asserts
    // the two close routes AGREE about an uncommitted part row, green on all eight cloud legs
    // of run 34345468218. Its arms F/G/H invoke no action precisely so the ACTION write-back
    // (OK().Invoke() -> SaveCurrentRow()) cannot stand in for the close.
    //
    // The trap, and why TestPageModalClosePartFlushTests exists: these two calls are REDUNDANT
    // for a part row here -- FlushParts() reaches each part's FlushRow(), and each part page's
    // own Dispose() calls its own FlushRow() -- so removing EITHER one alone leaves every
    // behavioural test green. Only removing both goes red. The IL guard in that file is what
    // fails on a single-call edit. Issue #3682.
    public override void Dispose() { FlushParts(); FlushRow(); }

    /// <summary>
    /// The close attempt a built-in OK/LookupOK invoked from a <c>[ModalPageHandler]</c> /
    /// <c>[PageHandler]</c> makes, which the runner did not make at all before #3593.
    ///
    /// <para>On BC the handler's <c>OK().Invoke()</c> is a CLIENT action: pressing OK drives the
    /// logical form's close, so <c>OnQueryClosePage</c> is raised right there, before the round
    /// trip that opened the page gets control back. The runner's <c>Invoke()</c> only recorded a
    /// result, so the ONLY close attempt on this route was the one
    /// <see cref="AlRunner.Patches.RunnerModalDispatch.FormRunModal"/> makes afterwards.</para>
    ///
    /// <para>The observable consequence, and the reason this is a defect rather than an internal
    /// detail: a close BC REFUSES is attempted twice, so an <c>OnQueryClosePage</c> that raises
    /// an AL error consumed by a <c>[MessageHandler]</c> delivers that message TWICE on the
    /// RunModal route. Measured on a real service tier by corpus codeunit 60602 "QCM Query Close
    /// Msg Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#272, merged bd168356), green on
    /// all eight cloud legs and confirmed by the Windows nightly reference tier, whose modal arms
    /// assert a delivery count of 2 while its TestPage arm asserts 1.</para>
    ///
    /// <para>ONE mechanism produces both counts, which is why this is not a counter. A successful
    /// attempt here CLOSES the form, so <c>FormRunModal</c>'s own <c>IsFormOpen</c> gate skips its
    /// attempt and the trigger is raised exactly once -- the result corpus codeunit 60276 "MQC
    /// Tests" measured for an allowed close, and the one the runner already matched. A REFUSED
    /// attempt leaves the form open, so that gate lets the second attempt through and the message
    /// is delivered twice. Delivering twice unconditionally would break the allowed-close case.</para>
    ///
    /// <para>Restricted to a page the TEST DID NOT OPEN (<c>!_opened</c>, written only by
    /// <see cref="MarkOpened"/>). A page the test opened itself is the test's to close: BC's
    /// client does not press its OK button, and <c>Card.OpenNew(); Card.OK().Invoke();</c>
    /// followed by further calls on the same variable is ordinary AL that must keep working.
    /// That route's close attempt is <see cref="Close"/>, which is unchanged.</para>
    ///
    /// <para>A refusal is SWALLOWED here rather than raised, and that is the faithful answer, not
    /// a convenience: BC's own close handler returns "close refused" to the client without
    /// raising anything the handler can see (the message has already been shown), and the handler
    /// carries on to its own end. What the caller of <c>RunModal()</c> observes is then decided by
    /// <c>FormRunModal</c>'s second attempt, which reaches the same refusal and drops the
    /// handler's result -- so <c>Action::None</c> still comes out of the refused path, unchanged.
    /// The one thing that must NOT be swallowed is a refusal whose message had nowhere to go:
    /// with no <c>[MessageHandler]</c> declared, BC's own "Unhandled UI: Message …" comes out of
    /// <see cref="AlRunner.Patches.RunnerFormCloseHandler"/> rather than being returned, and that
    /// is a real test failure which propagates.</para>
    /// </summary>
    private void AttemptHandlerDrivenClose(FormResult result)
    {
        // The test opened this page itself, so closing it is the test's call, not the client's.
        if (_opened) return;

        // Nothing to raise a trigger on, or AL already closed the page from under the handler
        // (CurrPage.Close() from an OnAction) -- in which case the close has happened and its
        // triggers have run exactly once already (issue #3091).
        if (_page == null || _tornDown || RunnerPageInstance.WasClosedFromAl(_page.Form)) return;

        // Only the CONFIRMING built-ins close the page on BC. A Cancel that reached here would
        // be a second question -- what a cancelled modal's close attempt does -- which no tier
        // has been asked, so it keeps the behaviour it had.
        if (result is not (FormResult.OK or FormResult.LookupOK)) return;

        // BC's client sends the row being edited -- INCLUDING a row typed into a part -- before
        // it drives the close, so OnQueryClosePage reads a part that already holds it. Invoke()
        // above flushes only this page's own row, so without this the trigger sees a part one
        // row short and a page that materialises its part contents on OK saves nothing (#3701).
        // Measured on a real service tier: corpus codeunit 60663 "Opf Ok Part Flush Tests"
        // (StefanMaron/BusinessCentral.AL.Language.Tests#315).
        //
        // Order here is row THEN parts: Invoke() above has already flushed this page's own row.
        // Close()/Dispose()/SaveCurrentRow() are parts-then-row because a part's OnValidate can
        // touch the header. The repeat pass is a no-op -- FlushPendingNewRow/FlushPendingModify
        // clear their flag on entry -- so nothing is written twice. Do not delete Invoke()'s
        // FlushRow() on the strength of this call: FlushParts() does not write the host row, and
        // no test here would catch its loss.
        FlushParts();

        // Both refusals leave the form OPEN and raise nothing here, which is what makes
        // FormRunModal's own attempt run -- and that second attempt is where the second message
        // delivery, and the Action::None, come from. They are one branch on purpose: unlike
        // Close(), which must tell them apart because a veto there is a scope boundary
        // (testpage-close-veto), this route's observable outcome is produced downstream either
        // way, and it is the outcome the route already had before #3593.
        if (!_page.RaiseOnClosePage(result, out _)) return;

        // The close succeeded, so BC's own form state has to agree -- otherwise IsOpen stays
        // true and FormRunModal runs the whole sequence a second time, which is exactly the
        // double-raise issue #3091 fixed. ForceClose raises nothing: the triggers have just run.
        _opened = false;
        _page.ForceCloseForm();
    }

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
                        MarkEdited, PromoteNewRowLineForWrite, _validationErrors);
            return field;
        }

        // Otherwise it may be bound to a page VARIABLE — resolvable only through the page's
        // own binding table (NavForm.SourceExpressions).
        var expression = _page?.TryGetSourceExpression(id);
        if (expression != null)
        {
            if (!_pageVariableFields.TryGetValue(id, out var pageField))
                _pageVariableFields[id] = pageField =
                    new PageVariableTestField(_page!, expression, id, _validationErrors);
            return pageField;
        }

        // Neither — and from here there are TWO different answers, which this site used to
        // collapse into one runner-gap refusal (issue #3313).
        //
        // If the page declares no control with this id AT ALL, the id is not in the page's
        // control-id space and BC refuses it BY DESIGN. NavTestPageBase.GetField(int,bool) —
        // the precompiled BC method the AL compiler emits for TestPage.GetField(Id) — is
        // `fields.TryGetValue(id, ...)` over the page's own control dictionary, falling back
        // to ITestPage.GetField(id), and raises its own NavTestFieldNotFoundException ("The
        // field with ID = N is not found on the page.") when that answers null. Returning
        // null hands BC's method exactly the input it is written to refuse, so AL sees BC's
        // own exception rather than one the runner invented.
        //
        // The reachable case in ordinary AL is confusing GetField's id space with the source
        // table's: `Host.Lines.GetField(Rec.FieldNo(Descr))`. Table field numbers are keys in
        // neither dictionary. Answering a field for one was a SILENT WRONG ANSWER in the sense
        // .claude/rules/loud-failures.md names — AL got a handle it could never have obtained
        // on a real tier, and nothing looked broken until the same test ran against one.
        //
        // Measured on a real service tier by corpus codeunit 60346
        // (StefanMaron/BusinessCentral.AL.Language.Tests#227, all 8 cloud legs): the suite's
        // first revision passed table field 3 and every leg answered "The field with ID = 3
        // is not found on the page." The corpus test asserts alongside it that 3 really IS a
        // valid table field number on that part's source table, so the refusal it pins is
        // about the ID SPACE and not about a meaningless argument.
        //
        // If the page DOES declare the control and the runner merely could not resolve its
        // binding, that is a genuine runner gap and keeps the named refusal below — this
        // narrows what gets reported as unimplemented, it does not widen it. Only a live page
        // object can answer the declaration question, so a page the runner built no metadata
        // for keeps the gap refusal too, which is the honest answer there: the runner does not
        // know whether BC would have found the control.
        if (_page?.DeclaresControl(id) == false) return null!;

        // Historically `id` was handed to the record as a FIELD NUMBER, which produced "The
        // supplied field number '<hash>' cannot be found in the '<table>' table" — a
        // control-name hash reported as a missing field, blaming the table for the runner's
        // own inability to resolve the control. Say what actually happened.
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
        FlushParts(); FlushRow();

        // Whether the cursor was ALREADY on the draft line, read before LeaveNewRowLine clears
        // it. A First() over a rowset that is still empty does not move anywhere: the draft line
        // was the only row before the call and is the only row after it, so the row it stands
        // for is the same row and must not be started a second time (#3029). Without this, the
        // open-time reload entered the draft line and the test's own First() entered it again,
        // raising the page's OnNewRecord twice before anything was typed.
        var wasOnNewRowLine = _onNewRowLine;

        LeaveNewRowLine();
        var found = _page?.RaiseOnFindRecord("-")
                    ?? record.ALFindFirstAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (!found)
        {
            // Same draft line as before the call: restore the latch LeaveNewRowLine just
            // cleared, so EnterNewRowLine takes its already-started branch. A First() that DID
            // move — from a data row, or onto one — leaves it clear and the next draft line
            // gets its own new-record step, which is what keeps this from becoming
            // "once per page".
            if (wasOnNewRowLine) _newRowLineRecordStarted = true;
            EnterNewRowLine(record);
        }
        return Loaded(found);
    }

    /// <summary>
    /// Go to the last row of the page's rowset.
    ///
    /// The empty case falls onto the implicit new-row line exactly as <see cref="MoveFirst"/>
    /// does, and for the same reason: on an editable, insert-allowed page a client that finds
    /// no matching row still renders one row — the blank line — and a subsequent write has to
    /// have somewhere to land. Without it, `Last()` on such a page left the cursor on nothing
    /// and `TP.SomeField.SetValue('X')` afterwards wrote into a record nothing had positioned
    /// (issue #2964; the same gap #2392 fixed for First() and #2923 for a linked part).
    ///
    /// The RETURN VALUE stays false, so this changes internal cursor state only, never what
    /// Last() answers. That matters for the one internal caller,
    /// FindRowFromTableFieldValues's backward scan, which starts at MoveLast() and enters its
    /// `while (hasRow)` loop only on true — the identical guarantee the MoveFirst() arm of
    /// that same line already relies on.
    ///
    /// WHERE Last() LANDS on a page that DOES have rows is the last DATA row, not the blank
    /// line past it — so this is a fallback for the empty case only, never a step onto the
    /// draft line from a rowset that has data. Both halves are measured on a real service
    /// tier, corpus codeunit 60757 "Test Page Last New Row Line"
    /// (StefanMaron/BusinessCentral.AL.Language.Tests#231), over the same fixture family
    /// codeunit 60743 uses:
    ///
    ///   * EditableInsertableList_Last_LandsOnTheLastDataRow — three seeded rows, Last() reads
    ///     'CHARLIE', so "last data row" and "new-row line" are two distinct positions no
    ///     off-by-one can conflate;
    ///   * EditableInsertableList_NextAfterLast_ReachesTheNewRowLine — the blank line is still
    ///     there, one Next() past where Last() stopped;
    ///   * EmptyEditableList_LastReturnsFalse — asserted in the same procedure as First(), so
    ///     an implementation aliasing the two cannot satisfy both;
    ///   * EmptyEditableList_SetValueAfterLast_InsertsARow and its linked-part twin
    ///     ModalHostPart_EmptyPart_SetValueAfterLast_InsertsARow — the arms this fallback
    ///     exists for, and the only two of the twelve that failed before it.
    /// </summary>
    public override bool MoveLast()
    {
        var record = RequireRecord("MoveLast()");
        FlushParts(); FlushRow(); LeaveNewRowLine();
        var found = _page?.RaiseOnFindRecord("+")
                    ?? record.ALFindLastAsync(DataError.TrapError).GetAwaiter().GetResult();
        if (!found) EnterNewRowLine(record);
        return Loaded(found);
    }

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

        if (StepRow(record, 1) != 0) return Loaded(true);
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

        return Loaded(StepRow(record, -1) != 0);
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
        return Loaded(StepRow(record, 1) != 0);
    }

    /// <summary>
    /// Move one row along the rowset THE PAGE presents: its own OnNextRecord when it declares
    /// one, and the platform step otherwise (issue #3439).
    ///
    /// A page that serves its rows from somewhere other than its SourceTable — a temporary
    /// buffer above all — answers here with rows the record has never held, so stepping the
    /// record directly walks a different set from the one the page shows.
    /// </summary>
    private int StepRow(NavRecord record, int steps)
        => _page?.RaiseOnNextRecord(steps)
           ?? record.ALNextAsync(steps).GetAwaiter().GetResult();

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

    // ONE NEW-RECORD STEP PER DRAFT-LINE ROW (issue #3029). Set the moment the platform's
    // new-record step has run for the draft line the cursor is on, and cleared whenever that
    // line stops being the current one.
    //
    // The invariant it holds is that starting a record is a ONE-TIME event for a row, while
    // the two things that reach it are not: EnterNewRowLine is re-entered by page plumbing
    // that made no cursor move the test asked for, and PromoteNewRowLineForWrite runs on a
    // line EnterNewRowLine has already started. Both used to raise OnNewRecord unconditionally,
    // so one draft-line row cost FIVE firings where BC charges one — measured, see the PR body.
    //
    // A latch and not a counter: the question at both call sites is "has this row been started
    // already", which is a boolean. A count would also have to be reset on exactly the same
    // events, and would invite reading it as "how many times BC would have fired", which is not
    // what it would hold.
    private bool _newRowLineRecordStarted;

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
        //
        // ONCE PER ROW (#3029). _newRowLineRecordStarted is what stops a re-entry from raising
        // OnNewRecord a second time for the SAME draft line. It has to be checked HERE, around
        // the new-record step, rather than at the top of the method: the caller's other work is
        // still owed on a re-entry — the return position and the insert position are re-read
        // above because the parent row may have moved under the part, and _onNewRowLine must
        // end up set whichever branch ran. Guarding the whole method would have been the naive
        // placement and is wrong for exactly that reason; it is mutation-tested in the PR body.
        if (_newRowLineRecordStarted)
        {
            // The buffer is already the started row's. Re-blanking it would discard whatever
            // the page's own OnNewRecord put there, which is the damage this guard exists to
            // avoid as much as the duplicate firing is.
            _onNewRowLine = true;
            return true;
        }

        _newRowLineRecordStarted = true;

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
        // Stepping off the draft line ends that row (#3029) — see AbandonNewRowLine.
        _newRowLineRecordStarted = false;
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
        // The row this draft line stood for is gone, so the NEXT draft line is a new row and
        // owes its own new-record step (#3029). Clearing here rather than only in Reset is what
        // keeps the latch from turning "once per row" into "once per page".
        //
        // Guarded by DraftLineAbandonedByAParentMove_MakesTheNextRowOweItsOwnFiring, and by
        // that arm alone: every other arm stays within ONE parent row, so all of them pass with
        // this reset removed. Review established that by removing it — the fixture and all four
        // corpus arms stayed green. Only moving the parent between two draft lines separates
        // "once per row" from "once per page".
        _newRowLineRecordStarted = false;
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
    /// are exactly the pair of triggers RaiseOnAfterGetRecord above fires, which is why a row
    /// becoming the current row is one of the moments BC takes it.
    ///
    /// <para>It is not the only one, and this method now has FOUR callers — issue #3440. BC also
    /// retakes the before-image after every successful page-driven WRITE, so a second write in
    /// one page session sees the first write's row as its xRec: <c>NavForm.InsertAsync</c> does
    /// it inline (mirrored in <see cref="FlushPendingNewRow"/>), and <c>SaveRecordAsync</c>
    /// leaves it to the client, which re-reads and lands in <c>AfterGetCurrRecordAsync</c>'s own
    /// tail — mirrored in <see cref="FlushPendingModify"/> for the runner's own write path and in
    /// <c>RunnerPageInstance.RefreshBeforeImageAfterSave</c> for <c>CurrPage.SaveRecord()</c> /
    /// <c>Update(true)</c>. Removing any one of the four puts the stale before-image back on
    /// that path. What still holds is the OTHER half of the old sentence: nothing overwrites it
    /// BETWEEN the write's start and its trigger, so OnModify sees the row as fetched.</para>
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

    /// <summary>
    /// The only ITestPage entry point that genuinely receives a CONTROL id — and, unlike
    /// <see cref="FindRowFromTableFieldValues"/>, the one whose caller has ALREADY positioned
    /// the cursor where the search must begin. That is why it does not simply forward.
    ///
    /// <para>BC's <c>NavTestPageBase.InternalFindRowFromControlFieldValue</c> drives all three
    /// of FindFirstField/FindNextField/FindPreviousField, and it makes the initial move
    /// itself before calling in here:</para>
    /// <code>
    /// switch (initialMove) {
    ///   case InitialMove.First:    TestPage.MoveFirst(); break;
    ///   case InitialMove.Next:     if (!TestPage.MoveNext())     return false; break;
    ///   case InitialMove.Previous: if (!TestPage.MovePrevious()) return false; break;
    /// }
    /// return TestPage.FindRowFromControlFieldValue(fieldNo, value, initialMove != InitialMove.Previous);
    /// </code>
    /// <para>So the position on entry IS the argument: for FindNextField it is one row past
    /// the last match, for FindPreviousField one row before it. Re-seeking to the first (or
    /// last) row here discards it, and both members then answer the row FindFirstField
    /// already returned — FindNextField never advances and FindPreviousField never goes back
    /// (issue #3312).</para>
    ///
    /// <para>The sibling path is genuinely different and stays as it was:
    /// <c>InternalFindRowFromTableFieldValues</c> — which is what GoToKey and GoToRecord
    /// reach — calls <c>TestPage.MoveFirst()</c> unconditionally before its own
    /// <c>FindRowFromTableFieldValues</c>, so for THAT caller "scan the whole rowset" and
    /// "resume from the cursor" are the same answer. Verified against
    /// Microsoft.Dynamics.Nav.Ncl.dll.</para>
    /// </summary>
    public override bool FindRowFromControlFieldValue(int controlId, object value, bool forward)
        => FindRowFromFieldValues(new[] { ControlIdToTableFieldNo(controlId) }, new[] { value }, forward,
            startFromCurrentRow: true);

    public override bool FindRowFromTableFieldValues(int[] fieldNos, object[] values, bool forward)
        => FindRowFromFieldValues(fieldNos, values, forward, startFromCurrentRow: false);

    private bool FindRowFromFieldValues(int[] fieldNos, object[] values, bool forward, bool startFromCurrentRow)
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

        // Where the scan STARTS is the caller's decision, not the direction's.
        //
        // startFromCurrentRow: false (FindRowFromTableFieldValues — GoToKey, GoToRecord) scans
        // the WHOLE rowset from the first (or last, when searching backward) row, never from
        // wherever the page happens to be positioned. `forward` is then a direction, not
        // "resume from the cursor": BC's client locates the requested row anywhere in the
        // rowset. Starting at the current row silently failed to find any row BEHIND the
        // cursor, so navigating C -> A returned false even though A is on the page
        // (tests/runner-extras/testpage-gotorecord GoToRecord_MovesBetweenRows). BC agrees
        // for this caller by construction: InternalFindRowFromTableFieldValues calls
        // TestPage.MoveFirst() itself before reaching here.
        //
        // startFromCurrentRow: true (FindRowFromControlFieldValue — FindFirstField and
        // friends) resumes from the cursor, because BC's InternalFindRowFromControlFieldValue
        // already made the MoveNext()/MovePrevious() that says where to begin. See that
        // method's own doc comment above for the decompiled shape (issue #3312).
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
            // startFromCurrentRow: the caller positioned the cursor and that position is the
            // search's starting point (see FindRowFromControlFieldValue). "Current row" means
            // the row the record is actually standing on — an unpositioned record has no such
            // row, so it falls back to the end the direction starts from, which is also what
            // BC's InitialMove.First arm produces after its MoveFirst().
            var hasRow = startFromCurrentRow && hasCurrent
                ? true
                : forward ? MoveFirst() : MoveLast();

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
    private void RepositionAfterFilterChange()
    {
        // A FILTER CHANGE ENDS THE DRAFT LINE'S ROW (#3029). The blank line a page shows past
        // its data stands for a row IN the current rowset — its key fields are filled from that
        // rowset's own single-valued filters — so once the filter moves it stands for a
        // different row and owes a fresh new-record step.
        //
        // Without this, corpus codeunit 60710's OpenEdit -> SetFilter -> New() sequence took
        // MoveFirst's same-row branch: the page had parked on a draft line for the UNfiltered
        // rowset while opening, the filter then selected P2, and New() reused the row started
        // before anyone had said P2 — so the new row carried a blank ParentCode instead of the
        // filter's value. Three tests, and they are the reason this clears rather than the
        // reasoning above.
        AbandonNewRowLine();
        MoveFirst();
    }

    public override string GetFilter(int fieldNo)
        => RequireRecord("GetFilter()").ALGetFilter(fieldNo);

    // ── ITestFilter: the key and the direction the page walks (#3316) ─────────────
    //
    // The same argument SetFilter above makes. A page's key and sort direction are properties
    // of the rowset, so they belong on the NavRecord the page walks — every navigation member
    // of this class goes through ALFindFirstAsync/ALNextAsync on that record, and those read
    // the record's current key and ascending flag. Held in fields on this object instead (what
    // MockITestPage does, and what this class inherited until now) they were a write-only
    // store: SetCurrentKey and Ascending were recorded and reported back, and the page went on
    // walking its primary key ascending regardless.
    //
    // Delegating also fixes CurrentKey's rendering for free rather than by a second mechanism.
    // NavRecord.ALCurrentKey resolves the key against the table's own metadata and names its
    // fields, which is what corpus codeunit 60398's Record-side assertions already pin
    // ('CurrentKey() must include primary key field Entry No.'); the field-number join this
    // class used to inherit is what produced the observed '3' and '2, 3'.

    /// <summary>
    /// Install the key the page walks. Delegates to the record, so it changes the ORDER the
    /// page walks and not merely what <see cref="CurrentKey"/> reports.
    ///
    /// <para>An empty or null field list leaves the record's key alone: BC's own
    /// NavRecord.ALSetCurrentKey builds an NCLMetaField per field and asks the record
    /// implementation to select a key from them, and a zero-length list names no key. AL
    /// cannot produce that call anyway — <c>SetCurrentKey()</c> with no argument is
    /// error AL0135 — so this only guards the interface, which is not AL-constrained.</para>
    /// </summary>
    public override void SetCurrentKeyFields(int[] fields)
    {
        if (fields == null || fields.Length == 0) return;
        RequireRecord("SetCurrentKey()").ALSetCurrentKey(fields);
        // A key change reorders the rowset, so the cursor's position within it is no longer
        // meaningful — the same reason SetFilter repositions. BC's client reopens the rowset
        // on the new key and lands on its first row, which is what the corpus asserts by
        // walking from First() after SetCurrentKey.
        RepositionAfterFilterChange();
    }

    /// <summary>
    /// The field numbers of the key the record is currently walking. Answered from the
    /// record's own current key rather than from a remembered argument list, so a page whose
    /// key was never set through this interface still reports the key it is actually on.
    /// </summary>
    public override int[] GetCurrentKeyFields()
        => _record == null ? Array.Empty<int>() : TestFilterKeyFields.Of(_record);

    /// <summary>
    /// The direction the page walks its current key. Read and written on the record, so
    /// <c>Ascending(false)</c> reverses the walk instead of only being reported back.
    /// </summary>
    public override bool Ascending
    {
        get => _record == null || _record.ALAscending;
        set
        {
            RequireRecord("Ascending()").ALAscending = value;
            // Reversing the order moves the first row, so the cursor is repositioned for the
            // same reason a key change repositions it.
            RepositionAfterFilterChange();
        }
    }

    /// <summary>
    /// The current key rendered the way BC renders it — naming the key's fields. Straight
    /// through to NavRecord.ALCurrentKey, which is what AL's own <c>Record.CurrentKey()</c>
    /// reads, so the page and the record can never disagree about the key the page is on.
    /// </summary>
    public override string CurrentKey => _record == null ? string.Empty : _record.ALCurrentKey;

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
