// TestPageClientConstructionRule — which client a TestPage over page N gets (#2090, #2195).
//
// Named "…Host…" when it landed, because the only caller was the construction site for the
// page a TestPage handle is over. #2195 added the second caller —
// LiveNavTestPage.GetPart, classifying the PART's own page — and the question is the same
// one there, so the name no longer says "host". Both callers' AL-observable behaviour is
// measured on real BC: the host half by corpus codeunit 60763 (commit 2ddd9715), the part
// half by codeunit 60803 (commit ef52b7e9), both green on BC 27.5 and BC 28.3.
//
// NavTestPageHandle.CreateTarget has to hand BC's NavTestPage an ITestPage. There are three
// possible answers and the choice is not obvious, which is how the wrong one shipped: any
// page whose RECORD could not be built was demoted to the blanket navigation mock, every one
// of whose members answers a default — MoveFirst() false, GetField() blank, GetPart() an
// empty MockITestPart. That is right for a page the runner genuinely cannot drive and wrong
// for a page that simply has no source table.
//
// A page with no SourceTable is ordinary, legal AL: the StandardDialog / Worksheet-header
// shape, whose controls bind to page globals rather than to a record. It still has a control
// tree, its own AL triggers and a part list, and a subpage part on it drives its OWN source
// table — nothing about the part's rowset depends on the host having one, since only a FIELD
// SubPageLink could make it. Demoting such a host silently emptied its parts, which is what
// issue #2090 reported: the same part found its rows under RunModal + [ModalPageHandler] and
// not under TestPage.OpenEdit, because the handler-driven construction site
// (RunnerTestClientSession.GetPage) has built a live page over a null record for this shape
// since #2007 and this one never did.
//
// Kept as a pure function of three booleans so the classification is testable without a
// loaded BC runtime — same reason TestPageNewRowLineRule exists.
namespace AlRunner.Patches;

/// <summary>Which ITestPage implementation a TestPage over a given page id gets.</summary>
internal enum TestPageClientKind
{
    /// <summary>Driven live over the page's own source-table cursor.</summary>
    LiveOverRecord,

    /// <summary>Driven live with a null record: control tree, actions, parts and page
    /// triggers all work; anything that genuinely needs a record refuses BY NAME through
    /// LiveNavTestPage.RequireRecord rather than answering a default.</summary>
    LiveRecordless,

    /// <summary>The runner cannot drive this page at all — it was never parsed, or it
    /// declares a source table the runner has no runtime record type for. Both are runner
    /// gaps. How they are REPORTED is the call site's choice, not this enum's: the TestPage
    /// handle site degrades to MockITestPage and says so on stderr (BC constructs that page
    /// eagerly, whether or not the test ever touches it, so throwing there would fail tests
    /// that never used the page); LiveNavTestPage.GetPart throws RunnerOutOfScopeException,
    /// because a part is only built when AL asks for it and an empty MockITestPart answers
    /// "no rows, no inserts" to a question the test did ask.</summary>
    NavigationMock,
}

internal static class TestPageClientConstructionRule
{
    /// <param name="recordBuilt">Whether TestPageFactory.TryBuild produced a record cursor.</param>
    /// <param name="pageIsParsed">Whether the runner AL-source-parsed this page, so "declares
    /// no SourceTable" is a fact about the page rather than about the runner's ignorance.</param>
    /// <param name="pageDeclaresSourceTable">Whether the parsed page declares a SourceTable.</param>
    internal static TestPageClientKind Resolve(
        bool recordBuilt, bool pageIsParsed, bool pageDeclaresSourceTable)
    {
        if (recordBuilt) return TestPageClientKind.LiveOverRecord;

        // No record AND the page declares no source table: nothing is missing — there is
        // simply nothing to build. Drive it record-less.
        if (pageIsParsed && !pageDeclaresSourceTable) return TestPageClientKind.LiveRecordless;

        // No record but the page DOES declare a source table (or the runner never saw the
        // page): something the runner needed is missing. Answering record-less here would
        // swap one wrong answer for another — a page that should have had rows would report
        // none, quietly — so this keeps the pre-existing mock, which the call site announces.
        return TestPageClientKind.NavigationMock;
    }
}
