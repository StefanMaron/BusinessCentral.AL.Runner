// Report layout selection BY NAME.
//
// RED (before): the runner discarded the layout NAME entirely. Layout
// resolution was Cecil-rewritten to ReportLayoutSelection.GetLayoutSelections
// -> empty list and TryGetSelectedLayoutOrDefault -> a synthetic "DEFAULT"
// layout whose Format came from the report's own DefaultLayout. The by-name
// path (SetTempLayoutSelectedName -> InvokeSelectReportLayoutCode ->
// ParseAndSelectLayoutFromIDAsync -> GetLayoutByNameAndAppIDAsync) reads the
// "Report Layout List" system virtual table (2000000234), which was empty, so
// every named selection failed with
//   NavNCLReportNoLayoutException: Report N does not have a valid layout.
//
// GREEN (after): 2000000234 is populated from the report's own
// `rendering { layout(Name) { Type; MimeType; ... } }` declarations, captured
// off the AL compiler's ReportLayoutSymbol at emit time, so BC's own by-name
// resolution runs unmodified.
//
// ASSERTION STRENGTH. Two independent, non-trivial observables are used, so a
// registry that simply accepted any name cannot pass:
//   1. The Report Layout List rows are read back through AL and their Name /
//      Layout Format / MIME Type are asserted against the exact values declared
//      in the report's `rendering` block, and an undeclared name must yield NO
//      row.
//   2. Selecting the NON-DEFAULT layout by name must drive the render down a
//      different processor fork than the RDLC default does. Same report, same
//      SaveAs call - only the name differs - and the failure mode changes.
//      That is what proves the NAME decided the layout rather than the report
//      default being used regardless.
codeunit 61873 "RLB Layout By Name Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RLB Assert";
        LayoutOneTok: Label 'RlbLayoutOne', Locked = true;
        LayoutTwoTok: Label 'RlbLayoutTwo', Locked = true;
        UndeclaredTok: Label 'RlbNoSuchLayout', Locked = true;
        NoValidLayoutTok: Label 'does not have a valid layout', Locked = true;

    local procedure SeedSample()
    var
        Sample: Record "RLB Sample";
    begin
        Sample.DeleteAll();
        Sample."Entry No." := 1;
        Sample.Description := 'RLBMARKER-9c8b7a6d';
        Sample.Insert();
    end;

    // Runs SaveAs(Pdf) with the given layout selected by name ('' = no selection,
    // i.e. the report's DefaultRenderingLayout) and returns the error text. Used by
    // the cases that MUST fail.
    local procedure SaveAsPdfErrorText(LayoutName: Text): Text
    var
        ReportLayoutSelection: Record "Report Layout Selection";
        BlobRec: Record "RLB Sample";
        OutStr: OutStream;
    begin
        SeedSample();
        BlobRec."Blob Data".CreateOutStream(OutStr);
        if LayoutName <> '' then
            ReportLayoutSelection.SetTempLayoutSelectedName(CopyStr(LayoutName, 1, 250));
        ClearLastError();
        asserterror Report.SaveAs(Report::"RLB Fixture Report", '', ReportFormat::Pdf, OutStr);
        if LayoutName <> '' then
            ReportLayoutSelection.ClearTempLayoutSelected();
        exit(GetLastErrorText());
    end;

    // ------------------------------------------------------------------
    // 1. The declared layouts must be visible on the real BC surface, with
    //    their real per-layout properties.
    // ------------------------------------------------------------------

    // Positive: both declared layouts are listed, each carrying the Type and
    // MIME type declared in the report's `rendering` block. Asserting the
    // per-layout Format/MIME (which DIFFER between the two rows) means a
    // provider that returned one canned row, or the same row twice, fails.
    [Test]
    procedure ReportLayoutList_ListsBothDeclaredLayoutsWithTheirOwnFormat()
    var
        LayoutList: Record "Report Layout List";
    begin
        LayoutList.SetRange("Report ID", Report::"RLB Fixture Report");
        LayoutList.SetRange(Name, LayoutOneTok);
        Assert.IsTrue(LayoutList.FindFirst(),
            'the RDLC default layout must be listed in Report Layout List');
        Assert.AreEqual(LayoutOneTok, LayoutList.Name, 'layout one Name');
        Assert.IsTrue(LayoutList."Layout Format" = LayoutList."Layout Format"::RDLC,
            StrSubstNo('layout one must report its declared Type = RDLC, got %1', LayoutList."Layout Format"));

        LayoutList.SetRange(Name, LayoutTwoTok);
        Assert.IsTrue(LayoutList.FindFirst(),
            'the non-default Custom layout must be listed in Report Layout List');
        Assert.AreEqual(LayoutTwoTok, LayoutList.Name, 'layout two Name');
        Assert.IsTrue(LayoutList."Layout Format" = LayoutList."Layout Format"::Custom,
            StrSubstNo('layout two must report its declared Type = Custom, got %1', LayoutList."Layout Format"));
        Assert.AreEqual('application/x-rlb-layout', LayoutList."MIME Type",
            'layout two must report its declared MimeType');
        Assert.AreEqual('RLB layout two (custom, non-default)', LayoutList.Caption,
            'layout two must report its declared Caption');
    end;

    // Negative: a name that was never declared must produce NO row. A registry
    // that accepts any name fails here.
    [Test]
    procedure ReportLayoutList_UndeclaredNameHasNoRow()
    var
        LayoutList: Record "Report Layout List";
    begin
        LayoutList.SetRange("Report ID", Report::"RLB Fixture Report");
        LayoutList.SetRange(Name, UndeclaredTok);
        Assert.IsFalse(LayoutList.FindFirst(),
            'an undeclared layout name must not be listed in Report Layout List');
    end;

    // ------------------------------------------------------------------
    // 2. The NAME must decide the layout, observably.
    // ------------------------------------------------------------------

    // Baseline: with no name selected the report renders through its
    // DefaultRenderingLayout (RDLC), which is external rendering and throws the
    // documented out-of-scope reason. This pins the "default" fork so the
    // by-name test below has something to differ from.
    [Test]
    procedure NoSelection_UsesRdlcDefault_ThrowsExternalRendering()
    begin
        Assert.Contains(SaveAsPdfErrorText(''), 'report-rendering-external',
            'with no layout selected the RDLC DefaultRenderingLayout must drive an external render');
    end;

    // Positive, and the strongest observable available: selecting the
    // NON-DEFAULT Custom layout by name must NOT take the RDLC external-render
    // fork the default takes, and must not fail for want of a layout. Only a
    // by-name resolution that really returned RlbLayoutTwo can change the fork.
    [Test]
    procedure SelectByName_NonDefaultCustomLayout_ChangesTheRenderFork()
    var
        ReportLayoutSelection: Record "Report Layout Selection";
        BlobRec: Record "RLB Sample";
        OutStr: OutStream;
    begin
        SeedSample();
        BlobRec."Blob Data".CreateOutStream(OutStr);
        ReportLayoutSelection.SetTempLayoutSelectedName(LayoutTwoTok);
        // The identical call one test above throws report-rendering-external when the
        // report's RDLC default is in force. Selecting the Custom layout BY NAME must
        // take the in-scope custom-merger fork instead, i.e. complete without error.
        Assert.IsTrue(
            Report.SaveAs(Report::"RLB Fixture Report", '', ReportFormat::Pdf, OutStr),
            'SaveAs with the Custom layout selected by name must complete - if the NAME were ignored, the RDLC default would throw report-rendering-external here');
        ReportLayoutSelection.ClearTempLayoutSelected();
    end;

    // Positive, and the observable the fork test above cannot reach: the SELECTED layout's
    // own CONTENT must reach the merger.
    //
    // RED (before): for a report declaring more than one layout, layout hydration was
    // skipped entirely — the rule was "hydrate only when the report declares exactly one
    // layout", on the reasoning that picking one of several might serve the wrong template.
    // A by-name selection therefore resolved the right layout IDENTITY and handed the
    // renderer an EMPTY stream. Every assertion in this suite still passed, because none of
    // them looked at the content; the failure surfaced only in the ISV's own AL as
    // "LF-XML: The template is not well-formed XML: 'Root element is missing.'".
    //
    // GREEN: hydration matches the declared layout by the NAME BC itself put on the
    // ReportLayout instance, so a multi-layout report is served its selected layout's bytes.
    [Test]
    procedure SelectByName_CustomLayoutContentReachesTheMerger()
    var
        ReportLayoutSelection: Record "Report Layout Selection";
        BlobRec: Record "RLB Sample";
        Capture: Codeunit "RLB Merger Capture";
        OutStr: OutStream;
    begin
        SeedSample();
        Capture.ClearCapture();
        BlobRec."Blob Data".CreateOutStream(OutStr);

        ReportLayoutSelection.SetTempLayoutSelectedName(LayoutTwoTok);
        Report.SaveAs(Report::"RLB Fixture Report", '', ReportFormat::Pdf, OutStr);
        ReportLayoutSelection.ClearTempLayoutSelected();

        // RlbLayoutTwo.rlblayout's entire content. Asserting the actual body — not merely
        // that something non-empty arrived — is what distinguishes "the selected layout's
        // bytes" from "some default template".
        Assert.Contains(Capture.CapturedTemplate(), 'RLB-CUSTOM-LAYOUT-BODY',
            'the merger must receive the by-name-selected Custom layout''s own content; an empty template here is the multi-layout hydration gap');
    end;

    // Negative: an undeclared name must still fail loudly with BC's own
    // no-valid-layout error. This is the guard against "resolve to the default
    // whenever the name is not found", which would make the positive test above
    // pass for the wrong reason.
    [Test]
    procedure SelectByName_UndeclaredName_StillFailsLoudly()
    begin
        Assert.Contains(SaveAsPdfErrorText(UndeclaredTok), NoValidLayoutTok,
            'an undeclared layout name must still fail with BC''s no-valid-layout error');
    end;
}
