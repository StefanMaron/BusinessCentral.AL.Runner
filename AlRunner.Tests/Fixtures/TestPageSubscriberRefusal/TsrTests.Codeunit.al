codeunit 70604 "TSR Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "TSR Assert";
        Guard: Codeunit "TSR Guard";

    local procedure Seed(No: Code[20]; Guarded: Boolean)
    var
        Row: Record "TSR Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := No;
        Row.Guarded := Guarded;
        Row.Insert();
        // Committed on purpose. `asserterror` unwinds to the last commit, so without this the
        // row the refusal was supposed to SAVE would be gone for a reason that has nothing to
        // do with the refusal, and the assertion below would pass against an implementation
        // that deleted the row and then rolled back.
        Commit();
    end;

    // THE CLAIM. The subscriber's Error travels back up through Delete(true), through the
    // control's OnValidate, through the page-global control write, and out of SetValue where
    // the caller's asserterror can trap it.
    [Test]
    procedure SubscriberRefusal_ReachesTheAssertErrorAroundSetValue()
    var
        Pg: TestPage "TSR Page";
        Row: Record "TSR Row";
    begin
        Seed('GUARDED', true);

        Pg.OpenEdit();
        asserterror Pg.PurgeAll.SetValue(true);

        // BC wraps the recorded text; the subscriber's own message is inside it.
        Assert.IsTrue(
            StrPos(GetLastErrorText(), Guard.ExpectedMessage('GUARDED')) > 0,
            'the trapped error must carry the subscriber''s own message, got: ' + GetLastErrorText());

        // And the delete it refused did not happen.
        Assert.AreEqual(1, Row.Count(), 'the refused row must still be there');
    end;

    // The ledger, read AFTER the asserterror swallowed the exception — the half Microsoft's
    // Codeunit134614.TestRemoveSUPERPermissionsByUserAll asserts and the half issue #3105
    // reported as never reached.
    [Test]
    procedure SubscriberRefusal_RecordsExactlyOneValidationError()
    var
        Pg: TestPage "TSR Page";
    begin
        Seed('GUARDED', true);

        Pg.OpenEdit();
        asserterror Pg.PurgeAll.SetValue(true);

        Assert.AreEqual(1, Pg.PurgeAll.ValidationErrorCount(),
            'a refusal raised below the control write must be recorded once on the control');
        Assert.AreEqual(Guard.ExpectedMessage('GUARDED'), Pg.PurgeAll.GetValidationError(1),
            'a page-global control stages no row edit, so the stored text is the bare message');
    end;

    // THE MIRROR. A row the subscriber does not refuse goes through, the delete happens, and
    // nothing is recorded — so "make every SetValue throw" cannot satisfy the arms above.
    [Test]
    procedure UnguardedRow_IsDeletedAndRecordsNoValidationError()
    var
        Pg: TestPage "TSR Page";
        Row: Record "TSR Row";
    begin
        Seed('OPEN', false);

        Pg.OpenEdit();
        Pg.PurgeAll.SetValue(true);

        Assert.AreEqual(0, Pg.PurgeAll.ValidationErrorCount(),
            'an accepted write must record no validation error');
        Assert.AreEqual('Yes', Pg.PurgeAll.Value(),
            'the accepted value must be readable back from the page-global control');
        Assert.AreEqual(0, Row.Count(), 'the accepted write must have performed the delete');
    end;
}
