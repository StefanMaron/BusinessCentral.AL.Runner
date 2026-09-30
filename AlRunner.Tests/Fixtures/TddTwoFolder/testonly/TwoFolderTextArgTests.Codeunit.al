/// <summary>
/// #5037: the only object of its app. CalcByName takes a Text argument, whose length a call
/// site cannot fix, so --tdd refuses to generate it and must report each test FAILED instead.
/// </summary>
codeunit 65240 "Two Folder Text Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure TextArg_RefusedAndReportedFailed()
    var
        LoyaltyPoints: Codeunit "Loyalty Points";
        Result: Integer;
    begin
        Result := LoyaltyPoints.CalcByName('Gold');
    end;

    [Test]
    procedure SecondTest_AlsoReportedFailed()
    var
        LoyaltyPoints: Codeunit "Loyalty Points";
        Result: Integer;
    begin
        Result := LoyaltyPoints.CalcByName('Bronze');
    end;
}
