/// <summary>"Points" is generated beside the package table, in this file, but "When" has no assignment that types it, so the compile
/// drops the file and the extension with it.</summary>
codeunit 65451 "A Gen"
{
    procedure SetIt()
    var
        E: Record "Loyalty Entry";
    begin
        E.Points := 5;
        E.When := Today;
    end;
}
