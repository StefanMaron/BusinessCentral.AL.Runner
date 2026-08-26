/// <summary>
/// Three-statement test method for DapServerTests: proves a breakpoint set on the
/// SECOND statement's line pauses BEFORE that statement runs — Counter is 1 (the
/// first statement's effect), not yet 2 — and that continuing lets the third
/// statement run and the test pass. See AlDapSession's file header for why StmtHit(N)
/// firing before statement N's own effect is exactly the boundary a debugger wants.
/// </summary>
codeunit 60251 "Dap Breakpoint Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dap Bp Assert";

    [Test]
    procedure ThreeStatements()
    var
        Counter: Integer;
    begin
        Counter := 1;
        Counter := 2;
        Counter := 3;
        Assert.AreEqual(3, Counter, 'final value after all three statements');
    end;
}
