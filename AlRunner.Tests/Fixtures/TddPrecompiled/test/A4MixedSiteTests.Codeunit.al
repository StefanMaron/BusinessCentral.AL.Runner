/// <summary>
/// A call through a plain variable, which on its own --tdd could stub, to a member another file calls
/// through an array element (A2). A member is stubbed only when EVERY call to it can be pointed at the
/// stub, so this site is not stubbed either and its test is reported FAILED, not run against a stub.
/// </summary>
codeunit 65325 "Precompiled Mixed Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure PlainSite_OfAMemberAnotherSiteCannotStub_IsRefused()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.MixedCall(2);
    end;
}
