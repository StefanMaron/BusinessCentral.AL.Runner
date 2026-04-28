codeunit 1320407 "HRM GetHeaders Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetHeaders_ReturnsTrue()
    var
        Helper: Codeunit "HRM GetHeaders Bool Src";
    begin
        Assert.AreEqual(true, Helper.GetHeadersInIf(),
            'HttpRequestMessage.GetHeaders must return true in bool context');
    end;

    [Test]
    procedure GetHeaders_WrongExpectationFails()
    var
        Helper: Codeunit "HRM GetHeaders Bool Src";
    begin
        asserterror Assert.AreEqual(false, Helper.GetHeadersInIf(),
            'GetHeaders should not return false');
        Assert.ExpectedError('AreEqual');
    end;
}
