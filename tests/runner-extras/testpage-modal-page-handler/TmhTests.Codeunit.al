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

    // Negative: no [HandlerFunctions] at all. The modal page must be refused, and the AL that
    // called RunModal must never see a result — a modal page that quietly returned OK with no
    // handler would make an unattended dialog look like a confirmed one.
    //
    // KNOWN GAP in the message. Real BC raises "the following UI handlers were not found"
    // naming page 61920, from NavTestExecution.FindHandler — but that throw is guarded on
    // `executingTestRunner != null`, and the runner has no test-runner codeunit. So BC falls
    // through to the client-callback path, where HeadlessClientCallback raises the right
    // page-naming error and NavForm.RunModalAsync's `catch { UnregisterForm(this); throw; }`
    // then throws OVER it: Session.ClientCallback fails BEFORE RegisterForm ran, so the
    // unregister finds nothing. The assertion below therefore pins the refusal and its
    // consequence, not the wording. Fixing the wording means giving NavTestExecution a real
    // executingTestRunner, which also drives CommitTestCodeunits/CommitTestFunctions and so
    // is not a change to make blind.
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
        Assert.ExpectedError('has not been registered');

        Assert.IsFalse(Row.Get('RESULT'),
            'a refused modal page must not have let the calling AL record a result');
    end;

    // Positive: a modal opened as a LOOKUP must close with LookupOK, so AL gated on
    // `RunModal() <> Action::LookupOK` takes the accept branch and the trigger's `var Text`
    // reaches the field.
    //
    // NavTestPageBase.GetBuiltInAction(OK) is FindBuiltInAction(FormResult.OK,
    // FormResult.LookupOK): it asks the client for OK and only tries LookupOK when the
    // client answers NULL. The runner answered every result with an action, so OK always
    // matched, a lookup page closed as plain OK, and the AL above took the `exit(false)`
    // branch even though the handler had invoked OK — the field stayed blank with no error
    // anywhere. Six Pageworks tests failed exactly this way.
    [Test]
    [HandlerFunctions('OkHandler')]
    procedure LookupModeModalClosesWithLookupOkAndValueReachesTheField()
    var
        Row: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.Picked.Lookup();

        Assert.IsTrue(Row.Get('HANDLER'), 'the [ModalPageHandler] must have run for a lookup-mode modal');
        Assert.AreEqual('PICKED', Host.Picked.Value(),
            'the OnLookup trigger''s var Text must reach the field, which only happens when RunModal reported LookupOK');
        Host.Close();
    end;

    // Negative: a cancelling handler must NOT read back as LookupOK. Without this, mapping
    // every close to LookupOK would pass the test above — the mirror of the bug it fixes.
    [Test]
    [HandlerFunctions('CancelHandler')]
    procedure LookupModeModalCancelLeavesTheFieldUnchanged()
    var
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.Picked.Lookup();

        Assert.AreEqual('', Host.Picked.Value(),
            'a cancelled lookup must leave the field untouched — the OnLookup returned false, so its var Text is discarded');
        Host.Close();
    end;

    // Positive: a handler can drive a control bound to the modal page's own VARIABLE.
    //
    // A page AL opens with RunModal is not the instance the runner constructed, so it never
    // got BC's real form initialisation — and RegisterSourceExpression is the step that
    // publishes a page's control -> value bindings. Without it the binding table is empty
    // and every page-variable control on a modal page is unresolvable, which is how
    // Pageworks' InsertPicker "KindSelector" surfaced as an out-of-scope control id.
    [Test]
    [HandlerFunctions('VarsHandler')]
    procedure ModalPageHandler_CanDriveAPageVariableControl()
    var
        Echo: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.PickWithVars.Invoke();
        Host.Close();

        Assert.IsTrue(Echo.Get('MODE'),
            'the handler set the page-variable control, so the page''s OnValidate must have run');
        Assert.AreEqual('Blocks', Echo.Descr,
            'the OnValidate must see the value the handler wrote to the page variable');
    end;

    // Negative: the Rec-bound control on the same modal page must keep working. It resolves
    // through the record and never needed the binding table, so a fix aimed at page
    // variables must not disturb it.
    //
    // The handler positions the cursor itself rather than relying on the page opening on a
    // row. Whether a modal page arrives pre-positioned is a separate question from whether
    // its Rec-bound controls resolve, and this test is about the second one.
    [Test]
    [HandlerFunctions('VarsHandler')]
    procedure ModalPageHandler_RecBoundControlOnTheSamePageStillResolves()
    var
        Echo: Record "TMH Row";
        Host: TestPage "TMH Host";
    begin
        SeedRows();

        Host.OpenEdit();
        Host.First();
        Host.PickWithVars.Invoke();
        Host.Close();

        Assert.IsTrue(Echo.Get('RECBOUND'),
            'the handler read the Rec-bound control on the modal page');
        Assert.AreEqual('Alpha', Echo.Descr,
            'the Rec-bound control must read the current row''s value');
    end;

    [ModalPageHandler]
    procedure VarsHandler(var Modal: TestPage "TMH Modal Vars")
    var
        Stamp: Record "TMH Row";
    begin
        Modal.Mode.SetValue('Blocks');

        Modal.First();
        Stamp.Init();
        Stamp."No." := 'RECBOUND';
        Stamp.Descr := CopyStr(Modal.Descr.Value(), 1, MaxStrLen(Stamp.Descr));
        if not Stamp.Insert() then
            Stamp.Modify();

        Modal.OK().Invoke();
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
