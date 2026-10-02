codeunit 50982 "Claim Broken Bad RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure DoesNotCompile()
    begin
        ThisIdentifierDoesNotExist := 1;
    end;
}
