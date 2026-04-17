codeunit 60371 "DBG Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DBG Src";

    [Test]
    procedure IsActive_ReturnsFalse()
    begin
        Assert.IsFalse(Src.IsDebuggerActive(),
            'Debugger.IsActive must return false in standalone mode');
    end;

    [Test]
    procedure Deactivate_DoesNotThrow()
    begin
        Assert.IsTrue(Src.DeactivateDoesNotThrow(),
            'Debugger.DeactivateDebugger must not throw');
    end;

    [Test]
    procedure Activate_DoesNotThrow()
    begin
        Assert.IsTrue(Src.ActivateDoesNotThrow(),
            'Debugger.Activate must not throw');
    end;
}
