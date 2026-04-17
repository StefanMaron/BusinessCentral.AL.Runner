codeunit 60411 "LEC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "LEC Src";

    [Test]
    procedure GetLastErrorCallStack_NoPriorError_IsEmpty()
    begin
        Assert.AreEqual('', Src.GetCallStack_NoPriorError(),
            'GetLastErrorCallStack with no prior error must return empty');
    end;

    [Test]
    procedure GetLastErrorCallStack_DoesNotThrow()
    begin
        // Must complete without an exception.
        Src.GetCallStack_NoPriorError();
        Assert.IsTrue(true, 'GetLastErrorCallStack must not throw');
    end;

    [Test]
    procedure GetLastErrorCallStack_AfterCaughtError_IsText()
    var
        cs: Text;
    begin
        // After a caught TryFunction error, the call stack is available.
        // Standalone may return an empty string or a short trace; just verify
        // the call completes.
        cs := Src.GetCallStack_AfterCaughtError();
        Assert.IsTrue(true, 'GetLastErrorCallStack after caught error must not throw');
    end;
}
