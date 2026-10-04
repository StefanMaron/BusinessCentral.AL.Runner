/// <summary>
/// #5262: the member this calls is missing from "Jobs Rerun Points", which lives in the sibling app folder.
/// --tdd generates it into that app in memory and compiles the test bundle again, so a worker of a shared
/// test bundle compiles the bundle twice and the second pass must report the same dropped rows as the first.
/// </summary>
codeunit 51120 "Jobs Rerun Generates"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure LiteralArg_GeneratesIntegerParameter()
    var
        Points: Codeunit "Jobs Rerun Points";
        Result: Integer;
    begin
        Result := Points.CalcLit(250);
    end;
}
