codeunit 82051 "SUP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SUP Src";

    // -----------------------------------------------------------------------
    // Positive: Database.SetUserPassword must be a silent no-op in standalone
    // mode — the runner has no service tier, so password changes are ignored.
    // -----------------------------------------------------------------------

    [Test]
    procedure SetUserPassword_WithValidGuid_IsNoOp()
    var
        UserId: Guid;
    begin
        // Positive: a valid UserId + non-empty password must complete without error.
        UserId := CreateGuid();
        Assert.IsTrue(Src.ChangePassword(UserId, 'NewP@ssw0rd!'),
            'Database.SetUserPassword must not throw in standalone mode');
    end;

    [Test]
    procedure SetUserPassword_WithEmptyPassword_IsNoOp()
    var
        UserId: Guid;
    begin
        // Positive: empty password must also be silently accepted.
        UserId := CreateGuid();
        Assert.IsTrue(Src.ChangePassword(UserId, ''),
            'Database.SetUserPassword with empty password must not throw');
    end;

    // -----------------------------------------------------------------------
    // Negative: control-flow after the call still executes — the no-op does
    // not prevent subsequent code from running.
    // -----------------------------------------------------------------------

    [Test]
    procedure SetUserPassword_ReturnsTrueAfterCall()
    var
        UserId: Guid;
        Result: Boolean;
    begin
        // Negative: proves the function actually ran past the SetUserPassword
        // call and returned a meaningful value (not just swallowed by a crash).
        UserId := CreateGuid();
        Result := Src.ChangePassword(UserId, 'S3cr3t!');
        Assert.IsTrue(Result, 'Return value after SetUserPassword must be true');
    end;
}
