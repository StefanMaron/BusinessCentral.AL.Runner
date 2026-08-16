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
    begin
        Row.DeleteAll();
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
