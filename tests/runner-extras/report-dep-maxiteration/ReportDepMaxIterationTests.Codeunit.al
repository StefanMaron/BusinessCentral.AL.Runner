// Regression test — a precompiled dependency report's dataitem MaxIteration (#3370).
//
// This suite reproduces the defect HERMETICALLY. "RMI Precompiled MaxIteration Dep" (see
// this folder's .alpackages/*.app + .deps-bin/*.dll, and app.json's dependency entry) ships
// report 65801 as a Tier-1 precompiled DLL, so the runner never source-compiles it and
// AlReportMetadataRegistry never captures its metadata. NavReport.Metadata therefore falls
// through to RecordPatches.TryBuildDependencyReportMetadata, which rebuilds the runtime XML
// out of the .app's own SymbolReference.json — the surface under test.
//
// The report has two Integer dataitems and each one's OnAfterGetRecord inserts a row into
// table 65800 "RMI Counter", so the number of iterations is directly observable from AL
// rather than inferred from a duration. A duration assertion would be flaky and would pass
// on a fast box with the defect present, which is the whole reason the count is what is
// asserted here.
//
//   BoundedByMaxIteration — DataItemTableView states a sorting and nothing else, so the
//     ONLY thing that can stop this loop is MaxIteration = 1.
//   BoundedByFilter       — where(Number = filter(1 .. 3)) and no MaxIteration at all.
//
// RED (before the fix): BcAppSymbolCache.CollectReportDataItems read DataItemTableView,
// RequestFilterFields, DataItemLink, DataItemLinkReference and PrintOnlyIfDetail out of the
// dataitem's property dictionary and NOT MaxIteration, so ReportDataItemSymbol had no field
// for it and DependencyReportMetadata.WriteDataItem emitted no <MaxIteration> element.
// Types.dll's MetaDataItem left MaxIteration at 0, and Ncl.dll's
// DataItemIterator.ExecuteDataItemLoopAsync bounds the loop with
//     if (dataItem.MetaData.MaxIteration != 0 && maxIteration == dataItem.MetaData.MaxIteration)
//         break;
// where 0 means NO LIMIT — so BoundedByMaxIteration ran across the whole Integer
// virtual-table window (-1000 .. 100000, 101,001 rows) instead of once. That is the hang
// that aborted Codeunit134008.CalcPostVATSettlementForSalesTax with 8,914 Microsoft tests
// still unrun (#3370); Base Application report 20's "Close VAT Entries" dataitem is the
// same shape as BoundedByMaxIteration, MaxIteration = 1 over Integer.
//
// GREEN (after the fix): the property is parsed and emitted, so the loop runs once.
codeunit 65281 "RMI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RMI Assert";

    // Positive: the declared bound is honored, and the count is EXACT. One row means the
    // property reached BC's loop; the pre-fix number here is the Integer window size, so a
    // regression cannot hide inside a tolerance.
    [Test]
    procedure MaxIterationDataItem_OfPrecompiledDepReport_RunsExactlyOnce()
    var
        Counter: Record "RMI Counter";
    begin
        Counter.DeleteAll();

        Report.Run(65801, false, false);

        Counter.SetRange("Loop Name", 'MAXITER');
        Assert.AreEqual(1, Counter.Count(),
            'the dataitem declares MaxIteration = 1 and no restricting filter, so its OnAfterGetRecord must run exactly once — a larger count means the property was dropped and BC read 0 as "no limit", iterating the whole Integer window');
    end;

    // Negative, same report and same run: a dataitem that declares NO MaxIteration must
    // keep running to its filter's end. A fix that clamped every dataitem to one iteration
    // would satisfy the test above and break this one, which is exactly why the fixture
    // report carries both shapes.
    [Test]
    procedure FilterBoundedDataItem_WithNoMaxIteration_RunsItsWholeFilteredRange()
    var
        Counter: Record "RMI Counter";
    begin
        Counter.DeleteAll();

        Report.Run(65801, false, false);

        Counter.SetRange("Loop Name", 'FILTER');
        Assert.AreEqual(3, Counter.Count(),
            'the dataitem declares where(Number = filter(1 .. 3)) and no MaxIteration, so it must run three times — 1 would mean MaxIteration was fabricated for a dataitem that never declared one, and truncating a report that way is the mirror defect');
    end;
}
