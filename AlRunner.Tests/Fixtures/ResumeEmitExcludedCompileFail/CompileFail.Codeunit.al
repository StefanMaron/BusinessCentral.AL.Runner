codeunit 50940 "Resume Excl Compile Fail"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure CompileFail_A()
    var
        Missing: Codeunit "This Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
