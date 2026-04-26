codeunit 59731 "SDTC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SDTC Src";

    [Test]
    procedure SetDefaultTableConnection_NoOp()
    begin
        // Positive: standalone stub must complete without error.
        Src.CallSet(TableConnectionType::ExternalSQL, 'MyConn');
        Assert.IsTrue(true, 'SetDefaultTableConnection must not throw');
    end;

    [Test]
    procedure SetDefaultTableConnection_EmptyName()
    begin
        // Edge: empty connection name must not crash.
        Src.CallSet(TableConnectionType::ExternalSQL, '');
        Assert.IsTrue(true, 'SetDefaultTableConnection with empty name must not throw');
    end;

    [Test]
    procedure SetDefaultTableConnection_ExecutionContinues()
    begin
        // Proving: execution continues past the call.
        Assert.IsTrue(Src.CallSetAndReturnFlag(TableConnectionType::CRM, 'CRMConn'),
            'Caller must reach `exit(true)` after SetDefaultTableConnection');
    end;

    [Test]
    procedure SetDefaultTableConnection_DifferentTypes()
    begin
        // Both available TableConnectionType enum values must complete.
        Src.CallSet(TableConnectionType::ExternalSQL, 'SqlConn');
        Src.CallSet(TableConnectionType::CRM, 'CrmConn');
        Assert.IsTrue(true, 'Calls with both connection types must not throw');
    end;

    [Test]
    procedure SetDefaultTableConnection_RepeatedCalls_NegativeTrap()
    begin
        // Negative trap: guard against a stub that accumulates state and crashes
        // after multiple calls.
        Src.CallSet(TableConnectionType::ExternalSQL, 'Conn1');
        Src.CallSet(TableConnectionType::ExternalSQL, 'Conn2');
        Src.CallSet(TableConnectionType::ExternalSQL, 'Conn3');
        Assert.IsTrue(true, 'Multiple SetDefaultTableConnection calls must not throw');
    end;
}
