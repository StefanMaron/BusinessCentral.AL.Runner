codeunit 50963 "Resume Two Hangs Dropped"
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
}
