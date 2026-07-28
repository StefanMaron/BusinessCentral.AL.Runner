// SingleInstance codeunit lifetime.
//
// WHAT THESE TESTS DO AND DO NOT PROVE — read before trusting them as a regression gate.
// They pin the SingleInstance CONTRACT: one instance per test, state kept across scope
// boundaries, and (the control) a per-call codeunit NOT sharing state. They do NOT reproduce
// the defect that motivated them. That defect needs a scope to be genuinely Disposed rather
// than merely detached, and none of the shapes reachable from a small AL test — a nested local
// procedure, event dispatch, Codeunit.Run, or a Codeunit.Run that errors — produced one here;
// all four passed against the unfixed runner. The real reproduction is the Pageworks suite
// (5 tests, Base App codeunit 347 "Auto Format".GetGLSetup), where instrumentation showed the
// cached instance's tree DISPOSED immediately before the NRE and live after the fix.
//
// The defect: the runner cached a SingleInstance codeunit in a plain C# dictionary while the
// instance stayed parented on whichever AL scope first resolved it. When that scope's subtree
// was disposed the instance went with it, and NavApplicationObjectBaseHandle.Target returns
// NULL for a disposed tree instead of rebuilding — so every global record handle on the
// codeunit silently read back null and the next access NRE'd with no message inside BC.
//
// The control test matters: the fix roots the instance on the session, and a wrong version of
// that fix would root every codeunit there and silently fuse unrelated callers' state.
//
// ONE TEST PER CODEUNIT
// The runner's default isolation is per-CODEUNIT (matching BC's "Test Runner - Isol.
// Codeunit"), and the SingleInstance cache is reset at that boundary. Two of these tests would
// otherwise share one cached instance and the second would read the first's value.

codeunit 62051 "SIS Tests"
{
    Subtype = Test;

    local procedure SeedSetup(CurrencyCode: Code[10])
    var
        Setup: Record "SIS Setup";
    begin
        Setup.DeleteAll();
        Setup.Init();
        Setup."Primary Key" := 'MAIN';
        Setup."Currency Code" := CurrencyCode;
        Setup.Insert();
    end;

    /// Constructs the SingleInstance codeunit from inside a nested scope that then returns,
    /// so the tree it was parented on is gone by the time the caller reads it again.
    /// Goes through event dispatch, which is how the real failure is reached.
    local procedure PrimeFromNestedScope(): Code[10]
    var
        Publisher: Codeunit "SIS Publisher";
    begin
        exit(Publisher.Resolve());
    end;

    [Test]
    procedure SingleInstanceSurvivesCreatingScopeExit()
    var
        Cache: Codeunit "SIS Cache";
        Publisher: Codeunit "SIS Publisher";
        Primed: Code[10];
    begin
        SeedSetup('EUR');

        Primed := PrimeFromNestedScope();
        if Primed <> 'EUR' then
            Error('The nested scope itself read the wrong value: expected <EUR>, got <%1>.', Primed);

        // The scope that built the instance has now returned. Reading through it again is
        // what used to come back null and NRE.
        if Cache.GetCurrencyCode() <> 'EUR' then
            Error('After the creating scope exited, the cached value read back as <%1>, expected <EUR>.',
                Cache.GetCurrencyCode());

        // And again through dispatch, from this scope.
        if Publisher.Resolve() <> 'EUR' then
            Error('Re-dispatching after the creating scope exited read back <%1>, expected <EUR>.',
                Publisher.Resolve());

        // Proves it is the SAME instance and not a silently rebuilt one — a fresh instance
        // would have re-read the record and pushed this to 2. Without this the test would
        // still pass if the runner just rebuilt the codeunit on every resolution, which is
        // not the SingleInstance contract.
        if Cache.GetReadCount() <> 1 then
            Error('Expected exactly 1 record read across both calls (one shared instance), got %1.',
                Cache.GetReadCount());
    end;
}

codeunit 62055 "SIS Run Scope Tests"
{
    Subtype = Test;

    local procedure SeedSetup(CurrencyCode: Code[10])
    var
        Setup: Record "SIS Setup";
    begin
        Setup.DeleteAll();
        Setup.Init();
        Setup."Primary Key" := 'MAIN';
        Setup."Currency Code" := CurrencyCode;
        Setup.Insert();
    end;

    [Test]
    procedure SingleInstanceSurvivesACodeunitRunScope()
    var
        Cache: Codeunit "SIS Cache";
    begin
        SeedSetup('GBP');

        // Codeunit.Run gives the resolution its own scope, which is then torn down. The
        // instance cached from inside it must still be usable afterwards.
        if not Codeunit.Run(Codeunit::"SIS Runner") then
            Error('Priming through Codeunit.Run failed: %1', GetLastErrorText());

        if Cache.GetCurrencyCode() <> 'GBP' then
            Error('After the Codeunit.Run scope was torn down, the cached value read back as <%1>, expected <GBP>.',
                Cache.GetCurrencyCode());
        if Cache.GetReadCount() <> 1 then
            Error('Expected exactly 1 record read across the Run and the later call (one shared instance), got %1.',
                Cache.GetReadCount());
    end;
}

codeunit 62058 "SIS Failed Scope Tests"
{
    Subtype = Test;

    local procedure SeedSetup(CurrencyCode: Code[10])
    var
        Setup: Record "SIS Setup";
    begin
        Setup.DeleteAll();
        Setup.Init();
        Setup."Primary Key" := 'MAIN';
        Setup."Currency Code" := CurrencyCode;
        Setup.Insert();
    end;

    [Test]
    procedure SingleInstanceSurvivesAScopeThatErrored()
    var
        Cache: Codeunit "SIS Cache";
    begin
        SeedSetup('CHF');

        // The priming scope errors, so it is torn down through the rollback path.
        if Codeunit.Run(Codeunit::"SIS Failing Runner") then
            Error('The priming codeunit was supposed to fail.');

        if Cache.GetCurrencyCode() <> 'CHF' then
            Error('After a failed scope was torn down, the cached value read back as <%1>, expected <CHF>.',
                Cache.GetCurrencyCode());
    end;
}

codeunit 62056 "SIS Per Call Tests"
{
    Subtype = Test;

    local procedure BumpInNestedScope()
    var
        PerCall: Codeunit "SIS Per Call";
    begin
        PerCall.Bump();
    end;

    [Test]
    procedure NonSingleInstanceCodeunitDoesNotShareStateAcrossScopes()
    var
        PerCall: Codeunit "SIS Per Call";
    begin
        // The opposite direction, and the reason this pair exists: rooting a codeunit on the
        // session must NOT turn every codeunit into a shared one. A plain codeunit gets a
        // fresh instance per AL variable, so the nested scope's increments are invisible here.
        // Without this, "make it survive scope exit" could be satisfied by caching everything,
        // which would silently fuse unrelated callers' state.
        BumpInNestedScope();
        BumpInNestedScope();

        if PerCall.GetBumps() <> 0 then
            Error('A non-SingleInstance codeunit leaked state across scopes: expected 0 bumps in a ' +
                'fresh instance, got %1.', PerCall.GetBumps());

        PerCall.Bump();
        if PerCall.GetBumps() <> 1 then
            Error('A non-SingleInstance codeunit did not keep its OWN state: expected 1, got %1.',
                PerCall.GetBumps());
    end;
}
