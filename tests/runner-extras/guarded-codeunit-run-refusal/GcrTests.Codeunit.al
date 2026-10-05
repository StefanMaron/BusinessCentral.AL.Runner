// Issue #5342 — a guarded Codeunit.Run must not turn a runner refusal into a quiet `false`.
//
// The guarded form is `Ok := Codeunit.Run(...)` (the Boolean is consumed). BC's
// NavCodeunit.DoRunAsync wraps OnRun in `catch (NavBaseException)` and nothing else, so an AL
// error or a BC-raised error comes back as `false` with its text readable through
// GetLastErrorText(), while any other exception reaches the caller. The runner's two
// replacements (the static form and the instance form) caught EVERYTHING, which is how a
// refusal such as TaskScheduler.TaskExists became `ok=No lastError=[]` and resurfaced somewhere
// unrelated (the #5149 workload).
//
// Every refusal test wraps the guarded run in `asserterror`: the runner's asserterror catches a
// refusal on purpose (#2871), so reaching it proves the refusal escaped the guarded run, and a
// guarded run that swallowed it makes the asserterror itself fail ("expected an error").
codeunit 65749 "Gcr Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Gcr Assert";

    // ── The refusal escapes, static form ──

    [Test]
    procedure StaticGuarded_Refusal_EscapesByName()
    var
        Ok: Boolean;
    begin
        asserterror Ok := Codeunit.Run(Codeunit::"Gcr Refuses");

        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');
        Assert.ExpectedError('docs/scope.md#jobs');
    end;

    [Test]
    procedure StaticGuarded_RefusalFromBcCode_EscapesByName()
    var
        Ok: Boolean;
    begin
        asserterror Ok := Codeunit.Run(Codeunit::"Gcr Refuses From Bc");

        Assert.ExpectedError('out-of-scope: SQL connection');
    end;

    // ── The refusal escapes, instance form ──

    [Test]
    procedure InstanceGuarded_Refusal_EscapesByName()
    var
        Refuses: Codeunit "Gcr Refuses";
        Ok: Boolean;
    begin
        asserterror Ok := Refuses.Run();

        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');
        Assert.ExpectedError('docs/scope.md#jobs');
    end;

    [Test]
    procedure InstanceGuarded_RefusalFromBcCode_EscapesByName()
    var
        Refuses: Codeunit "Gcr Refuses From Bc";
        Ok: Boolean;
    begin
        asserterror Ok := Refuses.Run();

        Assert.ExpectedError('out-of-scope: SQL connection');
    end;

    // ── ...through a second guarded run ──

    [Test]
    procedure NestedGuarded_Refusal_CrossesBothRuns()
    var
        Ok: Boolean;
    begin
        // The inner guarded run lets the refusal out; the outer one must not catch it either.
        asserterror Ok := Codeunit.Run(Codeunit::"Gcr Nested");

        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');
    end;

    // ── The unguarded form was never swallowed: a control that the fix did not move it ──

    [Test]
    procedure StaticUnguarded_Refusal_StillRaises()
    begin
        asserterror Codeunit.Run(Codeunit::"Gcr Refuses");

        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');
    end;

    // ── What BC suppresses is still suppressed, with its text, in both forms ──

    [Test]
    procedure StaticGuarded_PlainError_ReturnsFalseWithItsText()
    var
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := Codeunit.Run(Codeunit::"Gcr Errors");

        Assert.IsFalse(Ok, 'a guarded run of a codeunit that raises an AL error returns false');
        Assert.AreEqualText('plain AL error', GetLastErrorText(), 'the inner error text is kept');
    end;

    [Test]
    procedure InstanceGuarded_PlainError_ReturnsFalseWithItsText()
    var
        Errors: Codeunit "Gcr Errors";
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := Errors.Run();

        Assert.IsFalse(Ok, 'a guarded run of a codeunit that raises an AL error returns false');
        Assert.AreEqualText('plain AL error', GetLastErrorText(), 'the inner error text is kept');
    end;

    [Test]
    procedure StaticGuarded_ErrorRaisedByBc_ReturnsFalseWithItsText()
    var
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := Codeunit.Run(Codeunit::"Gcr Bc Error");

        Assert.IsFalse(Ok, 'a guarded run of a codeunit whose Get finds nothing returns false');
        Assert.ErrorContains(GetLastErrorText(), 'does not exist', 'BC''s own error text is kept');
    end;

    [Test]
    procedure InstanceGuarded_ErrorRaisedByBc_ReturnsFalseWithItsText()
    var
        BcError: Codeunit "Gcr Bc Error";
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := BcError.Run();

        Assert.IsFalse(Ok, 'a guarded run of a codeunit whose Get finds nothing returns false');
        Assert.ErrorContains(GetLastErrorText(), 'does not exist', 'BC''s own error text is kept');
    end;

    [Test]
    procedure StaticGuarded_DivisionByZero_ReturnsFalseWithItsText()
    var
        Ok: Boolean;
    begin
        // BC remaps the CLR DivideByZeroException to an AL error, so a guarded run traps it.
        ClearLastError();
        Ok := Codeunit.Run(Codeunit::"Gcr Divides");

        Assert.IsFalse(Ok, 'a guarded run of a codeunit that divides by zero returns false');
        Assert.IsTrue(GetLastErrorText() <> '', 'the division error text is kept');
    end;

    [Test]
    procedure InstanceGuarded_DivisionByZero_ReturnsFalseWithItsText()
    var
        Divides: Codeunit "Gcr Divides";
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := Divides.Run();

        Assert.IsFalse(Ok, 'a guarded run of a codeunit that divides by zero returns false');
        Assert.IsTrue(GetLastErrorText() <> '', 'the division error text is kept');
    end;

    // ── The default, the override and the return to the default ──

    [Test]
    procedure GuardedRun_AfterARefusalEscaped_StillTrapsAnAlError()
    var
        Ok: Boolean;
    begin
        // A refusal that escaped a guarded run must leave the run's transaction bracket closed:
        // the next guarded run behaves like any other.
        asserterror Ok := Codeunit.Run(Codeunit::"Gcr Refuses");
        Assert.ExpectedError('out-of-scope: TaskScheduler.TaskExists');

        ClearLastError();
        Ok := Codeunit.Run(Codeunit::"Gcr Errors");

        Assert.IsFalse(Ok, 'a guarded run after an escaped refusal still traps an AL error');
        Assert.AreEqualText('plain AL error', GetLastErrorText(), 'with its text');
    end;

    [Test]
    procedure GuardedRun_OfAQuietCodeunit_ReturnsTrue()
    var
        Quiet: Codeunit "Gcr Quiet";
        Ok: Boolean;
    begin
        ClearLastError();
        Ok := Codeunit.Run(Codeunit::"Gcr Quiet");
        Assert.IsTrue(Ok, 'static guarded run of a codeunit that raises nothing returns true');
        Assert.AreEqualText('', GetLastErrorText(), 'and sets no error');

        Ok := Quiet.Run();
        Assert.IsTrue(Ok, 'instance guarded run of a codeunit that raises nothing returns true');
        Assert.AreEqualText('', GetLastErrorText(), 'and sets no error');
    end;
}
