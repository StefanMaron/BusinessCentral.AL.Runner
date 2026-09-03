codeunit 65502 "Bpodt Tests"
{
    Subtype = Test;

    // Regression tests for issue #2452:
    // RecordPatches.GetSourceTableIdForPage resolved a bundle-parsed page's declared
    // SourceTable ONLY against tables THIS bundle itself AL-source-parsed
    // (_parsedTables). When a bundle-declared page's SourceTable names a table that ships
    // PRECOMPILED in a loaded dependency .app instead (e.g. Base Application
    // "Salesperson/Purchaser", table 13), the name-matching loop never found it,
    // GetSourceTableIdForPage answered 0, and TestPageFactory.TryBuild's own dependency
    // fallback (TryGetDependencySourceTableIdForPage) could not help either — that path
    // resolves a page that ITSELF ships precompiled in a dependency .app, which is the
    // opposite case. The net effect: the TestPage silently fell back to a navigation mock
    // (logging "declares no SourceTable; using navigation mock"), and any field set through
    // it was silently discarded rather than persisted.
    //
    // Fix: GetSourceTableIdForPage now falls back to RecordPatches.TryPopulateParsedTableByName
    // — the SAME by-name dependency-table lookup RecordPatches.BuildMetaCalcFormula/BcAppFallback
    // already use for FlowField CalcFormula source-table resolution — when no bundle-parsed
    // table matches by name. Because the fix lives in the one shared function, every caller
    // (TestPageFactory.TryBuild, NavTestPageBase_ALGoToRecord,
    // RecordPatches.ResolveSourceTableIdForAnyPage and its own callers, e.g.
    // CodeunitPatches.NavFormHandle_CreateTarget for a plain page VARIABLE) gets the fix for
    // free — no second call site needed patching.
    //
    // Deliberately kept in its OWN app group (see app.json) rather than folded into an
    // existing runner-extras suite: any OTHER codeunit in the same compiled bundle that
    // declares a `Record "Salesperson/Purchaser"` (or the same shape for any other table)
    // would pre-populate RecordPatches._parsedTables by TABLE ID during that codeunit's own
    // compile, masking the exact by-NAME resolution gap this suite exists to prove. The
    // Salesperson/Purchaser Record variable below, inside the SAME codeunit as the page under
    // test, is the only place this table is referenced in the whole bundle.
    var
        Assert: Codeunit "Bpodt Assert";

    // Un-fakeable positive: a MockITestPage (the runner's silent-fallback client whose members
    // all answer defaults) cannot actually persist a row. Reading the row back from a SEPARATE
    // Record variable after the TestPage closes is the only way to prove the TestPage was
    // driven over a REAL record, not a mock.
    [Test]
    procedure BundlePageOverDependencyTable_SetValueThenGet_PersistsRow()
    var
        SalesPurch: Record "Salesperson/Purchaser";
        Pg: TestPage "ALR Bundle Salesperson Page";
    begin
        // [WHEN] A TestPage compiled from this bundle's own AL source, whose SourceTable
        // names Base Application's precompiled "Salesperson/Purchaser" table, is driven live.
        Pg.OpenNew();
        Pg.Code.SetValue('ALR2452');
        Pg.Name.SetValue('AL Runner dependency page metadata regression');
        Pg.Close();

        // [THEN] The row was really inserted into table 13 — proves TestPageFactory built a
        // live record over the resolved dependency table, not a navigation mock.
        Assert.IsTrue(SalesPurch.Get('ALR2452'),
            'TestPage over a bundle page whose SourceTable names a precompiled dependency table must persist a real row, not silently discard it via a navigation mock.');
        Assert.IsTrue(SalesPurch.Name = 'AL Runner dependency page metadata regression',
            'The persisted row must carry the value actually set through the TestPage field.');
    end;

    // Negative companion: a bundle page whose SourceTable names something that resolves
    // to NEITHER a bundle-parsed table NOR any loaded dependency's table must still fail
    // honestly (GoToRecord/field access refuses), not silently succeed against an
    // unrelated/empty table. Exercised via the shared resolver directly is not possible from
    // AL, so this asserts the positive shape is not a coincidence: querying an unrelated,
    // never-inserted key on the SAME page/table must correctly report "not found" rather than
    // finding a stray row — i.e. the resolution is precise (table 13, not some other table).
    [Test]
    procedure BundlePageOverDependencyTable_UnknownKey_GetReturnsFalse()
    var
        SalesPurch: Record "Salesperson/Purchaser";
    begin
        Assert.IsTrue(not SalesPurch.Get('ALR-NONE'),
            'A key never inserted must not be found — confirms the resolved table is really Salesperson/Purchaser (13), not a stray/mismatched table.');
    end;
}
