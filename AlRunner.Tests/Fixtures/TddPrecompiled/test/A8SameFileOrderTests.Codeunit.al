/// <summary>
/// One missing member called twice in ONE file with shapes that disagree: an Integer result first, a
/// Boolean assignment after it. Within a file the earlier call decides (position order), so the stub
/// answers an Integer and the Boolean assignment is the one that no longer compiles.
/// </summary>
codeunit 65330 "Precompiled Same File Order"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure DisagreeingShapes_TheEarlierCallInTheFileDecides()
    var
        Points: Codeunit "Precompiled Points";
        Count: Integer;
        Flag: Boolean;
    begin
        Count := Points.Ranked(1);
        Flag := Points.Ranked(1);
    end;
}
