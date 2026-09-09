// BuiltInPageModeAction — AL's `SomePage.View()` and `SomePage.Edit()` on a TestPage.
//
// THE CLAIM
//   Nothing in the application declares these actions; the CLIENT supplies them, and BC's
//   TestPageProxy.View()/Edit() is a lookup rather than an effect. Across every shape BC
//   distinguishes, Visible is TRUE — including the shapes where nothing happens. Enabled is
//   the channel BC says "not here" through. Both properties answered a hardcoded true, and the
//   two no-card shapes were refused outright, until issue #3258.
//
//   Measured, not read off the builder: corpus codeunit 60479 "TPMS Tests"
//   (StefanMaron/BusinessCentral.AL.Language.Tests#317, 10 arms on BC 28.4.53241.0) and 60461
//   "TPVE Tests" (upstream #203). Row by row the table is pinned as assertions in
//   AlRunner.Tests/BuiltInPageModeActionRuleTests.cs; the builder walk behind it
//   (ActionBuilder.ResolveCardFormId / IsModifyAllowedInCard, BC 28.1) and the #3185 history
//   are in this change's pull request.
//
// THE TRAP
//   The read-only rule reads the HOST page's own Editable, never the target card's.
//   ResolveCardFormId falls back to the parent page's OWN id only when that parent is not a
//   List — so a list whose card is read-only keeps its Edit action, Visible and not Enabled,
//   while a Card declaring Editable = false has no Edit action at all.
//
//   The in-place switch moves LiveNavTestPage's own editability (SwitchViewModeInPlace) rather
//   than driving BC's PageModeAggregator.ChangePageMode: that type operates on a client
//   LogicalForm, which the runner never builds.
//
// WHAT IS STILL REFUSED
//   A page declaring Editable = false, asked for Edit() — a deliberate divergence, since BC
//   raises a bare NullReferenceException that names nothing. See
//   docs/limitations.md#testpage-page-mode-no-edit-action and
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
    /// True in every shape a service tier has been asked about — corpus 60479 (upstream #317)
    /// and 60461 (#203) assert it on every one. The action's EXISTENCE is what
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
