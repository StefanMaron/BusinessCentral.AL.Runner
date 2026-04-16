codeunit 56691 "Company Name Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Company Name Helper";

    [Test]
    procedure CompanyNameReturnsText()
    var
        Result: Text;
    begin
        // Positive: CompanyName should return a text value without crashing
        Result := Helper.GetCompanyName();
        // In standalone mode, CompanyName returns 'CRONUS' by default — the key thing
        // is that it does NOT throw a NullReferenceException and returns a non-empty value
        Assert.AreEqual('CRONUS', Result, 'CompanyName should return CRONUS stub value in standalone mode');
    end;

    [Test]
    procedure UserIdReturnsText()
    var
        Result: Text;
    begin
        // Positive: UserId should return a text value without crashing.
        // Default is "TESTUSER" when not configured via --user-id.
        Result := Helper.GetUserId();
        Assert.AreEqual('TESTUSER', Result, 'UserId should return TESTUSER by default');
    end;

    [Test]
    procedure CompanyNameReturnsNonEmpty()
    begin
        // Positive: CompanyName must return a non-empty stub value in standalone mode
        Assert.AreNotEqual('', Helper.GetCompanyName(), 'CompanyName must return a non-empty string in standalone mode');
    end;
}
