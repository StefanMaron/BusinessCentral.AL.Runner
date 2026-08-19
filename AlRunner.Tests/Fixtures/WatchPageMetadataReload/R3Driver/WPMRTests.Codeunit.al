// EDIT-MARKER-V1
// WatchPageMetadataReloadTests appends a trailing comment-only edit below this
// marker between cycles, to reproduce #1957 exactly: the edit lands in THIS app,
// never in R3Pages, and the page's app is still reported "unchanged — reusing
// the loaded module" while its OnOpenPage trigger regresses.
codeunit 70025 "WPMR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "WPMR Assert";

    [Test]
    procedure OpeningThePageRunsItsOnOpenPageTrigger()
    var
        Row: Record "WPMR Row";
        List: TestPage "WPMR List";
    begin
        // [GIVEN] a row named 'MARK', not yet touched
        Row.DeleteAll();
        Row.Init();
        Row."Code" := 'MARK';
        Row.Insert(true);

        // [WHEN] the page is opened (and closed) — OnOpenPage should mark it
        List.OpenView();
        List.Close();

        // [THEN] the concrete effect OnOpenPage produces actually happened.
        // NOT "OpenView() didn't throw" — a silent record-only fallback also
        // doesn't throw, which is exactly how this bug stayed invisible (#1957).
        Row.Get('MARK');
        Assert.IsTrue(Row.Touched,
            'OnOpenPage did not run — the page opened as a record-only fallback '
            + '(the runner silently downgraded it instead of running the real page object)');
    end;

    // Negative direction (#1957's own repro): OnOpenPage must touch ONLY the row
    // it names. A trigger that never ran at all would ALSO satisfy this trivially
    // — which is why this test alone stayed green throughout the original bug and
    // must never be read as sufficient evidence on its own.
    [Test]
    procedure OnOpenPageTouchesOnlyTheRowItNames()
    var
        Row: Record "WPMR Row";
        List: TestPage "WPMR List";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."Code" := 'MARK';
        Row.Insert(true);
        Row.Init();
        Row."Code" := 'OTHER';
        Row.Insert(true);

        List.OpenView();
        List.Close();

        Row.Get('OTHER');
        Assert.IsFalse(Row.Touched, 'OnOpenPage touched a row it never named');
    end;
}
