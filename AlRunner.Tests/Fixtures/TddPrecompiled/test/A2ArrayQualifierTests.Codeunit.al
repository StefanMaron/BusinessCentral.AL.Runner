/// <summary>
/// A missing member reached through an array element: the rewrite would skip evaluating the index, so
/// --tdd refuses it. Alone in its file, so nothing else drops this object.
/// </summary>
codeunit 65323 "Precompiled Array Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ArrayElementQualifier_IsRefused()
    var
        Points: array[2] of Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points[1].ByIndex(1);
    end;
}
