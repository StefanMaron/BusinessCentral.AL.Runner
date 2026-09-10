// LiveNavTestPage: actions — the page's own actions, the built-in OK/Cancel/Yes/No
// affordances, and the Edit/View page-mode switch.
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
internal partial class LiveNavTestPage
{
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
            //
            // NO AL CAN REACH THIS TODAY (#3735), and it stays as the guard that keeps it that
            // way. ALL THREE routes that construct a LiveNavTestPage are covered, but not by
            // one argument — the third rests on a weaker invariant, which is the thing a later
            // editor would get wrong:
            //
            //   1-2. CodeunitPatches.CreateTestPageClient, via TestPageClientConstructionRule.
            //        LiveOverRecord needs a source table, which TestPageFactory.TryBuild
            //        resolves through the same two lookups TryGetAnyPageType reads;
            //        LiveRecordless needs IsPageShapeKnown outright. A page in neither gets
            //        MockITestPage, whose View()/Edit() never enter this method, and
            //        CreateTestPageClient's `[warn] … navigation mock` line says so.
            //   3.   RunnerTestClientSession.GetPage — the [PageHandler]/[ModalPageHandler]
            //        route — applies NO shape gate at all. Its only gate is form construction
            //        (FindFormType, CodeunitPatches), a CLR-type inventory that is NOT
            //        contained in the symbol inventory by construction: the symbol side is
            //        per-bundle (ResetForReload clears _sourceDirs and _parsedPages;
            //        ClearPerBundleBcAppPaths drops _bcAppPaths) while loaded assemblies are
            //        process-wide and BcRuntime.IsStaleBundleAssembly excludes only superseded
            //        generations. What keeps it unreachable is one step earlier: BC picks the
            //        handler from its `TestPage "X"` parameter type, so the page must resolve
            //        in THIS bundle's compile, and every compile symbol source is also a
            //        registration source (Program.cs — one `ordered` list feeds both
            //        DependencyLoader.LoadAll and AddBcAppPath; the layered-workspace packages
            //        SetExtraSymbolDirs adds are resolved as declared dependencies; the
            //        registered source dirs mirror the compile's CollectSuitePaths). That last
            //        one is MAINTAINED, not structural, and #3611/#3714 are the record of the
            //        two sets having drifted apart before — so widening what the compiler can
            //        see without widening what RecordPatches registers makes this reachable.
            //
            // What would widen the inventory is a runtime-package metadata reader (#3537); BC's
            // captured emitter metadata cannot, because it exists only for objects this run
            // compiles. Pinned by AlRunner.Tests/LiveTestPagePageTypeKnownTests.cs.
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
}
