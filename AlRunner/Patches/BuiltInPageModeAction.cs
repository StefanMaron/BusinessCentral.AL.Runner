// BuiltInPageModeAction — AL's `SomePage.View()` and `SomePage.Edit()` on a TestPage.
//
// THE GAP (issue #3185)
//   Both raised "InvalidOperationException: The UISessionManager was expected to be
//   initialized." from TestPageClientSession.GetTestLogicalDispatcher(), before any page could
//   open. Two independent causes, and the first one hid the second:
//
//   1. NavTestPageBase.ALView()/ALEdit() wrap their result in TestClientProxy<ITestAction>
//      .Proxy(...), which needs the client's dispatcher. NclCecilRewrite step 4 strips that
//      call from NavTestPageBase — but it used to strip it from a hard-coded list of six
//      method names, and NavTestPageBase has EIGHT Proxy call sites. ALView and ALEdit were
//      the two the list did not name. That step now sweeps the whole type.
//
//   2. Underneath it, ITestPage.View()/Edit() answered `new MockITestAction()`, whose Invoke()
//      is a literal no-op. So even with the proxy gone, invoking either did nothing at all.
//      This file is that half.
//
// WHAT REAL BC DOES, AND HOW THAT WAS ESTABLISHED
//   Nothing in the application declares these actions; the CLIENT supplies them. The
//   reference implementation is Microsoft.Dynamics.Nav.Client.TestPageClient.TestPageProxy
//   (BC 28.1), and it is a lookup, not an effect:
//
//     public ITestAction View()  => the first ActionControl whose Action is a
//         NavOpenTaskPageAction { IsPageModeAction: not false } with ViewMode == PageMode.View,
//         wrapped in a TestActionProxy — or NULL when the page has none.
//
//   Edit() is the same with PageMode.Edit. Those actions are created by
//   Microsoft.Dynamics.Nav.Client.FormBuilder.ActionBuilder, from MenuActionType.View /
//   MenuActionType.Edit, and everything this file needs is in three of its methods:
//
//     * ResolveCardFormId — the TARGET. For a system menu action `actionDef.TargetID` is 0, so
//       it falls back to the card page id in the builder context (a list page's CardPageId),
//       then FormState.CardPageId, and only then — when the parent form is NOT a List — to the
//       parent page's OWN id.
//     * IsModifyAllowedInCard — whether the card allows modification. BC does NOT drop the
//       Edit action when it does not: the action is still there and still Visible, and BC
//       expresses "you cannot use it here" through Enabled instead (corpus 60479
//       ListWhoseCardIsReadOnlyLeavesTheEditActionVisibleButNotEnabled). This file said the
//       opposite until #3258, and the runner refused that shape on the strength of it.
//     * NavOpenTaskPageAction.FindFormState / CreateForm — the ROW and the MODE. `new
//       FormState(ViewMode)` carries the requested mode onto the target, and the parent
//       binding manager's CurrentRow bookmark is stamped onto it, so the card opens on the
//       row the list is standing on.
//
//   And the service tier has adjudicated the result twice. Corpus codeunit 60461 "TPVE Tests"
//   (StefanMaron/BusinessCentral.AL.Language.Tests#203) drives a List with CardPageId, parks it
//   on its second row, invokes each action, and asserts that the card opens exactly once, on
//   that row, that the [PageHandler] ran, and that View gives the handler a read-only page
//   while Edit gives it an editable one. Corpus codeunit 60479 "TPMS Tests" (upstream #317,
//   9/9 on BC 28.4.53241.0) adds every shape where NO card opens:
//
//     shape                                   | Visible | Enabled | Invoke
//     ----------------------------------------|---------|---------|---------------------------
//     Card opened read-only, Edit()            | true    | true    | the page becomes editable
//     Card opened editable, View()             | true    | true    | the page becomes read-only
//     Card already in the requested mode       | true    | FALSE   | nothing at all
//     List with no CardPageId, View()          | true    | true    | nothing opens
//     List with no CardPageId, Edit()          | true    | FALSE   | nothing opens
//     List whose card is read-only, Edit()     | true    | FALSE   | nothing opens
//     List whose card is read-only, View()     | true    | true    | that card opens, read-only
//     List whose card is editable, both        | true    | true    | that card opens (60461)
//
//   Visible is true in every single row. Enabled is where BC says "not here" — which is the
//   correction #3258 was filed to get: both properties used to answer a hardcoded true, and
//   the two no-card shapes were refused outright.
//
// WHAT THIS FILE IMPLEMENTS
//   All of the above. The in-place switch moves LiveNavTestPage's own editability
//   (SwitchViewModeInPlace) rather than driving BC's PageModeAggregator.ChangePageMode: that
//   type lives in Microsoft.Dynamics.Nav.Client.UI.dll and operates on a client LogicalForm,
//   which the runner never builds — its whole action layer is its own.
//
// WHAT IS STILL REFUSED
//   A page declaring Editable = false, asked for Edit(). BC has no such action and raises a
//   bare NullReferenceException out of NavTestAction; the runner refuses by name instead. See
//   LiveNavTestPage.BuiltInPageModeActionFor.
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

