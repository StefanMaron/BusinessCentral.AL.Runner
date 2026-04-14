codeunit 50421 "Try Caller Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TrySucceedsReturnsTrue()
    var
        Caller: Codeunit "Try Caller";
    begin
        // [GIVEN] A TryFunction with no Error
        // [THEN] Probe must return true
        Assert.IsTrue(Caller.ProbeSuccess(), 'TryFunction with no Error must return true');
    end;

    [Test]
    procedure TryFailureReturnsFalse()
    var
        Caller: Codeunit "Try Caller";
    begin
        // [GIVEN] A TryFunction that calls Error
        // [THEN] Probe must return false — NOT bubble up the error
        Assert.IsFalse(Caller.ProbeFailure(), 'TryFunction that errors must return false');
    end;

    [Test]
    procedure TryFailureInIfBranchesCorrectly()
    var
        Caller: Codeunit "Try Caller";
    begin
        // [GIVEN] TryFunction invoked in `if` condition
        // [THEN] False branch must run
        Assert.AreEqual('handled-failure', Caller.ProbeFailureIgnoringResult(), 'If-branch should handle the false case');
    end;
}
