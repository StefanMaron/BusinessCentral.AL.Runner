/// <summary>
/// #5244: two tests name the same missing member, and the first call site cannot anchor a type (a
/// bare statement: void, or a discarded return value?). The second one can, so the member is
/// generated from it, and BOTH tests reach the stub: the first now compiles against it.
/// </summary>
codeunit 65211 "Tdd Shape Order Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure A_BareStatementFirst_StillReachesTheStubTheTypedCallGenerates()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Target.Ordered(1);
    end;

    [Test]
    procedure B_TypedCallSecond_GeneratesTheMember()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.Ordered(1);
    end;
}
