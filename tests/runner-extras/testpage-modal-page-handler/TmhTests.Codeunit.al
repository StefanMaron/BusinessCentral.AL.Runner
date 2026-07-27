/// <summary>
/// Pins [ModalPageHandler] dispatch: AL that opens a modal page must be answered by the
/// test codeunit's handler, and the handler's OK/Cancel must reach the calling AL.
///
/// BC routes this through NavTestExecution.FindPageType, which reads
/// form.MasterPage.PageProperties.PageType. NavForm.GetMasterPage is guarded to run only
/// for forms the runner built itself, so a page BC opened on AL's behalf had a NULL
/// MasterPage and every modal page raised a NullReferenceException before its handler was
/// ever looked up. Thirty Pageworks tests declare HandlerFunctions.
///
/// RED: invoking the action NREs in NavTestExecution.FindPageType.
/// GREEN: the handler runs and its answer comes back to the AL that called RunModal.
///
/// The negatives are what stop a shallow fix. A runner that routed to the handler but
/// always reported OK would pass the positive; the Cancel test catches that. And a modal
/// page with NO declared handler must raise BC's own missing-handler error — silently
/// returning a default result is how an unhandled dialog turns a failing test green.
/// </summary>
codeunit 61925 "TMH Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "TMH Assert";

    local procedure SeedRows()
    var
        Row: Record "TMH Row";
    begin
        Row.DeleteAll();

        Row.Init();
        Row."No." := 'A';
        Row.Descr := 'Alpha';
        Row.Insert();
    end;

    // Positive: the handler runs at all, and is handed the modal page.
    [Test]
    [HandlerFunctions('OkHandler')]
    procedure ModalPageHandlerRuns()
    var
        Row: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.PickIt.Invoke();
        Host.Close();

        Assert.IsTrue(Row.Get('HANDLER'), 'the [ModalPageHandler] must have run');
    end;

    // Positive: the handler's OK reaches the AL that called RunModal.
    [Test]
    [HandlerFunctions('OkHandler')]
    procedure ModalPageHandlerOkReachesTheCallingAl()
    var
        Row: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.PickIt.Invoke();
        Host.Close();

        Assert.IsTrue(Row.Get('RESULT'), 'the calling AL must have recorded a RunModal result');
        Assert.AreEqual('OK', Row.Descr, 'RunModal must have returned OK when the handler invoked OK');
    end;

    // Negative: a cancelling handler must NOT read back as OK. A runner that dispatched to
    // the handler but always answered OK would pass the test above and fail this one.
    [Test]
    [HandlerFunctions('CancelHandler')]
    procedure ModalPageHandlerCancelReachesTheCallingAl()
    var
        Row: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.PickIt.Invoke();
        Host.Close();

        Assert.IsTrue(Row.Get('RESULT'), 'the calling AL must have recorded a RunModal result');
        Assert.AreEqual('Cancel', Row.Descr,
            'RunModal must have returned Cancel when the handler cancelled');
    end;

    // Negative: no [HandlerFunctions] at all. BC must refuse rather than let the modal page
    // return a default result unobserved.
    [Test]
    procedure ModalPageWithoutAHandlerIsRefused()
    var
        Row: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        asserterror Host.PickIt.Invoke();
        Assert.ExpectedError('61920');

        Assert.IsFalse(Row.Get('RESULT'),
            'a refused modal page must not have let the calling AL record a result');
    end;

    [ModalPageHandler]
    procedure OkHandler(var Modal: TestPage "TMH Modal")
    var
        Stamp: Record "TMH Row";
    begin
        Stamp.Init();
        Stamp."No." := 'HANDLER';
        Stamp.Descr := 'ran';
        if not Stamp.Insert() then
            Stamp.Modify();
        Modal.OK().Invoke();
    end;

    [ModalPageHandler]
    procedure CancelHandler(var Modal: TestPage "TMH Modal")
    var
        Stamp: Record "TMH Row";
    begin
        Stamp.Init();
        Stamp."No." := 'HANDLER';
        Stamp.Descr := 'ran';
        if not Stamp.Insert() then
            Stamp.Modify();
        Modal.Cancel().Invoke();
    end;
}
