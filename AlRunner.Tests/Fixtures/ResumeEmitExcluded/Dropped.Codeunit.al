codeunit 50990 "Resume Excl Dropped"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Dropped_A()
    var
        Missing: Page "This Table Does Not Exist At All";
    begin
        Missing.Run();
    end;

    [Test]
    procedure Dropped_B()
    begin
    end;

    [Test]
    procedure Dropped_C()
    begin
    end;
}
