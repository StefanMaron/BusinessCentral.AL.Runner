// TestPageBuiltInCancelAffordanceTests — which built-in actions LiveNavTestPage offers, and in
// particular the extra condition on plain Cancel (#3124).
//
// WHAT IS PINNED WHERE. The BC-behaviour claim — that TestPage.Cancel() on a non-lookup page
// whose PageType gives the client no dialog chrome is REFUSED, "The built-in action = Cancel is
// not found on the page." — belongs upstream and is measured there, on a real service tier:
// corpus codeunit 60276 "MQC Tests", arm PlainModal_HasNoBuiltInCancelAction
// (StefanMaron/BusinessCentral.AL.Language.Tests#192), green on all eight cloud legs. Two more
// corpus files record the same refusal on a plain Card opened with OpenNew()
// (TestPageRecordTriggers.al) and on the precompiled List page "Error Messages"
// (TestPageModalHandler_PrecompiledPage.al); TestPageModalHandler_ModalPage.al records the
// positive side, that PageType = StandardDialog is what gives the client OK/Cancel chrome.
//
// What this file pins is the RUNNER's own decision table, as a pure function, so that every row
// of it is covered without a loaded BC runtime and without a service tier — the same split, and
// the same shape, as TestPageClientConstructionRuleTests. The corpus can only reach the rows a
// corpus fixture happens to declare; these reach all of them, including the two the corpus does
// not have a page for (an unknown PageType, and a page whose lookup mode the runner cannot
// read).
using AlRunner;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageBuiltInCancelAffordanceTests
{
    // THE REGRESSION ROW, and the three page types a real service tier has actually REFUSED.
    // "MQC Trace Modal" is PageType = Worksheet, run modally without LookupMode, and its
    // Cancel() must not resolve. Before #3124 it did, so a corpus arm that real BC refuses
    // closed the page and reported Action::Cancel instead. Card (opened with OpenNew(),
    // TestPageRecordTriggers.al) and List (precompiled "Error Messages",
    // TestPageModalHandler_PrecompiledPage.al) are the other two the corpus measures.
    [Theory]
    [InlineData("Worksheet")]
    [InlineData("Card")]
    [InlineData("List")]
    public void NonLookupPageMeasuredWithoutDialogChrome_OffersNoPlainCancel(string pageType)
        => Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType));

    // MEASURED SINCE #3283, and this is where #3131's two questions were answered. Corpus
    // codeunit 60338 "TBA Tests" drives Cancel() on both page types on a real service tier:
    // NavigatePage refuses it (arm e) and ConfirmationDialog refuses it (arm g).
    //
    // NavigatePage is the row that MOVED. It used to answer permissively here, on the inference
    // that FormState.RunModal is assigned in exactly two UI builders (NavigatePageBuilder,
    // StandardDialogBuilder) and both therefore carry OK/Cancel chrome. BC disagrees: a
    // NavigatePage's chrome is Back/Next/Finish, so it has an OK and no Cancel — see
    // NonLookupNavigatePage_OffersPlainOk below for the half that stayed. The
    // ConfirmationDialog inference held.
    [Theory]
    [InlineData("NavigatePage")]
    [InlineData("ConfirmationDialog")]
    public void NonLookupPageMeasuredWithoutCancelChrome_OffersNoPlainCancel(string pageType)
        => Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType));

    // STILL INFERRED, NOT MEASURED — kept separate from the rows above on purpose, so a later
    // reader cannot mistake these for service-tier facts. No corpus test drives Cancel() on
    // either; they are refused because they are not one of the page types a service tier has
    // shown a Cancel on, and nothing suggests they carry dialog chrome.
    [Theory]
    [InlineData("ListPlus")]
    [InlineData("Document")]
    public void NonLookupPageInferredWithoutDialogChrome_OffersNoPlainCancel_Unmeasured(string pageType)
        => Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType));

    // MEASURED, and the row this fix exists for (#3283). A PromptDialog HAS a plain Cancel —
    // whether or not it declares systemaction(Cancel), and whatever its PromptMode, because
    // PromptDialogBuilder.BeginBuildActionBar adds a form-level Cancel exit action
    // unconditionally. Corpus 60338 arms a and b.
    [Fact]
    public void NonLookupPromptDialogPage_OffersPlainCancel()
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, "PromptDialog"));

    // The other half, and what stops the rule above from being "Cancel never resolves": a page
    // built by a dialog builder DOES offer it. StandardDialog is measured — every green
    // Cancel().Invoke() in the corpus and in tests/runner-extras is aimed at one.
    [Fact]
    public void NonLookupStandardDialogPage_OffersPlainCancel()
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, "StandardDialog"));

    // The half of NavigatePage that a service tier DID confirm (corpus 60338 arm f): it offers
    // plain OK. That is what keeps the refusal above from being read as "a NavigatePage has no
    // built-ins at all".
    [Fact]
    public void NonLookupNavigatePage_OffersPlainOk()
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.OK, lookupMode: false, "NavigatePage"));

    // PageType comes from AL source or from a dependency's SymbolReference.json, neither of
    // which normalises case, so the comparison may not be ordinal-exact.
    [Theory]
    [InlineData("standarddialog")]
    [InlineData("STANDARDDIALOG")]
    [InlineData("promptdialog")]
    [InlineData("PROMPTDIALOG")]
    public void DialogPageTypeMatchIsCaseInsensitive(string pageType)
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType));

    // Plain OK is offered by nearly every non-lookup page — the corpus's PlainModal_HandlerOk
    // arm runs on the same Worksheet page the first test refuses Cancel on, and it is green
    // upstream. The two exceptions are below; a fix that gated OK the way Cancel is gated would
    // have broken this row.
    [Theory]
    [InlineData("Worksheet")]
    [InlineData("Card")]
    [InlineData("StandardDialog")]
    [InlineData("PromptDialog")]
    public void NonLookupPage_OffersPlainOk(string pageType)
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.OK, lookupMode: false, pageType));

    // EXCEPTION 1, MEASURED (corpus 60338 arm h): a ConfirmationDialog's chrome is Yes/No, so it
    // has neither built-in. This is the direction that used to answer a silent wrong OK —
    // OK().Invoke() closed the page here and raises on BC.
    [Fact]
    public void NonLookupConfirmationDialog_OffersNoPlainOk()
        => Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.OK, lookupMode: false, "ConfirmationDialog"));

    // EXCEPTION 2, MEASURED (corpus 60338 arms c and d), and the only row in this file that is
    // not a function of PageType alone: declaring systemaction(OK) REPLACES the built-in OK
    // rather than adding one, because PromptDialogBuilder.BuildPromptActions creates the
    // ExitAction only on its else-branch. Undeclared, the same page offers OK — which is what
    // makes this a statement about the declaration and not about PromptDialog.
    [Fact]
    public void PromptDialogDeclaringSystemActionOk_OffersNoPlainOk()
    {
        Assert.False(LiveNavTestPage.OffersBuiltInAction(
            FormResult.OK, lookupMode: false, "PromptDialog", declaresSystemActionOk: true));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(
            FormResult.OK, lookupMode: false, "PromptDialog", declaresSystemActionOk: false));
    }

    // ...and the declaration is inert everywhere else: it is a PromptDialog-builder fact, not a
    // general one. Cancel on the same page is unaffected too (arm a drives Cancel() on a page
    // that declares all three system actions).
    [Fact]
    public void DeclaringSystemActionOk_ChangesNothingOnOtherPageTypesOrOnCancel()
    {
        Assert.True(LiveNavTestPage.OffersBuiltInAction(
            FormResult.OK, lookupMode: false, "StandardDialog", declaresSystemActionOk: true));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(
            FormResult.OK, lookupMode: false, "Card", declaresSystemActionOk: true));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(
            FormResult.Cancel, lookupMode: false, "PromptDialog", declaresSystemActionOk: true));
    }

    // A lookup page must answer NULL for plain Cancel even when it IS a dialog, so BC's
    // FindBuiltInAction(Cancel, LookupCancel) falls through to LookupCancel — which is how
    // LookupModal_HandlerCancel_RunsQueryClosePageWithLookupCancel reports LookupCancel rather
    // than Cancel. Same for plain OK falling through to LookupOK.
    [Theory]
    [InlineData("StandardDialog")]
    [InlineData("Worksheet")]
    public void LookupPage_OffersNeitherPlainCancelNorPlainOk(string pageType)
    {
        Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: true, pageType));
        Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.OK, lookupMode: true, pageType));
    }

    // ...and the lookup pair it falls through to IS offered, on any page type. The Cancel gate
    // must not reach LookupCancel: gating that instead would have made every cancelled lookup
    // in the corpus unreachable.
    [Theory]
    [InlineData("StandardDialog")]
    [InlineData("Worksheet")]
    public void LookupPage_OffersTheLookupPair(string pageType)
    {
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.LookupCancel, lookupMode: true, pageType));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.LookupOK, lookupMode: true, pageType));
    }

    // The mirror: a NON-lookup page has no LookupOK/LookupCancel, which is what makes BC's
    // OK -> LookupOK fallback land on plain OK there rather than on the lookup one.
    [Fact]
    public void NonLookupPage_OffersNeitherLookupResult()
    {
        Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.LookupOK, lookupMode: false, "StandardDialog"));
        Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.LookupCancel, lookupMode: false, "StandardDialog"));
    }

    // Results outside the two closing pairs are not this rule's business and stay untouched —
    // a claim about lookup-vs-normal closing is not a claim about which other built-ins exist.
    [Theory]
    [InlineData("Worksheet")]
    [InlineData("StandardDialog")]
    public void ResultsOutsideTheClosingPairsAreLeftAlone(string pageType)
    {
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Yes, lookupMode: false, pageType));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.No, lookupMode: true, pageType));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Print, lookupMode: false, pageType));
    }

    // A null PageType means TryGetAnyPageType found the page in NEITHER the runner's parsed AL
    // nor a dependency's symbols — a fact about the runner's inventory, not about the page. It
    // stays permissive, because refusing on the strength of a lookup miss would turn every
    // unknown page's Cancel() into a spurious "not found".
    [Fact]
    public void UnknownPageType_KeepsThePermissiveAnswer()
    {
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType: null));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: null, pageType: null));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.LookupCancel, lookupMode: null, pageType: null));
    }

    // A page with no RunnerPageInstance (lookupMode unknown) still gets the Cancel rule, because
    // the PageType is known even when the mode is not — that is the precompiled "Error Messages"
    // List case the corpus records BC refusing. Everything else about such a page stays as
    // permissive as it was.
    [Fact]
    public void UnknownLookupMode_StillAppliesTheCancelRuleButNothingElse()
    {
        Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: null, "List"));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: null, "StandardDialog"));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.OK, lookupMode: null, "List"));
        Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.LookupOK, lookupMode: null, "List"));
    }

    // The affordance predicate on its own, so a reader can see the set is exactly two names plus
    // the unknown case — and so a future edit that widened it to "any dialog-ish name" fails
    // here rather than silently in the corpus.
    //
    // Every row except ListPlus/Document/API/"" is a service-tier fact now: StandardDialog,
    // Card, List and Worksheet from #3059/#3152, PromptDialog, NavigatePage and
    // ConfirmationDialog from #3283 (corpus 60338 arms a, b, e, g). The four remaining rows ride
    // on the inference that a page type nobody has shown a Cancel on does not have one; they are
    // not surprising members of the no-chrome set.
    [Theory]
    [InlineData("StandardDialog", true)]      // measured
    [InlineData("PromptDialog", true)]        // measured, #3283
    [InlineData(null, true)]
    [InlineData("NavigatePage", false)]       // measured, #3283 — was an inferred true
    [InlineData("ConfirmationDialog", false)] // measured, #3283
    [InlineData("Card", false)]
    [InlineData("List", false)]
    [InlineData("Worksheet", false)]
    [InlineData("API", false)]
    [InlineData("", false)]
    public void HasDialogCancelAffordance_IsExactlyTheMeasuredDialogPageTypes(string? pageType, bool expected)
        => Assert.Equal(expected, LiveNavTestPage.HasDialogCancelAffordance(pageType));

    // The OK predicate on its own, the same way. It is nearly-always-true, and the point of the
    // table is that the two false rows do not follow the Cancel table: PromptDialog is true for
    // Cancel and false here once it declares one, ConfirmationDialog is false for both, and
    // NavigatePage is false for Cancel and true here.
    [Theory]
    [InlineData("StandardDialog", false, true)]
    [InlineData("NavigatePage", false, true)]        // measured, #3283
    [InlineData("Worksheet", false, true)]
    [InlineData("Card", false, true)]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]                   // unknown page: stays permissive either way
    [InlineData("PromptDialog", false, true)]        // measured, #3283
    [InlineData("PromptDialog", true, false)]        // measured, #3283
    [InlineData("ConfirmationDialog", false, false)] // measured, #3283
    [InlineData("ConfirmationDialog", true, false)]
    public void HasPlainOkAffordance_RefusesOnlyConfirmationDialogAndADeclaringPromptDialog(
        string? pageType, bool declaresSystemActionOk, bool expected)
        => Assert.Equal(expected, LiveNavTestPage.HasPlainOkAffordance(pageType, declaresSystemActionOk));

    // #3284, and a table the Cancel one cannot predict: what a NON-lookup page reports when the
    // handler invoked nothing. ConfirmationDialog reports Cancel while having no built-in Cancel
    // to invoke; NavigatePage reports OK while having none either. Measured in corpus 60338 arms
    // i-l, with the Worksheet row from "MQC Tests" (60276) arm b.
    [Theory]
    [InlineData("StandardDialog", FormResult.Cancel)]
    [InlineData("PromptDialog", FormResult.Cancel)]
    [InlineData("ConfirmationDialog", FormResult.Cancel)]
    [InlineData("NavigatePage", FormResult.OK)]
    [InlineData("Worksheet", FormResult.OK)]
    [InlineData("Card", FormResult.OK)]
    [InlineData("List", FormResult.OK)]
    [InlineData(null, FormResult.OK)]
    [InlineData("", FormResult.OK)]
    public void UnattendedCloseResult_IsCancelOnlyForTheThreeMeasuredDialogs(
        string? pageType, FormResult expected)
        => Assert.Equal(expected, LiveNavTestPage.UnattendedCloseResult(pageType));

    // Case-insensitive for the same reason the affordance rules are: PageType arrives from AL
    // source or a SymbolReference.json, neither of which normalises case.
    [Theory]
    [InlineData("standarddialog")]
    [InlineData("PROMPTDIALOG")]
    [InlineData("confirmationdialog")]
    public void UnattendedCloseResultMatchIsCaseInsensitive(string pageType)
        => Assert.Equal(FormResult.Cancel, LiveNavTestPage.UnattendedCloseResult(pageType));
}
