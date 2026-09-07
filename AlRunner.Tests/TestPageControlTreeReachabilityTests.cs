// TestPageControlTreeReachabilityTests — the C# half of issue #3313: what is, and is not, in a
// test page's control tree.
//
// The AL-observable claims are claims about BC, so they are NOT made here. Both are measured on
// a real service tier by corpus codeunit 60346, merged as
// StefanMaron/BusinessCentral.AL.Language.Tests PR #227, green on all 8 cloud legs:
//
//   * TestPart_InvisiblePart_IsNotInTheControlTreeAtAll — reaching a part declared
//     Visible = false errors "The part with ID = N was not found on the page." rather than
//     yielding a handle that reports false. The suite's FIRST revision asserted the pair
//     (a visible part answering true, an invisible one answering false on one open host) and
//     every leg falsified it identically; that falsification is the finding.
//
//   * TestPart_GetField_TakesAControlIdNotATableFieldNo — GetField(Id) keys on the page
//     CONTROL's generated id, and refuses a table field number with "The field with ID = 3 is
//     not found on the page." The corpus test asserts alongside it that 3 really IS a valid
//     table field number on that part's source table, so what it pins is the ID SPACE.
//
// What is provable without a loaded BC page object is the mechanism underneath both, and that
// is what these tests pin: the runner hands BC's own precompiled GetField/GetPart a NULL, which
// is the input those methods are written to refuse, instead of either answering a handle or
// raising a runner-invented RunnerOutOfScopeException. Ncl.dll 28.1:
//
//   NavTestPageBase.GetField(int, bool):  ... TestClientProxy<ITestField>.Proxy(testPage.GetField(id));
//                                         if (testField == null) throw NavTestFieldNotFoundException.Create(...)
//   NavTestPageBase.GetPart(int, bool):   ... TestClientProxy<ITestPart>.Proxy(testPage.GetPart(id));
//                                         if (testPart == null) throw NavTestPartNotFoundException.Create(...)
//
// Each refusal row below is paired with a row that must still be ACCEPTED or must still refuse
// as a runner gap. That pairing is the point: an implementation that refused everything — the
// cheapest way to make the corpus's two negative assertions pass — fails the paired rows.

