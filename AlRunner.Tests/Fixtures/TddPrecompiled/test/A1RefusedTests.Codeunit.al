/// <summary>
/// A call --tdd refuses to stub (a Text argument: its length cannot be inferred) next to one it could.
/// The object is dropped and each test reported FAILED naming the missing symbol; the other files must
/// still run, so the stub they need cannot live here. The file sorts first on purpose.
/// </summary>
codeunit 65322 "Precompiled Refused Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TextArgument_IsRefused()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.ByName('abc');
    end;

    [Test]
    procedure StubbableCall_InTheSameDroppedFile()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.CalcPoints(1);
    end;
}
