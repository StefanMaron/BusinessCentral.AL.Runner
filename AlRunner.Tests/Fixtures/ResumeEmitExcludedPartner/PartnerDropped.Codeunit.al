codeunit 50921 "Resume Excl Partner Dropped"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure PartnerDropped_A()
    var
        Missing: Record "This Table Does Not Exist At All";
    begin
        Missing.Init();
    end;
}
