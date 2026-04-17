codeunit 118001 "TRAP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Trap_DoesNotCrash()
    var
        TP: TestPage "TRAP Card Page";
    begin
        // Positive: Trap() must not throw.
        TP.Trap();
    end;

    [Test]
    procedure Trap_PreventsModalHandlerException()
    var
        TP: TestPage "TRAP Card Page";
        P: Page "TRAP Card Page";
    begin
        // Positive: after Trap(), RunModal() on the matching Page var must
        // succeed (return OK) instead of throwing "No ModalPageHandler registered".
        TP.Trap();
        P.RunModal();
    end;

    [Test]
    procedure WithoutTrap_RunModal_Throws()
    var
        P: Page "TRAP Card Page";
    begin
        // Negative trap: without a prior Trap() call, RunModal() must throw.
        asserterror P.RunModal();
        Assert.ExpectedError('ModalPageHandler');
    end;
}