/// <summary>
/// The open mode a built-in page-mode action asked for, handed to the page BC is about to
/// open. BC carries it as <c>FormState(ViewMode)</c> into the builder; the runner has no
/// builder, and <c>NavForm.RunAsync</c> hands back no form to configure, so it is parked here
/// for the dispatch that DOES see the form — <see cref="RunnerModalDispatch"/>, immediately
/// before it raises OnOpenPage.
///
/// <para>Read ONCE, and only by the page id it was armed for. A page opened from inside the
/// target's own OnOpenPage is a different open and must not inherit this one's mode; consuming
/// on the first matching read is what keeps that true without tracking a stack.</para>
/// </summary>
internal static class RunnerPendingPageOpenMode
{
    [ThreadStatic] private static int _pageId;
    [ThreadStatic] private static bool _readOnly;

    /// <summary>Arm the mode for the next open of <paramref name="pageId"/> on this thread.</summary>
    internal static void Arm(int pageId, bool readOnly)
    {
        _pageId = pageId;
        _readOnly = readOnly;
    }

    /// <summary>Drop an armed mode that was never consumed — the page refused to open, or BC
    /// answered the run some other way. Without this a later, unrelated open of the same page
    /// on this thread would pick it up.</summary>
    internal static void Disarm() => _pageId = 0;

    /// <summary>
    /// The mode armed for <paramref name="pageId"/>, consumed. False when nothing was armed
    /// for that page, which is the normal case for every other page open in the run.
    /// </summary>
    internal static bool TryConsume(int pageId, out bool readOnly)
    {
        readOnly = false;
        if (pageId == 0 || _pageId != pageId) return false;
        readOnly = _readOnly;
        _pageId = 0;
        return true;
    }
}

/// <summary>Which of BC's three shapes a built-in page-mode action is. See this file's header.</summary>
internal enum BuiltInPageModeActionKind
{
    /// <summary>Opens the host's CardPageId card (corpus 60461).</summary>
    OpenCard,

    /// <summary>A list with no card to open: the action exists and does nothing.</summary>
    NoTarget,

    /// <summary>A non-list host: the page already open changes mode.</summary>
    InPlaceSwitch,
}

/// <summary>
/// A page's built-in View / Edit action. See this file's header for what BC's own client does
/// and where each row of the table is measured.
/// </summary>
internal sealed class BuiltInPageModeAction : ITestAction
{
    private readonly LiveNavTestPage _host;
    private readonly NavRecord? _record;
    private readonly int _targetPageId;
    private readonly bool _viewMode;
    private readonly BuiltInPageModeActionKind _kind;

    internal BuiltInPageModeAction(
        LiveNavTestPage host, NavRecord? record, int targetPageId, bool viewMode,
        BuiltInPageModeActionKind kind)
    {
        _host = host;
        _record = record;
        _targetPageId = targetPageId;
        _viewMode = viewMode;
        _kind = kind;
    }

