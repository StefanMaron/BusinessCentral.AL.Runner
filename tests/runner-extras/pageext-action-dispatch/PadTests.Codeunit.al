/// Regression suite for #1923: TestPage Invoke() of a pageextension-contributed action never
/// dispatched its OnAction trigger.
///
/// RED (without the fix):
///   - DirectActionRuns:              passes (control arm, unaffected)
///   - ExtActionOnOwnPageRuns:        throws RunnerOutOfScopeException naming
///                                    "testpage-action — the page declares no OnAction
///                                    trigger for this action ..." for an action that
///                                    plainly declares one, in the pageextension
///   - ExtActionOnBaseAppPageRuns:    Invoke() raises nothing at all (silent no-op); only
///                                    this test's own Assert.IsTrue catches that the row was
///                                    never logged
///
/// GREEN (with the fix): all three log their own tag, and only their own tag — proving each
/// arm's OnAction genuinely ran, not merely that Invoke() returned without throwing.
codeunit 64524 "Pad Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Pad Assert";

    local procedure Initialize()
    var
        Row: Record "Pad Row";
    begin
        Row.DeleteAll();
    end;

    // Control: an action declared directly on the page dispatches. If this fails, the
    // other two tests' failures would be meaningless (broken plumbing, not #1923).
    [Test]
    procedure DirectActionRuns()
    var
        Row: Record "Pad Row";
        HostPage: TestPage "Pad Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.DirectAction.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('DIRECT'), 'Invoke() must have run the page''s own OnAction trigger');
    end;

    // Positive, arm 2: a pageextension's action on a page compiled from SOURCE in this
    // bundle must dispatch its OWN OnAction — the trigger body lives on the extension's
    // compiled type, not the base page's, and its member id hashes from the EXTENSION's
    // object id (64522), not the page's (64521). A stub that fell back to "does nothing"
    // would leave EXT-OWN-PAGE unlogged; a stub that matched the wrong trigger by name
    // prefix would still fail DirectActionOnlyLogsItsOwnTag below.
    [Test]
    procedure ExtActionOnOwnPageRuns()
    var
        Row: Record "Pad Row";
        HostPage: TestPage "Pad Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.ExtActionOnOwnPage.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('EXT-OWN-PAGE'),
            'Invoke() must have run the pageextension''s OnAction trigger for an action added to a source-compiled page');
    end;

    // Positive, arm 3 — the dangerous half: a pageextension's action on a page that ships
    // PRECOMPILED inside Base Application (Item Attributes) must dispatch its OnAction too.
    // Before the fix this raised NOTHING at Invoke() time; only this assert on the concrete
    // effect (the logged row) catches the miss, exactly like it would silently do on real AL
    // test code whose first step is such an action.
    [Test]
    procedure ExtActionOnBaseAppPageRuns()
    var
        Row: Record "Pad Row";
        ItemAttrPage: TestPage "Item Attributes";
    begin
        Initialize();

        ItemAttrPage.OpenEdit();
        ItemAttrPage.ExtActionOnBaseAppPage.Invoke();
        ItemAttrPage.Close();

        Assert.IsTrue(Row.Get('EXT-BASEAPP-PAGE'),
            'Invoke() must have run the pageextension''s OnAction trigger for an action added to a precompiled Base App page');
    end;

    // Negative / isolation: invoking one arm must not run either of the other two triggers.
    // A dispatcher that resolved the FIRST OnAction-suffixed method it found on the base
    // page type (rather than the one matching this specific member id) would pass the three
    // positives above by coincidence while actually always running the same trigger; this
    // test catches that.
    [Test]
    procedure ExtActionOnOwnPageRunsOnlyItsOwnTrigger()
    var
        Row: Record "Pad Row";
        HostPage: TestPage "Pad Host Page";
    begin
        Initialize();

        HostPage.OpenEdit();
        HostPage.ExtActionOnOwnPage.Invoke();
        HostPage.Close();

        Assert.IsTrue(Row.Get('EXT-OWN-PAGE'), 'the invoked action must have run');
        Assert.IsFalse(Row.Get('DIRECT'), 'invoking the extension action must not run the page''s own action');
    end;
}
