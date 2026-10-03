/// <summary>#5292: hangs, so the run resumes in a fresh process. It sorts before "Resume Lib Dropped Tests", whose
/// tests therefore run in the resumed attempt.</summary>
codeunit 72500 "Resume Lib Dropped Hang"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Hangs()
    begin
        while true do;
    end;
}
