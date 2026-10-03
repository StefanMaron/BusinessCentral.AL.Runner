codeunit 50901 "Resume Excl No Tests Dropped"
{
    procedure Broken()
    var
        Missing: Codeunit "This Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
