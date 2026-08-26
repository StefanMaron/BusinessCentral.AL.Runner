/// <summary>
/// Nested-call test method for DapServerTests (issue #2045): the outer test procedure
/// has a statement that calls a local procedure (Double) with two statements of its
/// own, so step-over ("next"), step-in, and step-out land on three DIFFERENT
/// statements — proving real step granularity, not all three behaving like "continue".
/// See individual line-number constants in DapServerTests.cs; they are asserted
/// against exactly, so do not reformat this file without updating them there too.
/// </summary>
codeunit 60901 "Dap Stepping Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dap Step Assert";

    [Test]
    procedure NestedCall()
    var
        Result: Integer;
    begin
        Result := 1;
        Result := Double(Result);
        Result := Result + 10;
        Assert.AreEqual(12, Result, 'final value after the nested call and the outer addition');
    end;

    local procedure Double(X: Integer): Integer
    var
        Y: Integer;
    begin
        Y := X * 2;
        exit(Y);
    end;
}
