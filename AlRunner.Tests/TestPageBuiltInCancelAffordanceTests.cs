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

    // INFERRED, NOT MEASURED — kept separate from the rows above on purpose, so a later reader
    // cannot mistake these for service-tier facts. No corpus test drives Cancel() on any of
    // these three page types; they are refused because FormState.RunModal is assigned in exactly
    // two UI builders (NavigatePageBuilder, StandardDialogBuilder) and none of them is one.
    //
    // ConfirmationDialog is the row to watch, and it is the reason this test exists separately:
    // its NAME suggests OK/Cancel chrome, it moves from permissive to REFUSING here, and
    // refusing is the riskier direction — a wrong refusal breaks AL that works on real BC,
    // whereas a wrong permission only fails to reproduce a BC error. It is drawn from the same
    // builder fact that was judged too thin to act on for NavigatePage (which keeps its
    // permissive answer below). #3131 tracks getting both measured upstream; if BC turns out to
    // offer Cancel on a ConfirmationDialog, THIS is the test that has to change, not the one
    // above.
    [Theory]
    [InlineData("ConfirmationDialog")]
    [InlineData("ListPlus")]
    [InlineData("Document")]
    public void NonLookupPageInferredWithoutDialogChrome_OffersNoPlainCancel_Unmeasured(string pageType)
        => Assert.False(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType));

    // The other half, and what stops the rule above from being "Cancel never resolves": a page
    // built by a dialog builder DOES offer it. StandardDialog is measured — every green
    // Cancel().Invoke() in the corpus and in tests/runner-extras is aimed at one.
    [Fact]
    public void NonLookupStandardDialogPage_OffersPlainCancel()
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, "StandardDialog"));

    // INFERRED, NOT MEASURED, and the permissive direction of the same builder fact: NavigatePage
    // keeps the answer it already had rather than gaining a refusal. Its chrome is
    // Back/Next/Finish/Cancel rather than OK/Cancel, so BC may well answer differently here --
    // #3131, same issue as the refusing inference above.
    [Fact]
    public void NonLookupNavigatePage_OffersPlainCancel_Unmeasured()
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, "NavigatePage"));

    // PageType comes from AL source or from a dependency's SymbolReference.json, neither of
    // which normalises case, so the comparison may not be ordinal-exact.
    [Theory]
    [InlineData("standarddialog")]
    [InlineData("STANDARDDIALOG")]
    [InlineData("navigatepage")]
    public void DialogPageTypeMatchIsCaseInsensitive(string pageType)
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.Cancel, lookupMode: false, pageType));

    // Cancel is the ONLY built-in that gained a condition. Plain OK is offered by every
    // non-lookup page whatever its type — the corpus's PlainModal_HandlerOk arm runs on the
    // same Worksheet page the first test refuses Cancel on, and it is green upstream. A fix
    // that gated both would have broken it.
    [Theory]
    [InlineData("Worksheet")]
    [InlineData("Card")]
    [InlineData("StandardDialog")]
    public void NonLookupPage_AlwaysOffersPlainOk(string pageType)
        => Assert.True(LiveNavTestPage.OffersBuiltInAction(FormResult.OK, lookupMode: false, pageType));

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
    // Two of these rows are NOT service-tier facts, and the table cannot show that by itself:
    // NavigatePage (true) and ConfirmationDialog (false) are both inferred from the UI-builder
    // fact and tracked by #3131 — see the two *_Unmeasured tests above, which are where those
    // two claims are pinned with their provenance attached. StandardDialog/Card/List/Worksheet
    // are measured upstream; ListPlus/Document/API/"" ride on the same inference as
    // ConfirmationDialog but are not surprising members of the no-chrome set.
    [Theory]
    [InlineData("StandardDialog", true)]
    [InlineData("NavigatePage", true)]        // inferred, #3131
    [InlineData(null, true)]
    [InlineData("ConfirmationDialog", false)] // inferred, #3131 — the riskier direction
    [InlineData("Card", false)]
    [InlineData("List", false)]
    [InlineData("Worksheet", false)]
    [InlineData("API", false)]
    [InlineData("", false)]
    public void HasDialogCancelAffordance_IsExactlyTheTwoDialogBuilderPageTypes(string? pageType, bool expected)
        => Assert.Equal(expected, LiveNavTestPage.HasDialogCancelAffordance(pageType));
}
