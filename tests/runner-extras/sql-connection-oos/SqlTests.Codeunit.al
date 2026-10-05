// Issue #5190 — the runner's backstop refusal for a SQL connection request.
//
// The runner has no SQL Server and its skeleton database has no database server, so BC's
// NavSqlConnectionScope.TryOpenConnection dereferenced null and died with a bare
// NullReferenceException that named neither the AL surface nor a reason. The surfaces reported so
// far were fixed where they sit — TaskScheduler.TaskExists/CancelTask (#2866, task-scheduler-oos)
// and raising an [ExternalBusinessEvent] (#5149, external-business-event) — and this suite pins
// the net underneath them: any OTHER path now refuses by name too.
//
// Not tested here: a GUARDED Codeunit.Run (`if not Codeunit.Run(...) then`). The runner's
// Codeunit.Run catches every exception into `false` with an empty GetLastErrorText(), refusals
// included, whatever surface raised them; that is not specific to SQL and is tracked as #5342.
//
// Database.AlterKey is the statement that drives it. The TryFunction test and the control at the
// bottom matter as much as the refusal: the backstop must not turn into a quiet `false`, and a surface
// with its own named refusal must keep it rather than be taken over by the generic one.
codeunit 65683 "Sql Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Sql Assert";
        Target: Codeunit "Sql Target";

    [Test]
    procedure AlterKey_RefusedByNameNotNre()
    begin
        asserterror Target.AlterSecondaryKey();

        // The prefix every out-of-scope refusal carries, then the surface and the anchor.
        Assert.ExpectedError('out-of-scope: SQL connection');
        Assert.ExpectedError('sql-connection');
        Assert.ExpectedError('docs/scope.md#sql-connection');
        Assert.ErrorContainsExactlyOnce('see docs/scope.md');
        // The regression this issue is about.
        Assert.NotExpectedError('Object reference not set');
    end;

    [Test]
    procedure AlterKey_NamesTheFrameThatAskedAndTheAlEntryPoint()
    begin
        asserterror Target.AlterSecondaryKey();

        // Who asked for the connection, and which AL entry point it came in through. A refusal that
        // names neither leaves the author to guess which statement touched SQL.
        Assert.ExpectedError('(requested by ');
        Assert.NotExpectedError('an unidentified frame');
        Assert.ExpectedError('reached from the AL entry point ALDatabase.ALAlterKey');
    end;

    [Test]
    procedure TryFunction_DoesNotAbsorbTheRefusalIntoFalse()
    var
        Ok: Boolean;
    begin
        // An AL [TryFunction] traps a PERMANENT refusal into `false` (that is what a BC environment
        // lacking the surface does) but lets a "not-yet-implemented" one through. The backstop cannot
        // know every surface behind it is permanent, so it is the second kind: asserterror sees the
        // refusal instead of a quiet `false`, the way the NRE it replaces tore through the try.
        asserterror Ok := Target.TryAlterSecondaryKey();

        Assert.ExpectedError('out-of-scope: SQL connection');
        Assert.ExpectedError('not-yet-implemented');
    end;

    [Test]
    procedure TaskExists_KeepsItsOwnRefusalNotTheBackstop()
    var
        Exists: Boolean;
    begin
        // Control. #2866 refuses TaskExists where it sits, before any connection is requested, so
        // the backstop must never be the message an AL author reads here.
        asserterror Exists := TaskScheduler.TaskExists(CreateGuid());

        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');
        Assert.ExpectedError('docs/scope.md#jobs');
        Assert.NotExpectedError('sql-connection');
    end;
}
