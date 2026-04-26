codeunit 1299002 "GetUrl Overload Tests"
{
    Subtype = Test;

    [Test]
    procedure GetUrl_OneArg_ReturnsNonEmpty()
    var
        Helper: Codeunit "GetUrl Helper";
        Result: Text;
    begin
        // Positive: GetUrl with just ClientType should compile and return a value
        Result := Helper.GetUrlOneArg();
        Assert.AreNotEqual('', Result, 'GetUrl(ClientType) should return a non-empty URL');
    end;

    [Test]
    procedure GetUrl_TwoArgs_ReturnsNonEmpty()
    var
        Helper: Codeunit "GetUrl Helper";
        Result: Text;
    begin
        // Positive: GetUrl with ClientType + Company should compile and return a value
        Result := Helper.GetUrlTwoArgs();
        Assert.AreNotEqual('', Result, 'GetUrl(ClientType, Company) should return a non-empty URL');
    end;

    [Test]
    procedure GetUrl_FourArgs_ReturnsValueContainingObjectId()
    var
        Helper: Codeunit "GetUrl Helper";
        Result: Text;
    begin
        // Positive: GetUrl with ClientType, Company, ObjectType, ObjectId should return URL with object info
        Result := Helper.GetUrlFourArgs();
        Assert.AreNotEqual('', Result, 'GetUrl(ClientType, Company, ObjectType, ObjectId) should return a non-empty URL');
        Assert.IsTrue(Result.Contains('22'), 'GetUrl result should contain the object ID 22');
    end;

    var
        Assert: Codeunit "Library Assert";
}
