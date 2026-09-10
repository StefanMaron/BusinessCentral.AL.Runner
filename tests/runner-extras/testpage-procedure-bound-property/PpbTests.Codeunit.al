/// Issue #3731 -- a page property bound to a procedure call.
///
/// `Enabled = IsAllowed()` compiles with warning AL0573 ("Procedure calls is not valid for client
/// expressions ... This warning will become an error in a future release"), and real BC never
/// evaluates the expression: on BC 28.4.53241.0 (onprem w1, container, test toolkit) the action
/// reads Enabled = false and its OnAction is skipped, though IsAllowed() returns true
/// unconditionally. The runner used to resolve the call and answer true, so a test asserting the
/// action is enabled passed here and failed on a real tier.
///
/// WHY THIS IS NOT A CORPUS TEST. AL0573 says the warning becomes an error in a future release,
/// so a corpus codeunit carrying this shape is one BC minor away from failing the corpus app's
/// compile on every leg. That is a structural reason, not convenience: the claim is about BC, but
/// the corpus cannot be made to carry the AL that states it.
///
/// The one arm a service tier adjudicated is an ACTION's Enabled. A control's Enabled and an
/// action's Visible bound to a procedure call are unmeasured, so the runner refuses them loudly
/// instead of extrapolating (.claude/rules/loud-failures.md), and those refusals are pinned below
/// so a later measurement has to come back through this file.
codeunit 65914 "Ppb Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Ppb Assert";

    local procedure SeedRow()
    var
        Row: Record "Ppb Row";
        Trace: Record "Ppb Trace";
    begin
        Row.DeleteAll();
        Trace.DeleteAll();
        Row.Init();
        Row."No." := 'R1';
        Row.Flag := true;
        Row.Insert();
    end;

    [Test]
    procedure ActionEnabledBoundToProcedure_AnswersFalse()
    var
        Card: TestPage "Ppb Card";
    begin
        SeedRow();
        Card.OpenEdit();
        // IsAllowed() returns true unconditionally. BC answers false because it never calls it.
        Assert.IsFalse(Card.AProcEnabled.Enabled(),
            'an action whose Enabled is bound to a procedure call reads false on real BC (AL0573)');
        Card.Close();
    end;

    [Test]
    procedure ActionEnabledBoundToProcedure_InvokeSkipsTheTrigger()
    var
        Card: TestPage "Ppb Card";
        Trace: Record "Ppb Trace";
    begin
        SeedRow();
        Card.OpenEdit();
        Card.AProcEnabled.Invoke();
        Card.Close();
        Assert.IsFalse(Trace.Ran('PROC-ENABLED'),
            'the OnAction of a procedure-bound-Enabled action must not run: BC skips it');
    end;

    [Test]
    procedure ActionEnabledLiteralTrue_StillInvokes()
    var
        Card: TestPage "Ppb Card";
        Trace: Record "Ppb Trace";
    begin
        SeedRow();
        Card.OpenEdit();
        Assert.IsTrue(Card.ALiteralTrue.Enabled(), 'Enabled = true still reads true');
        Card.ALiteralTrue.Invoke();
        Card.Close();
        // The control arm: without it, an implementation that answered false for EVERY action
        // would pass the two tests above.
        Assert.IsTrue(Trace.Ran('LITERAL-TRUE'),
            'an action with Enabled = true must still dispatch its OnAction');
    end;

    [Test]
    procedure ActionWithNoEnabledDeclared_StillInvokes()
    var
        Card: TestPage "Ppb Card";
        Trace: Record "Ppb Trace";
    begin
        // The discriminator this fix rests on: an action declaring NO Enabled publishes the AL
        // default as a literal ("true"), while one whose expression the compiler dropped
        // publishes the empty string. If the two ever became the same value, this test goes red
        // rather than the fix silently disabling every action on every page.
        SeedRow();
        Card.OpenEdit();
        Assert.IsTrue(Card.ANoProp.Enabled(), 'an action declaring no Enabled reads true');
        Card.ANoProp.Invoke();
        Card.Close();
        Assert.IsTrue(Trace.Ran('NO-PROP'), 'an action declaring no Enabled must still dispatch');
    end;

    [Test]
    procedure ActionEnabledBoundToASourceTableField_StillFollowsTheRow()
    var
        Card: TestPage "Ppb Card";
        Trace: Record "Ppb Trace";
    begin
        // #3730's live-expression path, unchanged by this fix: Flag is true on the seeded row.
        SeedRow();
        Card.OpenEdit();
        Assert.IsTrue(Card.ARecFlag.Enabled(), 'Enabled = Rec.Flag reads the live row');
        Card.ARecFlag.Invoke();
        Card.Close();
        Assert.IsTrue(Trace.Ran('REC-FLAG'), 'Enabled = Rec.Flag on a true row must dispatch');
    end;

    [Test]
    procedure ActionVisibleBoundToProcedure_RefusesBecauseBcIsUnmeasured()
    var
        Card: TestPage "Ppb Card";
        Answered: Boolean;
    begin
        SeedRow();
        Card.OpenEdit();
        asserterror Answered := Card.AProcVisible.Visible();
        Assert.ExpectedError('out-of-scope:',
            'an action Visible bound to a procedure call must refuse: no service tier measured it');
        Assert.ExpectedError('AL0573',
            'the refusal names the diagnostic that explains why the expression is never evaluated');
    end;
}
