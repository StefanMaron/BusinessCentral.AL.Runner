/// <summary>
/// #5244 across bundles: the first call site is a bare statement, which --tdd cannot anchor a type
/// for; the second is typed. The member is generated into the app from the second, and both tests
/// reach it.
/// </summary>
codeunit 65510 "Order Cross Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure A_BareStatementFirst_StillReachesTheStubTheTypedCallGenerates()
    var
        Target: Codeunit "Order App Target";
    begin
        Target.CrossOrdered(1);
    end;

    [Test]
    procedure B_TypedCallSecond_GeneratesTheMemberIntoTheApp()
    var
        Target: Codeunit "Order App Target";
        Result: Integer;
    begin
        Result := Target.CrossOrdered(1);
    end;
}
