/// <summary>"Rank" is assigned an Integer in A1FieldTests and a Boolean here: the first file decides (Integer), so this one
/// no longer compiles and is FAILED.</summary>
codeunit 65407 "Package Disagreeing Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Rank_TheSecondFileLoses()
    var
        E: Record "Loyalty Entry";
    begin
        E.Rank := true;
    end;
}
