codeunit 114001 "DB Stubs Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DB Stubs Src";

    [Test]
    procedure SessionId_IsNonZero()
    begin
        Assert.IsTrue(Src.GetSessionId() > 0,
            'Database.SessionId must return a positive non-zero stub value');
    end;

    [Test]
    procedure SessionId_IsStable()
    begin
        Assert.AreEqual(Src.GetSessionId(), Src.GetSessionId(),
            'Database.SessionId must return the same value on repeated calls');
    end;

    [Test]
    procedure SessionId_IsNotNoop()
    begin
        // Negative trap: a no-op returning 0 would fail the > 0 check;
        // additionally confirm the return value is a specific stable positive int.
        Assert.AreNotEqual(0, Src.GetSessionId(),
            'Database.SessionId must not return 0 — stub must return at least 1');
    end;

    [Test]
    procedure HasTableConnection_UnregisteredReturnsFalse()
    begin
        Assert.IsFalse(Src.HasTableConnectionCRM('NonExistent'),
            'HasTableConnection must return false for an unregistered connection name');
    end;

    [Test]
    procedure HasTableConnection_NotANoop()
    begin
        // Negative trap: a stub always returning true would fail this assertion.
        Assert.AreNotEqual(true, Src.HasTableConnectionCRM('AlsoNotRegistered'),
            'HasTableConnection must not be a no-op always returning true');
    end;
}
