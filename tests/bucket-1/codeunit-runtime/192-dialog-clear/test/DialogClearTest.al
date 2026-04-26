/// Tests proving that Clear(dlg) on a Dialog variable compiles and runs without error.
/// Covers issue #964: MockDialog was missing Clear(), causing CS1061 at Roslyn compile time.
codeunit 97707 "Dialog Clear Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Dialog Clear Src";

    [Test]
    procedure ClearAfterOpenClose_DoesNotThrow()
    begin
        // [GIVEN] A dialog that has been opened and closed
        // [WHEN]  Clear(dlg) is called
        // [THEN]  No error is raised
        Src.OpenAndClear();
        Assert.IsTrue(true, 'Clear(dlg) after Open/Close must not throw');
    end;

    [Test]
    procedure ClearWithoutOpen_DoesNotThrow()
    begin
        // [GIVEN] A fresh dialog variable that was never opened
        // [WHEN]  Clear(dlg) is called
        // [THEN]  No error is raised
        Src.ClearWithoutOpen();
        Assert.IsTrue(true, 'Clear(dlg) on an unopened Dialog must not throw');
    end;

    [Test]
    procedure ClearTwice_DoesNotThrow()
    begin
        // [GIVEN] A dialog that is opened, closed, cleared, opened again, closed and cleared
        // [WHEN]  Both Clear() calls complete
        // [THEN]  No error is raised
        Src.ClearTwice();
        Assert.IsTrue(true, 'Calling Clear(dlg) twice must not throw');
    end;

    [Test]
    procedure ClearInline_DoesNotThrow()
    var
        dlg: Dialog;
    begin
        // [GIVEN] A dialog opened and closed inline
        dlg.Open('Hello');
        dlg.Close();

        // [WHEN]  Clear called directly in the test body
        Clear(dlg);

        // [THEN]  No error
        Assert.IsTrue(true, 'Inline Clear(dlg) must not throw');
    end;
}
