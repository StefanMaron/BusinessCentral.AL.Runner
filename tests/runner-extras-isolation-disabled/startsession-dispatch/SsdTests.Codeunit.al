// Issue #2826 — AL coverage of the StartSession dispatch path (#2733, #2752), preserved after
// #2805 made StartSession refuse from inside a [Test] under the default isolation mode.
//
// WHY THIS SUITE IS NOT IN tests/runner-extras
// --------------------------------------------
// Real BC refuses StartSession from inside a [Test] unless the TestRunner declares
// TestIsolation = Disabled — pinned upstream by corpus codeunit 60397 on all eight BC
// versions, and the runner implements it. Isolation is a process-global CLI flag with no
// per-bundle override, and the runner-extras CI step is one invocation under the default. So
// the dispatch path BEHIND the guard is reachable from AL only in a separate invocation, which
// is what this directory and its own bc-tests.yml step exist for.
//
// WHY THE WORKER MUST BE PRECOMPILED
// ----------------------------------
// BC's compiler emits `trigger OnRun()` as an async OnRunAsync on the concrete type for the
// dependency-compile path, and as a synchronous OnRun override for the runner's own emit. Both
// are virtuals on NavCodeunit with an EMPTY base body, so a sync-name-only reflection lookup
// always resolves — it binds and runs the empty base. SessionPatches.AlRunnerStartSession did
// exactly that, so StartSession on any Base Application / System Application / ISV worker
// returned true having executed none of its AL (#2733).
//
// A source-compiled sibling app is NOT enough: measured, a `-dep` fixture bundle compiled from
// .al alongside the test bundle carries the SYNC flavour and passes against the unfixed runner,
// so it proves nothing. The same distinction is recorded in
// tests/runner-extras/depapp-dictionary-main.
//
// Base Application codeunit 7002 "Price Calculation - V16" is the smallest precompiled worker
// with an OnRun whose effect is observable and needs no setup: it rewrites its own rows in
// "Price Calculation Setup". Codeunit 7003 "Price Calculation - V15" is its twin, which makes
// the pair differential.
//
// EVERY ASSERTION READS A ROW THE WORKER'S BODY WROTE. None asserts that StartSession returned,
// or that it did not throw — the broken behaviour satisfied both, which is precisely why it
// went unnoticed. A test here that only ran the path would restore the appearance of this
// coverage without its substance.
codeunit 61104 "Ssd StartSession Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Ssd Assert";

    [Test]
    procedure PrecompiledCodeunit_StartSession_RunsTheWorkersOnRunBody()
    var
        PriceCalculationSetup: Record "Price Calculation Setup";
        SessionId: Integer;
        Started: Boolean;
    begin
        PriceCalculationSetup.DeleteAll();

        Started := StartSession(SessionId, Codeunit::"Price Calculation - V16");

        Assert.IsTrue(Started, 'StartSession must report that it started the session.');
        Assert.IsTrue(SessionId > 0,
            StrSubstNo('BC guarantees a non-zero session id after StartSession; got %1.', SessionId));

        PriceCalculationSetup.SetRange(
            Implementation, PriceCalculationSetup.Implementation::"Business Central (Version 16.0)");
        Assert.AreEqual(2, PriceCalculationSetup.Count(),
            'Codeunit 7002''s OnRun body inserts its Purchase and Sale setup rows. The empty inherited ' +
            'NavCodeunit.OnRun returns just as quietly and writes nothing.');
        Assert.IsTrue(PriceCalculationSetup.FindFirst(), 'the rows the worker wrote must be readable');
        Assert.IsTrue(PriceCalculationSetup.Default,
            'OnRun ends in ModifyAll(Default, true), so the rows it wrote must carry Default = true.');
    end;

    // Differential: starting the V15 worker must run THAT worker, not the V16 one. Together
    // with the test above this rules out "some codeunit ran" as the explanation.
    [Test]
    procedure PrecompiledCodeunit_StartSession_RunsTheWorkerItWasGiven_AndNoOther()
    var
        PriceCalculationSetup: Record "Price Calculation Setup";
        SessionId: Integer;
    begin
        PriceCalculationSetup.DeleteAll();

        Assert.IsTrue(StartSession(SessionId, Codeunit::"Price Calculation - V15"),
            'StartSession must report that it started the session.');

        PriceCalculationSetup.SetRange(
            Implementation, PriceCalculationSetup.Implementation::"Business Central (Version 15.0)");
        Assert.AreEqual(2, PriceCalculationSetup.Count(),
            'Codeunit 7003''s OnRun body must have inserted its two setup rows.');

        PriceCalculationSetup.SetRange(
            Implementation, PriceCalculationSetup.Implementation::"Business Central (Version 16.0)");
        Assert.AreEqual(0, PriceCalculationSetup.Count(),
            'Starting the V15 worker must not have run the V16 worker''s body.');
    end;

    // Negative: an object id with no codeunit behind it must fail LOUDLY rather than return
    // true having done nothing — the exact failure mode this whole area is about.
    // Deliberately asserts only the error text: an error rolls the write transaction back to
    // the last commit point, so any table read placed after this asserterror would report the
    // state before the test began (see tests/al-language error-handling/TestAssertErrorRollback.al).
    [Test]
    procedure PrecompiledCodeunit_StartSession_OnAnIdWithNoCodeunit_FailsLoudly()
    var
        SessionId: Integer;
    begin
        asserterror StartSession(SessionId, 1999999);

        Assert.Contains(GetLastErrorText(), '1999999',
            'A StartSession naming an object id with no codeunit behind it must fail and name that id.');
    end;
}
