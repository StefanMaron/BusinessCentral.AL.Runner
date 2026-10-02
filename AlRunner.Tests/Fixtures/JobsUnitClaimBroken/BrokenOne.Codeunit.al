codeunit 50981 "Claim Broken One RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure DoesNotCompileEither()
    begin
        ThisIdentifierDoesNotExistToo := 1;
    end;
}
