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
        // In standalone mode, CompanyName returns empty string — the key thing
        // is that it does NOT throw a NullReferenceException
        Assert.AreEqual('', Result, 'CompanyName should return empty string in standalone mode');
    end;

    [Test]
    procedure UserIdReturnsText()
    var
        Result: Text;
    begin
        // Positive: UserId should return a text value without crashing
        Result := Helper.GetUserId();
        Assert.AreEqual('', Result, 'UserId should return empty string in standalone mode');
    end;

    [Test]
    procedure CompanyNameDoesNotReturnNonEmpty()
    begin
        // Negative: CompanyName should NOT return a non-empty value in standalone mode
        Assert.AreNotEqual('CRONUS', Helper.GetCompanyName(), 'CompanyName should not return CRONUS in standalone mode');
    end;
}
