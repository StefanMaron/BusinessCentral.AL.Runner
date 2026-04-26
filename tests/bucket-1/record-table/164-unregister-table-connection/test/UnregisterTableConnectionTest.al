codeunit 59771 "UTC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "UTC Src";

    [Test]
    procedure UnregisterTableConnection_NoOp()
    begin
        // Positive: standalone stub must complete without error.
        Src.CallUnregister(TableConnectionType::ExternalSQL, 'MyConn');
        Assert.IsTrue(true, 'UnregisterTableConnection must not throw');
    end;

    [Test]
    procedure UnregisterTableConnection_EmptyName()
    begin
        // Edge: empty name must not crash the stub.
        Src.CallUnregister(TableConnectionType::ExternalSQL, '');
        Assert.IsTrue(true, 'UnregisterTableConnection with empty name must not throw');
    end;

    [Test]
    procedure UnregisterTableConnection_ExecutionContinues()
    begin
        // Proving: execution continues past the call.
        Assert.IsTrue(Src.CallUnregisterAndReturnFlag(TableConnectionType::CRM, 'CRMConn'),
            'Caller must reach `exit(true)` after UnregisterTableConnection');
    end;

    [Test]
    procedure UnregisterTableConnection_DifferentTypes()
    begin
        // Both available TableConnectionType enum values must complete.
        Src.CallUnregister(TableConnectionType::ExternalSQL, 'SqlConn');
        Src.CallUnregister(TableConnectionType::CRM, 'CrmConn');
        Assert.IsTrue(true, 'Calls with both connection types must not throw');
    end;

    [Test]
    procedure UnregisterTableConnection_NonExistent_NoOp()
    begin
        // Edge: unregistering a connection that was never registered is a no-op
        // (the runner has no connection registry, but the call must still complete).
        Src.CallUnregister(TableConnectionType::ExternalSQL, 'NeverRegistered');
        Assert.IsTrue(true, 'Unregister of non-existent connection must not throw');
    end;
}
