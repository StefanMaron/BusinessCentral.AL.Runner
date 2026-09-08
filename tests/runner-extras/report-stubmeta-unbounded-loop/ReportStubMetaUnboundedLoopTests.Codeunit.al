// Regression test — a report NOTHING in the run describes must not have its data-item loop
// run against metadata the runner invented (#3375).
//
// THE SHAPE, AND HOW THIS SUITE REPRODUCES IT HERMETICALLY
// "RSM Precompiled StubMeta Dep" (this folder's .alpackages/*.app + .deps-bin/*.dll, and
// app.json's dependency entry) ships reports 65821 and 65822 as a Tier-1 precompiled DLL,
// and its .app carries NO SymbolReference.json. That closes BOTH of the runner's metadata
// sources at once: AlReportMetadataRegistry never saw these reports (the runner never
// source-compiles the dep), and RecordPatches.TryBuildDependencyReportMetadata finds no
// symbol entry to reconstruct from. NavReportSync.StubInitializeMetadata therefore installs
// the legacy stub MetaReport, and ReportAdd synthesizes a MetaDataItem per data item out of
// the data item's NAME alone (BuildSyntheticFlatMetaDataItem).
//
// RED (before the fix): that synthetic MetaDataItem leaves MaxIteration at 0 and states no
// DataItemTableView, and BC reads BOTH absences as "no bound" —
//   DataItemIterator.ExecuteDataItemLoopAsync:
//     if (dataItem.MetaData.MaxIteration != 0 && maxIteration == dataItem.MetaData.MaxIteration)
//         break;
// So report 65821's `MaxIteration = 1` data item ran the whole Integer virtual-table window
// (-1000 .. 100000, 101,001 rows) instead of once, and its `where(Number = filter(1 .. 3))`
// sibling ran 101,001 times instead of 3. Measured on this exact fixture with
// AL_RUNNER_INTEGER_WINDOW_MAX=50 (1,051 rows): the report's own OnPostReport reported
// "maxiter=1051 filter=1051" against a declared 1 and 3. That is the unbounded loop that
// aborted Codeunit134008.CalcPostVATSettlementForSalesTax with 8,914 Microsoft tests unrun
// (#3370) — reached by a second route that #3383 did not close.
//
// GREEN (after the fix): the runner refuses. The value genuinely is not recoverable on this
// path — the compiled DLL carries only the data item's NAME (verified by dumping its string
// table: "BoundedByMaxIteration" is there, "DataItemTableView" and "sorting" are not), and
// both metadata sources are closed by construction — so there is nothing to carry, and a
// synthetic data item cannot represent "unknown" to BC: MaxIteration is an int whose only
// unset value, 0, already means "no limit". Per .claude/rules/loud-failures.md the runner
// says so by name instead of looping to the window's edge.
codeunit 65841 "RSM Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RSM Assert";

    // POSITIVE. The refusal fires, is typed, and NAMES the report — a developer reading the
    // failure can act on it (register the .app that declares 65821) instead of watching a
    // run make steady forward progress forever.
    [Test]
    procedure DataItemLoop_OfReportNoSourceDescribes_IsRefusedByName()
    begin
        asserterror Report.Run(65821, false, false);

        Assert.Contains(GetLastErrorText(), 'out-of-scope: NavReport.Run(Report 65821)',
            'the refusal must name the report the runner could not describe — an unnamed refusal is no more actionable than the hang it replaces');
        Assert.Contains(GetLastErrorText(), 'report-metadata-unavailable',
            'the reason anchor is what tests/expectations/ and a reader both match on');
    end;

    // POSITIVE, and the part that makes the refusal a statement rather than a guess: the
    // report never reached its own OnPostReport, so no data item iterated at all. Before the
    // fix this text WAS present, carrying "maxiter=1051 filter=1051".
    [Test]
    procedure DataItemLoop_OfReportNoSourceDescribes_DoesNotIterateAtAll()
    begin
        asserterror Report.Run(65821, false, false);

        if StrPos(GetLastErrorText(), 'RSM ITERATIONS') > 0 then
            Error('report 65821 ran its data items and reported "%1" — the loop executed against invented metadata instead of being refused', GetLastErrorText());
    end;

    // NEGATIVE (proportionality). A report the runner also cannot describe, but which
    // declares NO data items, has no loop to refuse — so it must still run its lifecycle
    // triggers. Without this, "refuse every report whose metadata is a stub" would satisfy
    // the two tests above.
    [Test]
    procedure DatasetlessReport_NoSourceDescribesItEither_StillRunsItsTriggers()
    begin
        asserterror Report.Run(65822, false, false);

        Assert.Contains(GetLastErrorText(), 'RSM 65822 REACHED ONPOSTREPORT',
            'report 65822 declares no data items, so there is no loop to refuse and its OnPostReport must still run — a refusal here would be the mirror defect, refusing a report the runner can execute perfectly well');
    end;

    // NEGATIVE (scope). The SAME two data-item shapes, source-compiled in this bundle, so
    // BC's own emit captured their metadata. Both declared bounds must be honoured exactly:
    // 1 proves MaxIteration reached BC's loop, and 3 proves nothing was clamped to 1.
    [Test]
    procedure LocalReport_WithRealEmitCapturedMetadata_HonoursBothDeclaredBounds()
    var
        Counter: Record "RSM Counter";
        Row: Record "RSM Row";
        i: Integer;
    begin
        Counter.DeleteAll();
        Row.DeleteAll();
        // Twelve rows: a count distinct from BOTH expected answers below, so neither can be
        // satisfied by the table simply running out of rows.
        for i := 1 to 12 do begin
            Row.Init();
            Row."No." := i;
            Row.Insert();
        end;

        Report.Run(65842, false, false);

        Counter.SetRange("Loop Name", 'MAXITER');
        Assert.AreEqual(1, Counter.Count(),
            'the data item declares MaxIteration = 1 and no restricting filter over twelve rows, so its OnAfterGetRecord must run exactly once — 12 means the bound was lost');

        Counter.SetRange("Loop Name", 'FILTER');
        Assert.AreEqual(3, Counter.Count(),
            'the sibling data item declares where("No." = filter(1 .. 3)) and no MaxIteration, so it must run three times — 1 would mean every data item was clamped to one iteration, and 12 would mean the view was lost');
    end;
}
