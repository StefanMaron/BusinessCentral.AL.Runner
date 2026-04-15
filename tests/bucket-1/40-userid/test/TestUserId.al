codeunit 54250 "Test UserId"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure UserIdReturnsEmptyStringByDefault()
    var
        Id: Text;
    begin
        // Positive: UserId() returns empty string when not configured
        Id := UserId();
        Assert.AreEqual('', Id, 'UserId() must return empty string when not configured');
    end;
}
