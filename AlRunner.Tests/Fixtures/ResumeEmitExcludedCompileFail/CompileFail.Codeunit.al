codeunit 50940 "Resume Excl Compile Fail"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure CompileFail_A()
    var
        Missing: Page "This Table Does Not Exist At All";
    begin
        Missing.Run();
    end;
}
