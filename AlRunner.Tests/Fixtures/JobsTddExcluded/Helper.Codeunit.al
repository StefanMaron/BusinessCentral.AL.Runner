// Dropped, and declares no [Test]: ReachesDropped calls it by object id, which binds at run time, so the
// survivor compiles and the codeunit is missing only where the test reaches it.
codeunit 51001 "Jobs Tdd Helper"
{
    trigger OnRun()
    var
        Missing: Record "This Helper Table Does Not Exist";
    begin
        Missing.Init();
    end;
}
