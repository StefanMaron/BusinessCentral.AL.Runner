codeunit 50990 "Resume Excl Dropped"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure Dropped_A()
    var
        Missing: Codeunit "This Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
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
