/// <summary>"Points" is generated into a tableextension appended to this file, which also holds an unrelated error: the compile drops it,
/// the file is the whole app, and nothing is compiled.</summary>
codeunit 65400 "One File Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SetsPoints()
    var
        E: Record "Loyalty Entry";
        I: Integer;
    begin
        E.Points := 5;
        I := 'abc';
    end;
}
