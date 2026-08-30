// TestPageHostConstructionRuleTests — the C# gating rule behind which ITestPage a TestPage
// gets (#2090).
//
// This pins the RUNNER's own decision, not "what BC does". The AL-observable behavior — that
// a subpage part on a host with NO SourceTable answers the same rowset however the host was
// opened — is a claim about BC, and it is measured on a real service tier by corpus codeunit
// 60763 "Test Page Direct Part Tests": merged as
// StefanMaron/BusinessCentral.AL.Language.Tests commit 2ddd9715 (PR #78), all five arms green
// on both BC 27.5 and BC 28.3. It is the direct-open sibling of suite 60734, which pinned the
// same shape driven through RunModal + a [ModalPageHandler] and whose fixtures it reuses.
//
// What is provable here without a loaded BC runtime is the classification itself, and it is
// exactly where the bug lived: the old call site collapsed "TryBuild produced no record" into
// "this page cannot be driven", so a legal no-SourceTable page was demoted to the navigation
// mock whose every member answers a default. The four rows below are the whole decision, and
// three of them must NOT change.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageHostConstructionRuleTests
{
    // A record was built: nothing else matters, the page is driven over its own cursor.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void RecordBuilt_AlwaysDrivesOverTheRecord(bool pageIsParsed, bool declaresSourceTable)
        => Assert.Equal(TestPageClientKind.LiveOverRecord,
            TestPageHostConstructionRule.Resolve(
                recordBuilt: true, pageIsParsed, pageDeclaresSourceTable: declaresSourceTable));

    // THE REGRESSION ROW. A parsed page that declares no SourceTable has no record to build
    // and needs none — it is driven live over a null record. This resolved to
    // NavigationMock before the fix, which is what emptied its subpage parts.
    [Fact]
    public void NoRecord_ParsedPageWithoutSourceTable_IsDrivenRecordless()
        => Assert.Equal(TestPageClientKind.LiveRecordless,
            TestPageHostConstructionRule.Resolve(
                recordBuilt: false, pageIsParsed: true, pageDeclaresSourceTable: false));

    // A page that DOES declare a source table but whose record could not be built is a runner
    // gap — a missing runtime record type. Driving it record-less would report "no rows" for a
    // page that should have had them, so this must stay on the mock the call site announces.
    [Fact]
    public void NoRecord_ParsedPageWithASourceTable_StaysOnTheNavigationMock()
        => Assert.Equal(TestPageClientKind.NavigationMock,
            TestPageHostConstructionRule.Resolve(
                recordBuilt: false, pageIsParsed: true, pageDeclaresSourceTable: true));

    // A page the parser never saw: "declares no SourceTable" is then a fact about the runner's
    // ignorance, not about the page, so it may not be read as permission to drive it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoRecord_UnparsedPage_StaysOnTheNavigationMock(bool declaresSourceTable)
        => Assert.Equal(TestPageClientKind.NavigationMock,
            TestPageHostConstructionRule.Resolve(
                recordBuilt: false, pageIsParsed: false, pageDeclaresSourceTable: declaresSourceTable));
}