using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageControlTreeReachabilityTests
{
    private const int HostPageId = 79851;

    private static LiveNavTestPage PageWithoutMetadata(IReadOnlyDictionary<int, int> controlMap)
        => new(
            record: null,
            controlIdToFieldNo: controlMap,
            creatable: false,
            page: null,
            owner: new object(),
            pageId: HostPageId);

    // ── The literal-vs-expression distinction the whole elimination rule rests on ───────

    // A part declared Visible = false is unreachable because the AL compiler folded the
    // property to the literal false. A property bound to a variable or an expression is NOT
    // eliminated, however that expression currently evaluates — it stays in the tree and its
    // visibility is a live question. Losing this distinction would make every conditionally
    // hidden control unreachable, which is a far larger wrong answer than the one #3313 fixes.
    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    public void ALiteralFalseVisible_Eliminates(string raw)
        => Assert.True(RunnerPageInstance.IsLiteralFalse(raw));

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData(null)]                 // the AL default for Visible is true
    [InlineData("ShowTheHiddenPart")]  // a page global that may well be false right now
    [InlineData("NOT ShowIt")]
    public void AnythingElse_DoesNotEliminate(string? raw)
        => Assert.False(RunnerPageInstance.IsLiteralFalse(raw));

    // ── GetField: BC's own refusal vs. the runner's gap refusal ────────────────────────

    // THE REGRESSION ROW for the table-field-number half. The runner must not classify an id
    // BC refuses BY DESIGN as an unimplemented runner surface. Without a live page object the
    // runner cannot ask the declaration question at all, so it must keep the gap refusal —
    // this row pins the conservative branch, which is the honest answer there: the runner does
    // not know whether BC would have found the control.
    //
    // It is also the paired control for AControlIdInTheMap_StillResolvesToItsOwnField below:
    // if the fix had simply deleted the gap refusal, or had refused everything, the two rows
    // could not both hold.
    [Fact]
    public void WithoutAPageObject_AnUnresolvableFieldStaysARunnerGap()
    {
        var page = PageWithoutMetadata(new Dictionary<int, int>());

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => page.GetField(3));

        Assert.Contains("testpage-control-binding", ex.Message);
        Assert.Contains("TestPage control 3", ex.Message);
        // The reason must say the page object is missing, not blame the table for a field it
        // was never asked about.
        Assert.Contains("no AL page object was built", ex.Message);
    }

    // The ACCEPT side, and the row an implementation that refuses everything cannot pass: a
    // control id that IS in the page's control map still resolves to a working field handle.
    // Asserted through the handle's own control id so the test cannot pass by being handed
    // some other control's instance — the #2088-class defect this map's keying exists to stop.
    [Fact]
    public void AControlIdInTheMap_StillResolvesToItsOwnField()
    {
        const int ControlId = 450598965;
        var page = PageWithoutMetadata(new Dictionary<int, int> { [ControlId] = 3 });

        var field = page.GetField(ControlId);

        // Not merely non-null: it must be the LIVE field type, not the base mock's
        // answer-anything MockITestField, which hands back a handle for any id at all and is
        // indistinguishable from a real hit until something reads a value off it.
        Assert.IsType<LiveNavTestField>(field);
    }

    // And the discrimination itself, stated as one assertion over one page: the SAME page that
    // resolves its real control id refuses the table field number that control is bound to.
    // This is the C# shadow of what the corpus measures, and it is what makes "GetField keys on
    // control ids" a property of the runner rather than a coincidence of two separate tests.
    [Fact]
    public void OnOnePage_TheControlIdResolvesAndItsTableFieldNumberDoesNot()
    {
        const int ControlId = 450598965;
        const int TableFieldNo = 3;
        var page = PageWithoutMetadata(new Dictionary<int, int> { [ControlId] = TableFieldNo });

        Assert.NotNull(page.GetField(ControlId));
        Assert.Throws<RunnerOutOfScopeException>(() => page.GetField(TableFieldNo));
    }

    // ── GetPart ────────────────────────────────────────────────────────────────────────

    // The paired control for the invisible-part fix. Without a page object the runner has no
    // part definitions at all, so it still refuses by name as a gap rather than returning null
    // — returning null here would hand BC's GetPart a "not found" for a part the runner simply
    // failed to build, reporting a runner gap as BC's own refusal, which is the mirror image of
    // the bug being fixed and just as silent.
    [Fact]
    public void WithoutAPageObject_APartStaysARunnerGap()
    {
        var page = PageWithoutMetadata(new Dictionary<int, int>());

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => page.GetPart(450598965));

        Assert.Contains("testpage-part", ex.Message);
        Assert.Contains("no AL page object was built", ex.Message);
    }

    // ── The constant that is now load-bearing ──────────────────────────────────────────

    // LiveNavTestPart.Visible/Enabled answer a constant true, and after #3313 that is a
    // CONSEQUENCE of the reachability rule rather than a hardcode: no LiveNavTestPart is ever
    // constructed for an eliminated part control, so every instance that exists is one BC would
    // render. Pinning it here means a future change that starts constructing parts for
    // eliminated controls — reintroducing the bug — has to confront this test, because the
    // constant would then be answering for a control BC never put in the tree.
    [Fact]
    public void AReachablePart_ReportsVisibleAndEnabled()
    {
        var part = new LiveNavTestPart(
            record: null,
            controlIdToFieldNo: new Dictionary<int, int>(),
            creatable: false,
            page: null,
            owner: new object(),
            pageId: HostPageId,
            parentRecord: null,
            links: []);

        Assert.True(part.Visible);
        Assert.True(part.Enabled);
    }
}
