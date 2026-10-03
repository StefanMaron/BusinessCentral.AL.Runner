/// <summary>#5287 control: a chain of exactly the re-run limit. "M3" is missing on "Lib Chain 2", each of lib1 and lib2 calls a member the one before it lacks, and no member is left over after the third re-run.</summary>
codeunit 72070 "Lib Chain At Limit Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Reaches()
    var
        Last: Codeunit "Lib Chain 2";
        Result: Integer;
    begin
        Result := Last.M3(1);
    end;
}
