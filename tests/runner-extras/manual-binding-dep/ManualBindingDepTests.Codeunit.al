codeunit 62211 "Manual Binding Dep Tests"
{
    // Regression for issue #1729. With Tests-TestLibraries declared as a dependency, the
    // dep closure contains codeunit 132513 "Confirm Test Library" (System Application
    // Test Library): EventSubscriberInstance = Manual, subscribed to Confirm Management
    // Impl.OnBeforeGuiAllowed, unbound default state GuiAllowed=false + Handled=true.
    // A runner that dispatches Manual subscribers without a BindSubscription would let
    // that unbound default flip IsGuiAllowed() to false, so GetResponseOrDefault returns
    // its default WITHOUT raising the confirm and the [ConfirmHandler] never fires.
    Subtype = Test;

    var
        ConfirmWasRaised: Boolean;

    [Test]
    [HandlerFunctions('YesHandler')]
    procedure TriggerConfirm_WithTestLibrariesDep_ReachesHandler()
    var
        Row: Record "Manual Bind Guarded Header";
    begin
        ConfirmWasRaised := false;
        Row.Init();
        Row."No." := 'INST';
        Row.Template := false;
        Row."Template Used" := 'TMPL';
        Row.Insert(true);

        Row.Validate(Template, true);

        if not ConfirmWasRaised then
            Error('The [ConfirmHandler] never fired: GetResponseOrDefault returned the default instead of dispatching the confirm (issue #1729 regressed).');
        if not Row.Template then
            Error('Handler replied Yes but Template was reverted - the reply did not reach the trigger.');
    end;

    [Test]
    [HandlerFunctions('YesHandler')]
    procedure DirectGetResponseOrDefault_WithTestLibrariesDep_ReachesHandler()
    var
        ConfirmManagement: Codeunit "Confirm Management";
    begin
        ConfirmWasRaised := false;
        if not ConfirmManagement.GetResponseOrDefault('Direct?', false) then
            Error('GetResponseOrDefault returned the default: the unbound Manual subscriber 132513 answered OnBeforeGuiAllowed (issue #1729 regressed).');
        if not ConfirmWasRaised then
            Error('GetResponseOrDefault returned true but the [ConfirmHandler] never ran.');
    end;

    [Test]
    [HandlerFunctions('YesHandler')]
    procedure DirectConfirm_WithTestLibrariesDep_ReachesHandler()
    begin
        ConfirmWasRaised := false;
        if not Confirm('Proceed?', false) then
            Error('Direct Confirm() returned false - the [ConfirmHandler] did not answer.');
        if not ConfirmWasRaised then
            Error('Direct Confirm() returned true but the handler never ran.');
    end;

    [ConfirmHandler]
    procedure YesHandler(Question: Text[1024]; var Reply: Boolean)
    begin
        ConfirmWasRaised := true;
        Reply := true;
    end;
}
