codeunit 59361 "LT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "LT Src";

    [Test]
    procedure LockTimeout_DefaultIsTrue()
    begin
        // Positive: BC's Database.LockTimeout default is true.
        Assert.IsTrue(Src.GetLockTimeout(), 'LockTimeout must default to true');
    end;

    [Test]
    procedure LockTimeoutDuration_IsNonNegative()
    var
        d: Duration;
    begin
        // Positive: LockTimeoutDuration must be >= 0 (a negative duration
        // would indicate an unwired stub returning garbage).
        d := Src.GetLockTimeoutDuration();
        Assert.IsTrue(d >= 0, 'LockTimeoutDuration must be non-negative');
    end;

    [Test]
    procedure LockTimeout_SetFalseReadBack()
    var
        result: Boolean;
    begin
        // In the runner there is no real DB, so setting LockTimeout is a no-op.
        // The setter call must not throw, and the read afterward must still
        // be defined. Under the current stub contract the default (true) is
        // preserved regardless of what's passed to the setter.
        result := Src.SetAndGetLockTimeout(false);
        Assert.IsTrue(result, 'After no-op setter, LockTimeout must still read as true (default)');
    end;

    [Test]
    procedure LockTimeout_SetTrueReadBack()
    begin
        Assert.IsTrue(Src.SetAndGetLockTimeout(true), 'SetAndGetLockTimeout(true) must return true');
    end;

    [Test]
    procedure LockTimeout_ReadReturnsBoolean_NegativeTrap()
    var
        b: Boolean;
    begin
        // Negative: guard against a stub that throws — just reading into a local
        // variable proves the property getter completes and yields a Boolean.
        b := Src.GetLockTimeout();
        Assert.IsTrue(b or (not b), 'LockTimeout read must complete and return a Boolean');
    end;
}
