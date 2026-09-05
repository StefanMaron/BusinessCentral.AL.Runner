// Issue #2725 — runner-specific half of the table-connection contract.
//
// BC's CrmTableConnection.CreateDataAccess picks CrmTestDataProvider (in-memory, BC's own)
// when the connection was registered inside a test with the '@@test@@' connection string,
// and CrmDataProvider (a live Dataverse connection through the Xrm connector stack)
// otherwise. The first branch runs in-process; the second cannot, and the runner refuses it
// by name with RunnerOutOfScopeException (docs/scope.md#table-connections) rather than
// silently serving the table from a plain temp store, which is what happened before #2725.
//
// Everything that is plain BC behaviour (registration bookkeeping, the Guid PK a CRM insert
// gets, BC's own "not registered" error) is proven upstream in the al-language corpus.
codeunit 65532 "Tcl Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Tcl Assert";
        LiveConnectionTok: Label 'TCLLIVE', Locked = true;
        TestConnectionTok: Label 'TCLTEST', Locked = true;

    [Test]
    procedure LiveCrmConnection_Insert_ThrowsOutOfScope()
    begin
        Initialize();

        // A connection string with no '@@test@@' marker is, to BC, a real Dataverse org.
        // Registering it is session bookkeeping and succeeds on BC and here alike; it is
        // the first data access that would open the live connection.
        RegisterTableConnection(TableConnectionType::CRM, LiveConnectionTok,
            'Url=https://contoso.crm.dynamics.com; AuthType=Office365; UserName=tcl@contoso.com; Password=secret; ProxyVersion=9');
        SetDefaultTableConnection(TableConnectionType::CRM, LiveConnectionTok);
        Assert.IsTrue(HasTableConnection(TableConnectionType::CRM, LiveConnectionTok),
            'registering a live-shaped CRM connection is in scope; only using it is not');

        // The record variable lives inside InsertRow: BC binds a TableType = CRM record to its
        // connection's data access when the variable is first touched, so the touch has to
        // happen inside the asserterror, not at this procedure's entry.
        asserterror InsertRow('live');
        Assert.ExpectedError('out-of-scope: CrmTableConnection.CreateDataAccess');
        Assert.ExpectedError('table connection ''TCLLIVE''');
        Assert.ExpectedError('table-connections');

        UnregisterTableConnection(TableConnectionType::CRM, LiveConnectionTok);
    end;

    [Test]
    procedure TestCrmConnection_Insert_IsNotRefused()
    var
        Entity: Record "Tcl CRM Entity";
    begin
        // Scoping control for the test above: the refusal is keyed on the connection, not
        // on the TableType. The '@@test@@' connection lands on BC's own CrmTestDataProvider,
        // so the same Insert succeeds and the row is readable back through the same table.
        Initialize();

        RegisterTableConnection(TableConnectionType::CRM, TestConnectionTok, '@@test@@');
        SetDefaultTableConnection(TableConnectionType::CRM, TestConnectionTok);

        Entity.Init();
        Entity.Name := 'test';
        Entity.Insert();
        Assert.IsFalse(IsNullGuid(Entity.EntityId), 'BC''s CrmTestDataProvider assigns a Guid PK on insert');
        Assert.AreEqual(1, Entity.Count(), 'the row must be readable through the same CRM table');

        Entity.DeleteAll();
        UnregisterTableConnection(TableConnectionType::CRM, TestConnectionTok);
    end;

    local procedure InsertRow(Name: Text[100])
    var
        Entity: Record "Tcl CRM Entity";
    begin
        Entity.Init();
        Entity.Name := Name;
        Entity.Insert();
    end;

    local procedure Initialize()
    begin
        // The TableConnectionManager is session state and outlives a test, on BC and here.
        if HasTableConnection(TableConnectionType::CRM, LiveConnectionTok) then
            UnregisterTableConnection(TableConnectionType::CRM, LiveConnectionTok);
        if HasTableConnection(TableConnectionType::CRM, TestConnectionTok) then
            UnregisterTableConnection(TableConnectionType::CRM, TestConnectionTok);
    end;
}
