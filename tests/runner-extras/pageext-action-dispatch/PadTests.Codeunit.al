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
///
/// CORRECTION, measured 2026-08-30 (#2113): the third RED line above is stale. Page 7500
/// resolves a live RunnerPageInstance today, so that arm goes through RaiseOnAction and an
/// unresolvable action there THROWS rather than no-opping. See PadItemAttrExt.PageExt.al for
/// the measurement. The assertion below is unchanged and still correct — only the description
/// of which failure mode it guards against was.
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

    // Two distinct rows so a test can tell "the trigger read A CURRENT ROW" apart from
    // "the trigger read THE row the page is actually positioned on" — a stub that always
    // resolved to the first row, or to a blank record, could not pass both
    // ExtActionReadsRecAgainstFirstRow and ExtActionReadsRecAgainstThePagesCurrentRow below.
    local procedure SeedRows()
    var
        Row: Record "Pad Row";
    begin
        Row.Init();
        Row."No." := 'A';
        Row.Insert();

        Row.Init();
        Row."No." := 'B';
        Row.Insert();
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

    // Positive, arm 3: a pageextension's action on a page that ships PRECOMPILED inside Base
    // Application (Item Attributes) must dispatch its OnAction too — dispatch must not quietly
    // depend on the base page having been compiled from source in this bundle. Asserting the
    // concrete effect (the logged row) rather than "Invoke() did not throw" is still the right
    // shape, but see the CORRECTION in this file's header: as of 2026-08-30 this arm no longer
    // exercises the silent-no-op path it was written for.
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

    // Issue #1966: RED before the fix — get_Rec() NREs inside the extension's own trigger
    // body ("Object reference not set to an instance of an object", at
    // PageExtension{extId}.get_Rec()) the instant it reads Rec, so Invoke() never returns.
    // A stub implementation that swallowed the NRE and left Stamp.Descr blank would also
    // fail this: the assert is against a SPECIFIC row value ('A'), not merely "no exception".
    [Test]
    procedure ExtActionReadsRecAgainstFirstRow()
    var
        Stamp: Record "Pad Row";
        HostPage: TestPage "Pad Host Page";
    begin
        Initialize();
        SeedRows();

        HostPage.OpenEdit();
        HostPage.First();
        HostPage.ExtActionReadsRec.Invoke();
        HostPage.Close();

        Assert.IsTrue(Stamp.Get('REC-STAMP'),
            'Invoke() must have run the pageextension''s OnAction trigger that reads Rec');
        Assert.AreEqual('A', Stamp.Descr,
            'the extension action''s OnAction must have read Rec as the page''s current (first) row');
    end;

    // Positive, the actual claim in #1966: moving the page's cursor changes what Rec reads
    // inside the SAME extension trigger — proves Rec is wired to the page's LIVE cursor, not
    // a snapshot frozen at extension-construction time. If the fix instead hard-wired Rec to
    // whatever row was current when the runner lazily built the extension instance, this
    // would still read 'A' here and the test would catch it.
    [Test]
    procedure ExtActionReadsRecAgainstThePagesCurrentRow()
    var
        Stamp: Record "Pad Row";
        HostPage: TestPage "Pad Host Page";
    begin
        Initialize();
        SeedRows();

        HostPage.OpenEdit();
        HostPage.First();
        HostPage.Next();
        HostPage.ExtActionReadsRec.Invoke();
        HostPage.Close();

        Assert.IsTrue(Stamp.Get('REC-STAMP'), 'the extension action must have run');
        Assert.AreEqual('B', Stamp.Descr,
            'the extension action''s OnAction must have seen the row the page is positioned on, via Rec');
    end;
}
