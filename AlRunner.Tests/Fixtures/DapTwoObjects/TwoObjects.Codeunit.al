/// <summary>
/// #3786: TWO codeunits in ONE file. The second object's text begins partway down
/// the file, so BC's [SourceSpans] lines — which are relative to the OWNING OBJECT's
/// text, not to the file — no longer coincide with file lines for anything it holds.
///
/// The line numbers below are asserted against exactly by DapMultiObjectFileTests.
/// Keep them in sync, and keep the test codeunit SECOND: first-object statements are
/// the case where the two numbering schemes agree and prove nothing.
/// </summary>
codeunit 60261 "Dap Two Helper"
{
    procedure Bump(X: Integer): Integer
    begin
        exit(X + 1);
    end;
}

codeunit 60262 "Dap Two Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dap Two Assert";
        Helper: Codeunit "Dap Two Helper";

    [Test]
    procedure SecondObjectStatements()
    var
        Counter: Integer;
    begin
        Counter := 1;
        Counter := Helper.Bump(Counter);
        Assert.AreEqual(2, Counter, 'Bump ran once on the first statement''s value');
    end;
}
