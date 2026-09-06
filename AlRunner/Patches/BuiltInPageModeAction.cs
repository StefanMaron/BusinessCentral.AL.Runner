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
//     * IsModifyAllowedInCard — the Edit action is not created at all when the card does not
//       allow modification, which is why Edit() can legitimately answer null.
//     * NavOpenTaskPageAction.FindFormState / CreateForm — the ROW and the MODE. `new
//       FormState(ViewMode)` carries the requested mode onto the target, and the parent
//       binding manager's CurrentRow bookmark is stamped onto it, so the card opens on the
//       row the list is standing on.
//
//   And the service tier has adjudicated the result: corpus codeunit 60461 "TPVE Tests"
//   (StefanMaron/BusinessCentral.AL.Language.Tests#203) drives a List with CardPageId, parks it
//   on its second row, invokes each action, and asserts that the card opens exactly once, on
//   that row, that the [PageHandler] ran, and that View gives the handler a read-only page
//   while Edit gives it an editable one.
//
// WHAT THIS FILE IMPLEMENTS
//   Exactly the measured shape: a page that declares a resolvable CardPageId opens that card,
//   on the host's current row, in the requested mode, through BC's own NavForm front door —
//   so handler lookup, TestPage.Trap() and the "Unhandled UI" refusal stay BC's.
//
// WHAT IT REFUSES, LOUDLY
//   The in-place variant, where ResolveCardFormId lands on the host page's own id and
//   NavOpenTaskPageAction.InvokeCore switches THAT page's mode instead of opening anything
//   (UseCurrentForm -> PageModeAggregator.ChangePageMode). No corpus test measures it, the
//   runner models a TestPage's editability as a value fixed at open time, and answering it by
//   opening a second copy of the page would be a silent wrong answer. Issue #3258.
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

/// <summary>
/// A page's built-in View / Edit action, wired to the page it actually opens. See this file's
/// header for what BC's own client does and where each rule below is read from.
/// </summary>
internal sealed class BuiltInPageModeAction : ITestAction
{
    private readonly LiveNavTestPage _host;
    private readonly NavRecord? _record;
    private readonly int _targetPageId;
    private readonly bool _viewMode;

    internal BuiltInPageModeAction(LiveNavTestPage host, NavRecord? record, int targetPageId, bool viewMode)
    {
        _host = host;
        _record = record;
        _targetPageId = targetPageId;
        _viewMode = viewMode;
    }

    /// <summary>
    /// Save the current row, then open the card on it — the same order
    /// <see cref="LiveNavTestAction"/> uses, and BC's: <c>LogicalAction.RequiresSave</c> is set
    /// to true in <c>NavOpenTaskPageAction</c>'s constructor, so a real client sends the row it
    /// is standing on to the server before the action runs.
    /// </summary>
    public void Invoke()
    {
        _host.SaveCurrentRow();

        // The mode has to be in place BEFORE the form opens: OnOpenPage is AL that can read
        // CurrPage.Editable, and the [PageHandler] reads it through TestPage.Editable().
        RunnerPendingPageOpenMode.Arm(_targetPageId, readOnly: _viewMode);
        try
        {
            // The host's current row, which is what BC stamps onto the target's form state as
            // `parentBindingManager.CurrentRow.Bookmark` — the runner's equivalent of a
            // bookmark is handing BC's own page-run front door the record itself, exactly as
            // an action's `RunPageOnRec` already does (RunnerPageInstance.ActionRunObject).
            RunnerPageInstance.RunPageThroughBcFrontDoor(_targetPageId, _record);
        }
        finally
        {
            RunnerPendingPageOpenMode.Disarm();
        }
    }

    /// <summary>
    /// True, and the reason is structural rather than a default: in BC these two properties
    /// answer <c>LogicalAction.CanInvoke</c>, whereas whether the action EXISTS at all is
    /// decided earlier, by ActionBuilder — and that is the half this runner models, in
    /// <see cref="LiveNavTestPage.BuiltInPageModeActionFor"/>. An action that got this far was
    /// created by the builder's own rules.
    ///
    /// <para>What is NOT modelled: CanInvoke additionally refuses a multi-row selection
    /// (<c>IsMultipleSelectionDisabledAction</c>), and consults the action's FilterContext and
    /// any already-open form for the same page. None of those has a runner equivalent — the
    /// runner has no selection model and no window list — and no corpus test reads
    /// <c>Visible</c>/<c>Enabled</c> on a built-in page-mode action. Issue #3258.</para>
    /// </summary>
    public bool Visible => true;

    /// <inheritdoc cref="Visible"/>
    public bool Enabled => true;
}
