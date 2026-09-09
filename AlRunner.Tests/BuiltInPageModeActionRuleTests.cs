using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The measured table behind <c>TestPage.View()</c> / <c>TestPage.Edit()</c>, one assertion per
/// row. The claims themselves are BC's, adjudicated by a real service tier — corpus codeunit
/// 60479 "TPMS Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#317, 10 arms on BC
/// 28.4.53241.0) and 60461 "TPVE Tests" (upstream #203). These pin the runner's own decision
/// function against them, which is what the upstream arms cannot do until the corpus pin moves
/// (the chain is blocked on issue #2943), and what no BC-runtime test does cheaply.
///
/// The negative half is the point: before issue #3258 both properties answered a hardcoded
/// true and two of the shapes were refused outright, so an implementation that answers "true,
/// always" passes nothing here.
/// </summary>
public class BuiltInPageModeActionRuleTests
{
    // ── Which shape the action is ───────────────────────────────────────────────────────────

    [Theory]
    // A resolvable CardPageId wins whatever the host is, and for both modes (corpus 60461).
    [InlineData(60458, "List", true, true, "OpenCard")]
    [InlineData(60458, "List", false, true, "OpenCard")]
    [InlineData(60458, "Card", false, true, "OpenCard")]
    // A list with nothing to open still has both actions; they do nothing (60479
    // PlainListWithoutCardPageIdOffersBothActionsAndEnablesOnlyView).
    [InlineData(0, "List", true, true, "NoTarget")]
    [InlineData(0, "List", false, true, "NoTarget")]
    [InlineData(0, "ListPart", false, true, "NoTarget")]
    // Not a list, no card: the page already open changes mode (60479, both in-place arms).
    [InlineData(0, "Card", true, true, "InPlaceSwitch")]
    [InlineData(0, "Card", false, true, "InPlaceSwitch")]
    [InlineData(0, "Document", false, true, "InPlaceSwitch")]
    // Editable = false: View still exists, Edit does not (60479's file header).
    [InlineData(0, "Card", true, false, "InPlaceSwitch")]
    [InlineData(0, "Card", false, false, "RefuseNoEditAction")]
    // Unknown PageType cannot be told apart from either, so it is refused rather than guessed.
    [InlineData(0, null, true, true, "RefuseUnknownPageType")]
    [InlineData(0, null, false, true, "RefuseUnknownPageType")]
    public void ResolveShape_AnswersTheShapeTheServiceTierMeasured(
        int cardPageId, string? pageType, bool viewMode, bool declaredEditable,
        string expected)
        // The enum is internal, and an xUnit theory method must be public, so the expected value
        // travels as its name rather than as the value itself.
        => Assert.Equal(
            expected,
            BuiltInPageModeActionRule.ResolveShape(cardPageId, pageType, viewMode, declaredEditable).ToString());

    [Fact]
    public void ResolveShape_DoesNotRefuseAnEditActionOnAReadOnlyCardWhenACardPageIdResolves()
    {
        // The read-only rule is about the HOST's own Editable, never the target card's: a list
        // whose card is read-only keeps its Edit action and expresses the refusal through
        // Enabled (60479 ListWhoseCardIsReadOnlyLeavesTheEditActionVisibleButNotEnabled). The
        // runner used to throw here, which is half of what #3258 was filed for.
        Assert.Equal(
            BuiltInPageModeShape.OpenCard,
            BuiltInPageModeActionRule.ResolveShape(60475, "List", viewMode: false, declaredPageEditable: false));
        Assert.True(
            BuiltInPageModeActionRule.Enabled(
                BuiltInPageModeActionKind.OpenCard, viewMode: true, hostEditableNow: true, cardModifyAllowed: false),
            "the View half of the same list stays enabled and does open that card, read-only");
    }

    // ── Whether it can be invoked ───────────────────────────────────────────────────────────

    [Theory]
    // In place: enabled only when the requested mode differs from the current one
    // (CanSwitchViewMode). Rows 1-2 are the switch arms, 3-4 the "already in that mode" arms.
    [InlineData("InPlaceSwitch", false, false, true)]  // Edit on a read-only page
    [InlineData("InPlaceSwitch", true, true, true)]    // View on an editable page
    [InlineData("InPlaceSwitch", false, true, false)]  // Edit, already editable
    // View on a page that is already read-only, incl. one declaring Editable = false
    // (60479 RoCardPageHandler asserts IsFalse on View().Enabled() there).
    [InlineData("InPlaceSwitch", true, false, false)]  // View, already read-only
    // A list with no card: View is enabled, Edit is not.
    [InlineData("NoTarget", true, true, true)]
    [InlineData("NoTarget", false, true, false)]
    public void Enabled_FollowsTheModeThePageIsIn(
        string kind, bool viewMode, bool hostEditableNow, bool expected)
        => Assert.Equal(
            expected,
            BuiltInPageModeActionRule.Enabled(
                System.Enum.Parse<BuiltInPageModeActionKind>(kind), viewMode, hostEditableNow, null));

    [Theory]
    // Opening a card: View always; Edit only when that card allows modification.
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, null, true)]   // unknown card keeps the permissive answer
    [InlineData(true, null, true)]
    public void Enabled_OnACardOpeningActionFollowsTheCardsOwnModifyAllowed(
        bool viewMode, bool? cardModifyAllowed, bool expected)
        => Assert.Equal(
            expected,
            BuiltInPageModeActionRule.Enabled(
                BuiltInPageModeActionKind.OpenCard, viewMode, hostEditableNow: true, cardModifyAllowed));

    [Fact]
    public void Enabled_IsNotAConstant()
    {
        // The regression guard for what #3258 reported: Visible and Enabled both answered a
        // hardcoded true. Any implementation that answers one value everywhere fails here.
        var answers = new[]
        {
            BuiltInPageModeActionRule.Enabled(BuiltInPageModeActionKind.InPlaceSwitch, false, true, null),
            BuiltInPageModeActionRule.Enabled(BuiltInPageModeActionKind.InPlaceSwitch, false, false, null),
            BuiltInPageModeActionRule.Enabled(BuiltInPageModeActionKind.NoTarget, false, true, null),
            BuiltInPageModeActionRule.Enabled(BuiltInPageModeActionKind.OpenCard, false, true, false),
            BuiltInPageModeActionRule.Enabled(BuiltInPageModeActionKind.OpenCard, true, true, false),
        };
        Assert.Contains(true, answers);
        Assert.Contains(false, answers);
    }
}