    /// <summary>
    /// Save the current row, then do whatever this action's shape does. The save is BC's order
    /// too: <c>LogicalAction.RequiresSave</c> is set to true in <c>NavOpenTaskPageAction</c>'s
    /// constructor, so a real client sends the row it is standing on to the server first.
    ///
    /// <para>A DISABLED action does nothing, and does not raise. That is not the runner being
    /// permissive: it is what a real service tier does with every one of these — corpus 60479
    /// EditActionOnAnAlreadyEditableCardIsVisibleButNotEnabledAndDoesNothing, its View mirror,
    /// and ListWhoseCardIsReadOnlyLeavesTheEditActionVisibleButNotEnabled each invoke one and
    /// assert that the page's mode is unchanged and that nothing opened. Under
    /// loud-failures.md this is the observably-equivalent answer, not a swallowed failure:
    /// BC's own <c>LogicalAction.CanInvoke</c> gate produces no effect and no error.</para>
    /// </summary>
    public void Invoke()
    {
        _host.SaveCurrentRow();

        if (!Enabled) return;

        switch (_kind)
        {
            case BuiltInPageModeActionKind.InPlaceSwitch:
                // Nothing opens: the page already on screen changes mode. BC reaches this
                // through NavOpenTaskPageAction.InvokeCore's UseCurrentForm branch.
                _host.SwitchViewModeInPlace(_viewMode);
                return;

            case BuiltInPageModeActionKind.NoTarget:
                // A list with no CardPageId: BC resolves no target form and the invoke has no
                // effect (corpus 60479 PlainListWithoutCardPageIdOffersBothActionsAndEnablesOnlyView
                // asserts neither card opened).
                return;

            default:
                // The mode has to be in place BEFORE the form opens: OnOpenPage is AL that can
                // read CurrPage.Editable, and the [PageHandler] reads it through
                // TestPage.Editable().
                RunnerPendingPageOpenMode.Arm(_targetPageId, readOnly: _viewMode);
                try
                {
                    // The host's current row, which is what BC stamps onto the target's form
                    // state as `parentBindingManager.CurrentRow.Bookmark` — the runner's
                    // equivalent of a bookmark is handing BC's own page-run front door the
                    // record itself, exactly as an action's `RunPageOnRec` already does
                    // (RunnerPageInstance.ActionRunObject).
                    RunnerPageInstance.RunPageThroughBcFrontDoor(_targetPageId, _record);
                }
                finally
                {
                    RunnerPendingPageOpenMode.Disarm();
                }
                return;
        }
    }

    /// <summary>
    /// True in every shape a service tier has been asked about — see the table in this file's
    /// header, where Visible is true on all eight rows. The action's EXISTENCE is what
    /// LiveNavTestPage.BuiltInPageModeActionFor decides; anything that got this far exists.
    /// </summary>
    public bool Visible => true;

    /// <summary>
    /// BC's <c>LogicalAction.CanInvoke</c>, for the conditions that have an answer here:
    ///
    /// <list type="bullet">
    /// <item><b>In place</b> — enabled only when the requested mode differs from the page's
    /// current one, which is <c>NavOpenTaskPageAction.CanSwitchViewMode</c>: Edit applies to a
    /// page that is currently read-only, View to one that is currently editable.</item>
    /// <item><b>No target</b> — View is enabled, Edit is not. A list with no CardPageId has no
    /// editable card to reach.</item>
    /// <item><b>Open a card</b> — View always; Edit only when that card allows modification
    /// (<c>ActionBuilder.IsModifyAllowedInCard</c>). Unknown keeps the permissive answer, the
    /// rule every TryGetAny* caller uses: refusing on a lookup miss would answer from the
    /// runner's own inventory rather than from the page.</item>
    /// </list>
    ///
    /// <para>Computed per read rather than at construction, because a switch moves it: AL that
    /// reads Enabled, invokes, and reads again must see the second answer.</para>
    ///
    /// <para>CanInvoke's remaining conditions have no runner equivalent and need none — each
    /// collapses to its permissive value BY CONSTRUCTION, not by assumption.
    /// <c>IsMultipleSelectionDisabledAction</c> refuses a multi-row selection and the runner
    /// drives exactly one row; <c>FindExistingForm</c> refuses when a form for the same page is
    /// already open and RunnerTestClientSession.OpenFormsCount is 0 by design. So the three
    /// rules above are the whole gate here, and the eight measured rows agree with them.</para>
    /// </summary>
    public bool Enabled => BuiltInPageModeActionRule.Enabled(
        _kind, _viewMode, _host.StaticEditableNow,
        _kind == BuiltInPageModeActionKind.OpenCard
            ? RecordPatches.TryGetAnyPageModifyAllowed(_targetPageId)
            : null);
}
