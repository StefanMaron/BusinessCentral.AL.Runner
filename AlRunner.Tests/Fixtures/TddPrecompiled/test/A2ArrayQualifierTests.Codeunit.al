/// <summary>
/// A missing member reached through an array element: the rewrite would skip evaluating the index, so
/// --tdd refuses this site. Alone in its file, so nothing else drops this object. The same member is
/// called through a plain variable in A4: one site that cannot be stubbed refuses the member for all.
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
        Result := Points[1].MixedCall(1);
    end;
}
