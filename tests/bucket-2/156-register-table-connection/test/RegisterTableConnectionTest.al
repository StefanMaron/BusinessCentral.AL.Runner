codeunit 59681 "RTC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RTC Src";

    [Test]
    procedure RegisterTableConnection_NoOp()
    begin
        // Positive: standalone stub must complete without error.
        Src.CallRegister(TableConnectionType::ExternalSQL, 'MyConn', 'Server=localhost');
        Assert.IsTrue(true, 'RegisterTableConnection must not throw');
    end;

    [Test]
    procedure RegisterTableConnection_EmptyArgs()
    begin
        // Edge: empty strings must not crash the stub.
        Src.CallRegister(TableConnectionType::ExternalSQL, '', '');
        Assert.IsTrue(true, 'RegisterTableConnection with empty args must not throw');
    end;

    [Test]
    procedure RegisterTableConnection_ExecutionContinues()
    begin
        // Proving: execution continues past the call (flag gets set).
        Assert.IsTrue(
            Src.CallRegisterAndReturnFlag(TableConnectionType::CRM, 'CRMConn', 'Endpoint=crm.example'),
            'Caller must reach `exit(true)` after RegisterTableConnection');
    end;

    [Test]
    procedure RegisterTableConnection_DifferentTypes()
    begin
        // Both available TableConnectionType enum values must complete.
        Src.CallRegister(TableConnectionType::ExternalSQL, 'SqlConn', 'sql-cs');
        Src.CallRegister(TableConnectionType::CRM, 'CrmConn', 'crm-cs');
        Assert.IsTrue(true, 'Calls with both connection types must not throw');
    end;

    [Test]
    procedure RegisterTableConnection_LongConnectionString_NegativeTrap()
    begin
        // Negative trap: guard against crashing on large input.
        Src.CallRegister(
            TableConnectionType::ExternalSQL,
            'VeryLongConnectionName_123456789',
            'Server=verylonghostname.example.com;Database=db;User=dbuser;Password=pwd;MultipleActiveResultSets=true');
        Assert.IsTrue(true, 'RegisterTableConnection with long strings must not throw');
    end;
}
