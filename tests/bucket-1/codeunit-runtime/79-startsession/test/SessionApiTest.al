// Renumbered from 50901 to avoid collision in new bucket layout (#1385).
codeunit 1050901 "Session Api Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure StartSessionReturnsTrue()
    var
        Api: Codeunit "Session Api";
    begin
        // StartSession dispatches the codeunit synchronously and returns true.
        Assert.IsTrue(Api.TryStartSession(), 'StartSession should return true (synchronous dispatch)');
    end;

    [Test]
    procedure StartSessionWithRecordDispatchesSynchronously()
    var
        Api: Codeunit "Session Api";
        WorkItem: Record "Session Work Item";
        SessionId: Integer;
    begin
        // StartSession with a record parameter should dispatch the codeunit
        // synchronously, executing OnRun which marks the record as processed.
        Assert.IsTrue(Api.TryStartSessionWithRecord(SessionId), 'StartSession with record should return true');
        // Session ID should be non-zero (fake session counter).
        Assert.IsTrue(SessionId > 0, 'SessionId should be non-zero');
    end;

    [Test]
    procedure IsSessionActiveReturnsFalse()
    var
        Api: Codeunit "Session Api";
    begin
        // After synchronous dispatch, the session is already completed — returns false.
        Assert.IsFalse(Api.CheckIsSessionActive(999), 'IsSessionActive should return false (completed)');
    end;

    [Test]
    procedure SleepDoesNotCrash()
    var
        Api: Codeunit "Session Api";
    begin
        // Sleep should be a no-op and not throw.
        Api.DoSleep();
        Assert.IsTrue(true, 'Sleep completed without error');
    end;

    [Test]
    procedure StopSessionDoesNotCrash()
    var
        Api: Codeunit "Session Api";
    begin
        // StopSession should be a no-op and not throw.
        Api.DoStopSession(42);
        Assert.IsTrue(true, 'StopSession completed without error');
    end;

    [Test]
    procedure IsSessionActiveNotTrue()
    var
        Api: Codeunit "Session Api";
    begin
        // Negative: IsSessionActive must NOT return true — there is no live session.
        asserterror Assert.IsTrue(Api.CheckIsSessionActive(42), 'Should fail');
        Assert.ExpectedError('Should fail');
    end;
}
