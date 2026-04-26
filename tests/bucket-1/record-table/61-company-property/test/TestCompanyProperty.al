codeunit 61401 "Test CompanyProperty"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure DisplayName_ReturnsNonEmptyString()
    var
        Name: Text;
    begin
        Name := CompanyProperty.DisplayName();
        Assert.AreNotEqual('', Name, 'DisplayName must return a non-empty string');
    end;

    [Test]
    procedure DisplayName_ReturnsExpectedStubValue()
    var
        Name: Text;
    begin
        Name := CompanyProperty.DisplayName();
        Assert.AreEqual('My Company', Name, 'DisplayName must return the stub company name');
    end;

    [Test]
    procedure UrlName_ReturnsNonEmptyString()
    var
        Name: Text;
    begin
        Name := CompanyProperty.UrlName();
        Assert.AreNotEqual('', Name, 'UrlName must return a non-empty string');
    end;

    [Test]
    procedure UrlName_ReturnsExpectedStubValue()
    var
        Name: Text;
    begin
        Name := CompanyProperty.UrlName();
        Assert.AreEqual('My%20Company', Name, 'UrlName must return the URL-encoded stub company name');
    end;

    [Test]
    procedure ID_ReturnsNonEmptyGuid()
    var
        Id: Guid;
    begin
        Id := CompanyProperty.ID();
        Assert.IsFalse(IsNullGuid(Id), 'ID must return a non-empty GUID');
    end;
}
