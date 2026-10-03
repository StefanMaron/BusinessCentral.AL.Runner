/// <summary>
/// A test object of the test bundle itself that calls a member --tdd refuses (a Text argument): its
/// test is reported FAILED, beside the library object that was dropped without any test of its own.
/// </summary>
codeunit 65491 "Lib Refused Own Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure OwnRefusedCall_IsReportedFailed()
    var
        Loyalty: Codeunit "Lib Refused Loyalty";
        Result: Integer;
    begin
        Result := Loyalty.MissingOwn('abc');
    end;
}
