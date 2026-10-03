codeunit 50921 "Resume Excl Partner Dropped"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure PartnerDropped_A()
    var
        Missing: Codeunit "This Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
