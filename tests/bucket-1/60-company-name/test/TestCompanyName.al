codeunit 58701 "Test CompanyName"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CompanyName_ReturnsNonEmptyString()
    var
        Helper: Codeunit "CompanyName Helper";
    begin
        // Positive: runner must return a non-empty placeholder string
        Assert.AreNotEqual('', Helper.GetCompany(), 'CompanyName() must not return empty string in runner');
    end;

    [Test]
    procedure CompanyName_ReturnsStableValue()
    var
        Helper: Codeunit "CompanyName Helper";
    begin
        // Positive: two consecutive calls must return the same value
        Assert.AreEqual(Helper.GetCompany(), Helper.GetCompany(), 'CompanyName() must return a stable value');
    end;

    [Test]
    procedure CompanyName_SpecificValue_IsCRONUS()
    var
        Helper: Codeunit "CompanyName Helper";
    begin
        // Prove the exact stub value so a no-op empty-string mock would fail
        Assert.AreEqual('CRONUS', Helper.GetCompany(), 'CompanyName() stub must return CRONUS');
    end;
}
