codeunit 50901 "Resume Excl No Tests Dropped"
{
    procedure Broken()
    var
        Missing: Record "This Table Does Not Exist At All";
    begin
        Missing.Init();
    end;
}
