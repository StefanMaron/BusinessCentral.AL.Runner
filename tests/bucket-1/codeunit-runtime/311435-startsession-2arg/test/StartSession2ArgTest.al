codeunit 1312004 "StartSession 2-Arg Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure StartSession_2Arg_ReturnsTrue()
    var
        Api: Codeunit "StartSession2Arg Api";
        SessionId: Integer;
    begin
        // Positive: 2-arg StartSession(var SessionId, CodeunitID) dispatches
        // synchronously and returns true; SessionId is set to a non-zero value.
        Assert.IsTrue(Api.TryStartSessionNoCompany(SessionId), 'StartSession(2-arg) should return true');
        Assert.IsTrue(SessionId > 0, 'SessionId must be positive after 2-arg StartSession');
    end;

    [Test]
    procedure StartSession_2Arg_SessionIdIsNonZero()
    var
        Api: Codeunit "StartSession2Arg Api";
        SessionId: Integer;
    begin
        // StartSession must assign a fresh session ID (> 0).
        SessionId := 0;
        Api.TryStartSessionNoCompany(SessionId);
        Assert.IsTrue(SessionId > 0, 'SessionId should be non-zero after 2-arg StartSession');
    end;

    [Test]
    procedure StartSession_2Arg_MissingUserCU_ReturnsFalse()
    var
        Api: Codeunit "StartSession2Arg Api";
        SessionId: Integer;
    begin
        // Negative: a user-range codeunit ID (59999) that does not exist in the assembly
        // causes the synchronous dispatch to throw; StartSession traps it and returns false.
        Assert.IsFalse(Api.TryStartSessionMissingCU(SessionId), 'StartSession with missing user CU should return false');
    end;
}
