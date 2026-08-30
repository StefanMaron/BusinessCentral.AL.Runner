// TestPageNewRowLineRuleTests — the C# gating rules behind the TestPage new-row line (#2089).
//
// These pin the RUNNER's own decision logic, not "what BC does". The AL-observable behavior is
// proved against real BC 27.5/28.3 by TWO corpus suites, merged separately, and they measure
// different halves of it:
//
//   * codeunit 60743 "Test Page New Row Line Tests" — what the editability answer DOES to the
//     new-row line. Merged as StefanMaron/BusinessCentral.AL.Language.Tests commit a5576344
//     (PR #76); all nine arms green on both legs.
//   * codeunit 60747 "Test Page Hdlr Editable Tests" — the page-level TestPage.Editable()
//     answer itself, on a page the test never opened. Merged as commit 72281941 (PR #77); both
//     arms green on both legs.
//
// What is provable here without a loaded BC runtime is that the two gates combine the way those
// measurements require — including the two arms that were WRONG before this fix and that a
// runner-side test can catch cheaply:
//
//   * a page BC handed to a [ModalPageHandler] has no open mode, so it must fall back to the
//     page's own Editable rather than to a flat "editable" (the old field default);
//   * a subpage part is editable only if its host is.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageNewRowLineRuleTests
{
    // ---- ResolveStaticEditable ------------------------------------------------------------

    [Theory]
    // The test opened the page itself: the open mode already decided, and it wins outright —
    // OpenView on an Editable = true page is still not editable, and nothing below overrides it.
    [InlineData(true, null, true, true)]
    [InlineData(false, null, true, false)]
    // ...even when a host or the page's own property would say otherwise.
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    public void OpenMode_WhenPresent_DecidesOutright(
        bool? openModeEditable, bool? hostStaticEditable, bool pageEditable, bool expected)
        => Assert.Equal(expected,
            TestPageNewRowLineRule.ResolveStaticEditable(openModeEditable, hostStaticEditable, pageEditable));

    [Theory]
    // No open mode (a handler-driven page): the page's OWN Editable is the answer. The second
    // row is the regression this fix exists for — it used to resolve to true.
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NoOpenMode_TopLevelPage_FallsBackToThePagesOwnEditable(bool pageEditable, bool expected)
        => Assert.Equal(expected,
            TestPageNewRowLineRule.ResolveStaticEditable(null, hostStaticEditable: null, pageEditable));

    [Theory]
    // A part: editable only if BOTH the host and the part's own property say so.
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void NoOpenMode_Part_RequiresBothHostAndItsOwnEditable(
        bool hostStaticEditable, bool pageEditable, bool expected)
        => Assert.Equal(expected,
            TestPageNewRowLineRule.ResolveStaticEditable(null, hostStaticEditable, pageEditable));

    // ---- ShowsNewRowLine ------------------------------------------------------------------

    [Fact]
    public void ShowsNewRowLine_OnlyWhenEditableAndInsertAllowed()
    {
        Assert.True(TestPageNewRowLineRule.ShowsNewRowLine(staticEditable: true, insertAllowed: true));

        // Each gate alone suppresses it. These are the OpenView / Editable = false and the
        // InsertAllowed = false arms of corpus CU60743, and they are what a fix that simply
        // made Next() answer true once more at the end of any rowset would break.
        Assert.False(TestPageNewRowLineRule.ShowsNewRowLine(staticEditable: false, insertAllowed: true));
        Assert.False(TestPageNewRowLineRule.ShowsNewRowLine(staticEditable: true, insertAllowed: false));
        Assert.False(TestPageNewRowLineRule.ShowsNewRowLine(staticEditable: false, insertAllowed: false));
    }
}
