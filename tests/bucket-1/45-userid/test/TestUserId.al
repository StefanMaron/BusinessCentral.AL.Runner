codeunit 55000 "Test UserId"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure UserIdReturnsTestUserByDefault()
    var
        Id: Text;
    begin
        // By default, UserId() returns "TESTUSER" when not configured via --user-id.
        Id := UserId();
        Assert.AreEqual('TESTUSER', Id, 'UserId() must return TESTUSER when not configured');
    end;
}
