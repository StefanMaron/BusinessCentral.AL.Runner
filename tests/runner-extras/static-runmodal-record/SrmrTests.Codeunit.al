codeunit 64502 "Srmr Tests"
{
    // Regression for issue #1897. Real BC's NCLMetaForm.CreateObjectInstance(NavRecord)
    // constructs the page via `base.ApplicationObjectConstructor` — a delegate the runner
    // forces null for EVERY object type (same story as the sibling NCLMetaXmlPort/
    // NCLMetaQuery CreateObjectInstance fixes: RecordPatches.CreateObjectInstance.cs /
    // XmlPortPatches.cs). The AL-page-VARIABLE form of RunModal
    // (`P: Page "Srmr Modal"; P.SetRecord(Rec); P.RunModal();`) never reaches this method —
    // NavFormHandle.CreateTarget already has its own working construction path for that
    // case — so only the STATIC-by-id form (Page.RunModal(id, Record), and transitively
    // Base App Codeunit 700 "Page Management".PageRunModal/PageRun) is affected. Before the
    // fix this NREs at:
    //
    //   NCLMetaApplicationObject.get_ApplicationObjectLegacyConstructor()
    //   NCLMetaForm.CreateObjectInstance(NavRecord record)
    //   NavForm.RunModalAsync(bool isInLookupTrigger, bool isLookup, int formId, NavRecord record, int fieldNo)
    //
    // The 2-arg (PageId, Record) overload runs in LOOKUP mode (verified against real BC and
    // reflected here): the handler's OK/Cancel reads back as LookupOK/LookupCancel, not
    // OK/Cancel.
    Subtype = Test;

    local procedure Initialize()
    var
        Row: Record "Srmr Row";
        LookupRow: Record "Srmr Lookup Row";
    begin
        Row.DeleteAll();
        LookupRow.DeleteAll();
    end;

    // Positive: the static Page.RunModal(id, Record) form reaches the [ModalPageHandler]
    // (no NRE), and the handler's OK reaches the calling AL as LookupOK.
    [Test]
    [HandlerFunctions('OkHandler')]
    procedure StaticRunModal_ExplicitId_HandlerRunsAndReturnsLookupOk()
    var
        Row: Record "Srmr Row";
        Result: Action;
    begin
        Initialize();
        Row.Init();
        Row."No." := 'A';
        Row.Descr := 'Alpha';
        Row.Insert();

        Result := Page.RunModal(Page::"Srmr Modal", Row);

        if not Row.Get('HANDLER') then
            Error('the [ModalPageHandler] must have run for the static Page.RunModal(id, Record) form');
        if Format(Result) <> Format(Action::LookupOK) then
            Error('Page.RunModal(id, Record) must return the handler''s OK as LookupOK (lookup-mode overload), got %1', Format(Result));
    end;

    // Negative: a cancelling handler must NOT read back as LookupOK. Without this, a fix
    // that always reported success (e.g. mapping every construction success straight to
    // LookupOK) would pass the positive test above and hide the same bug in reverse.
    [Test]
    [HandlerFunctions('CancelHandler')]
    procedure StaticRunModal_ExplicitId_CancelReturnsLookupCancel()
    var
        Row: Record "Srmr Row";
        Result: Action;
    begin
        Initialize();
        Row.Init();
        Row."No." := 'B';
        Row.Descr := 'Bravo';
        Row.Insert();

        Result := Page.RunModal(Page::"Srmr Modal", Row);

        if not Row.Get('HANDLER') then
            Error('the [ModalPageHandler] must have run for the static Page.RunModal(id, Record) form');
        if Format(Result) <> Format(Action::LookupCancel) then
            Error('Page.RunModal(id, Record) must return the handler''s Cancel as LookupCancel, not LookupOK, got %1', Format(Result));
    end;

    // Sibling proof: the AL-page-VARIABLE form must keep working exactly as before this
    // fix — construction for that path goes through NavFormHandle.CreateTarget, a
    // different, already-working mechanism this change does not touch.
    [Test]
    [HandlerFunctions('OkHandler')]
    procedure InstanceRunModal_SetRecord_StillDispatchesHandler()
    var
        Row: Record "Srmr Row";
        Modal: Page "Srmr Modal";
    begin
        Initialize();
        Row.Init();
        Row."No." := 'C';
        Row.Descr := 'Charlie';
        Row.Insert();

        Modal.SetRecord(Row);
        Modal.RunModal();

        if not Row.Get('HANDLER') then
            Error('the [ModalPageHandler] must have run for the AL-page-variable RunModal form');
    end;

    // Regression for issue #1918 (split out of #1897): real BC's static
    // NavForm.RunModalAsync(bool, bool, int formId, NavRecord record, int) resolves
    // formId==0 from the record's LookupFormId:
    //
    //   if (formId == 0 && record != null) { formId = record.LookupFormId; }
    //
    // and NavRecord.LookupFormId reads MetaTable.LookupFormId, which real BC populates
    // from the table's LookupPageId property. Before the fix the runner's synthesized
    // NCLMetaTable never populated LookupFormId (always 0), so Page.RunModal(0, Row)
    // threw "You tried to invoke the Page object with the ID 0 ..." instead of
    // resolving Page::"Srmr Lookup List" from "Srmr Lookup Row".LookupPageId. This is
    // separate from #1897/#1919's NCLMetaForm.CreateObjectInstance NRE: it fails BEFORE
    // that method is ever reached, while formId is still (wrongly) 0.
    [Test]
    [HandlerFunctions('LookupListHandler')]
    procedure StaticRunModal_ById0_ResolvesPageFromTableLookupPageId()
    var
        Row: Record "Srmr Row";
        LookupRow: Record "Srmr Lookup Row";
        Result: Action;
    begin
        Initialize();
        LookupRow.Init();
        LookupRow."No." := 'A';
        LookupRow.Insert();

        Result := Page.RunModal(0, LookupRow);

        if not Row.Get('HANDLER') then
            Error('the [ModalPageHandler] for the table''s LookupPageId-resolved page must have run for Page.RunModal(0, Record)');
        if Format(Result) <> Format(Action::LookupOK) then
            Error('Page.RunModal(0, Record) must resolve the page via LookupPageId and return LookupOK, got %1', Format(Result));
    end;

    // Negative: a table declaring NO LookupPageId must still fail loudly on
    // Page.RunModal(0, Row) rather than silently resolving to some other page (e.g. the
    // first page the runner happens to find, or the last one built). Without this, a fix
    // that made every id-0 static RunModal succeed regardless of the record's own table
    // metadata would pass the positive test above and hide the same bug in reverse.
    // Real BC surfaces this failure as one of two distinct texts depending on internal
    // dispatch state — "You tried to invoke the Page object with the ID 0 ..." (the same
    // text the issue reported) or "The metadata object Page 0 was not found. ..." — both
    // NavMetadataNotFoundException-rooted "no such Page 0" diagnoses, just phrased
    // differently by whichever BC code path unwinds the failure. Either is accepted; what
    // must NOT happen is the call succeeding.
    [Test]
    procedure StaticRunModal_ById0_NoLookupPageId_FailsLoudly()
    var
        Row: Record "Srmr Row";
        ErrorText: Text;
    begin
        Initialize();
        Row.Init();
        Row."No." := 'D';
        Row.Descr := 'Delta';
        Row.Insert();

        asserterror Page.RunModal(0, Row);

        ErrorText := GetLastErrorText();
        if (StrPos(ErrorText, 'ID 0') = 0) and (StrPos(ErrorText, 'Page 0') = 0) then
            Error('Page.RunModal(0, Record) on a table declaring no LookupPageId must still fail ' +
                  'as "no such Page 0" rather than silently resolving to a page, got: %1', ErrorText);
    end;

    [ModalPageHandler]
    procedure LookupListHandler(var Modal: TestPage "Srmr Lookup List")
    var
        Stamp: Record "Srmr Row";
    begin
        Stamp.Init();
        Stamp."No." := 'HANDLER';
        Stamp.Descr := 'ran';
        if not Stamp.Insert() then
            Stamp.Modify();
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure OkHandler(var Modal: TestPage "Srmr Modal")
    var
        Stamp: Record "Srmr Row";
    begin
        Stamp.Init();
        Stamp."No." := 'HANDLER';
        Stamp.Descr := 'ran';
        if not Stamp.Insert() then
            Stamp.Modify();
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure CancelHandler(var Modal: TestPage "Srmr Modal")
    var
        Stamp: Record "Srmr Row";
    begin
        Stamp.Init();
        Stamp."No." := 'HANDLER';
        Stamp.Descr := 'ran';
        if not Stamp.Insert() then
            Stamp.Modify();
        Modal.Cancel().Invoke();
    end;
}
