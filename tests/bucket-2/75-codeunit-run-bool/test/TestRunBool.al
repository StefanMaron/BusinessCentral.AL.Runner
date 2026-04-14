codeunit 50759 "Test Run Bool"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SuccessfulRunReturnsTrue()
    var
        Dispatcher: Codeunit "Run Dispatcher";
    begin
        // Positive: Codeunit.Run on a codeunit that succeeds should return true
        Assert.IsTrue(Dispatcher.TryRunCodeunit(50751), 'Codeunit.Run should return true on success');
    end;

    [Test]
    procedure FailedRunReturnsFalse()
    var
        Dispatcher: Codeunit "Run Dispatcher";
    begin
        // Negative: Codeunit.Run on a codeunit that errors should return false
        Assert.IsFalse(Dispatcher.TryRunCodeunit(50750), 'Codeunit.Run should return false on error');
    end;
}
