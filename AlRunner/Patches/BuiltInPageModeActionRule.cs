// The two decisions behind a page's built-in View / Edit action, as pure functions of their
// inputs — which shape the action is, and whether it is enabled.
//
// They live apart from BuiltInPageModeAction and LiveNavTestPage for the reason
// TestPageNewRowLineRule and LiveNavTestPage.OffersBuiltInAction do: every value below is a
// measured claim about real BC, and a claim that can only be exercised by standing up a BC
// runtime and a TestPage is a claim nothing pins cheaply. Here each one is an assertion
// (AlRunner.Tests/BuiltInPageModeActionRuleTests.cs).
//
// Measured on a real service tier — corpus codeunit 60479 "TPMS Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#317) and 60461 "TPVE Tests" (upstream #203) —
// not read off BC's UI builder. The builder read explains the measurements and does not stand
// in for them; where the two disagreed the measurement won (issue #3258).
namespace AlRunner.Patches;

/// <summary>Which of BC's shapes a built-in page-mode action is, or why there is none.</summary>
internal enum BuiltInPageModeShape
{
    /// <summary>The host declares a resolvable CardPageId: the action opens that card.</summary>
    OpenCard,

    /// <summary>A list with no card to open: the action exists and does nothing.</summary>
    NoTarget,

    /// <summary>A non-list host: the page already open changes mode.</summary>
    InPlaceSwitch,

    /// <summary>
    /// The host's PageType is unknown here, so the runner cannot tell NoTarget from
    /// InPlaceSwitch — and those two differ in whether the page's editability moves.
    /// </summary>
    RefuseUnknownPageType,

    /// <summary>
    /// Edit() on a host declaring <c>Editable = false</c>. BC creates no action and raises a
    /// bare NullReferenceException out of NavTestAction; the runner refuses by name.
    /// </summary>
    RefuseNoEditAction,
}

internal static class BuiltInPageModeActionRule
{
    /// <summary>
    /// Which shape the action is. <paramref name="pageType"/> is null when the host page is in
    /// neither the parsed-AL nor the dependency-symbol inventory.
    /// </summary>
    internal static BuiltInPageModeShape ResolveShape(
        int cardPageId, string? pageType, bool viewMode, bool declaredPageEditable)
    {
        if (cardPageId > 0) return BuiltInPageModeShape.OpenCard;
        if (pageType == null) return BuiltInPageModeShape.RefuseUnknownPageType;

        // BC's ResolveCardFormId falls back to the host page's own id only when the host is not
        // a List. Measured for PageType = List and for PageType = Card; the other list-shaped
        // types follow the same declaration family rather than a separate measurement.
        if (pageType.StartsWith("List", System.StringComparison.OrdinalIgnoreCase))
            return BuiltInPageModeShape.NoTarget;

        if (!viewMode && !declaredPageEditable) return BuiltInPageModeShape.RefuseNoEditAction;

        return BuiltInPageModeShape.InPlaceSwitch;
    }

    /// <summary>
    /// Whether the action can be invoked — BC's <c>LogicalAction.CanInvoke</c> for the
    /// conditions that have an answer in the runner. <paramref name="cardModifyAllowed"/> is
    /// null when the card is not in this run's inventory, which keeps the permissive answer
    /// rather than refusing on a lookup miss.
    /// </summary>
    internal static bool Enabled(
        BuiltInPageModeActionKind kind, bool viewMode, bool hostEditableNow, bool? cardModifyAllowed)
        => kind switch
        {
            // NavOpenTaskPageAction.CanSwitchViewMode: Edit applies to a page that is currently
            // read-only, View to one that is currently editable.
            BuiltInPageModeActionKind.InPlaceSwitch => viewMode ? hostEditableNow : !hostEditableNow,

            // A list with no CardPageId has no editable card to reach.
            BuiltInPageModeActionKind.NoTarget => viewMode,

            // ActionBuilder.IsModifyAllowedInCard, which BC surfaces as Enabled rather than by
            // withholding the action.
            _ => viewMode || cardModifyAllowed != false,
        };
}
