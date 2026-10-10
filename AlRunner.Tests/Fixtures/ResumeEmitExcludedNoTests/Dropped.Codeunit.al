codeunit 50901 "Resume Excl No Tests Dropped"
{
    procedure Broken()
    var
        Missing: Page "This Table Does Not Exist At All";
    begin
        Missing.Run();
    end;
}
