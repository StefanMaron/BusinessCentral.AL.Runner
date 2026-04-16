codeunit 59000 "Has Table Connection Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure HasTableConnection_ReturnsFalse()
    begin
        // Positive: runner has no real external connections — must return false
        Assert.IsFalse(
            HasTableConnection(TableConnectionType::ExternalSQL, 'MyConnection'),
            'HasTableConnection() must return false in runner (no real connections)');
    end;

    [Test]
    procedure HasTableConnection_DifferentType_ReturnsFalse()
    begin
        // Positive: CRM type also returns false
        Assert.IsFalse(
            HasTableConnection(TableConnectionType::CRM, 'CRMConn'),
            'HasTableConnection() must return false for CRM connections in runner');
    end;

    [Test]
    procedure HasTableConnection_NegativeExpectation()
    begin
        // Negative: asserterror when code asserts the connection EXISTS
        asserterror Assert.IsTrue(
            HasTableConnection(TableConnectionType::ExternalSQL, 'AnyConn'),
            'Should not have a connection');
    end;
}
