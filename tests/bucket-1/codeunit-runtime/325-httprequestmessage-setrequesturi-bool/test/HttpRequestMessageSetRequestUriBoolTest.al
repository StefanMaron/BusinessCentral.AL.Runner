codeunit 1320403 "HRM SetRequestUri Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SetRequestUri_ReturnsTrue()
    var
        Helper: Codeunit "HRM SetRequestUri Bool Src";
    begin
        Assert.AreEqual(true, Helper.SetRequestUriInIf('https://example.com'),
            'HttpRequestMessage.SetRequestUri must return true in bool context');
    end;

    [Test]
    procedure SetRequestUri_WrongExpectationFails()
    var
        Helper: Codeunit "HRM SetRequestUri Bool Src";
    begin
        asserterror Assert.AreEqual(false, Helper.SetRequestUriInIf('https://example.com'),
            'SetRequestUri should not return false');
        Assert.ExpectedError('AreEqual');
    end;
}
