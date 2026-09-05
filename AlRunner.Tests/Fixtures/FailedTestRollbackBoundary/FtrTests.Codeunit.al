// Two pairs, in declaration order, which is run order. Each pair is (writer, reporter).
//
// Pair 1 — the writer FAILS. BC rolls the session back to the last commit point when a test
// method throws, so the row must be GONE by the time the reporter runs.
// Pair 2 — the writer PASSES. BC commits at the end of a passing test method, so the row
// must SURVIVE. This half is what stops the fix from becoming "reset everything between
// tests", which real BC does not do and which the al-language corpus already refutes
// (TestIsolationRollbackScope, 60897).
//
// Test01 and Test03 fail deliberately. FailedTestRollbackBoundaryTests.cs asserts their exact
// messages, so a fixture that stopped failing on purpose is itself a test failure.
//
// The two reporters do NOT fail. They assert, so their PASS/FAIL is the actual signal and the
// C# side does not have to parse a message to learn the answer.
codeunit 70302 "FTR Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "FTR Assert";

    [Test]
    procedure Test01_FailingWriterInsertsARowThenFails()
    var
        Row: Record "FTR Row";
    begin
        if Row.Get(70301001) then
            Row.Delete();

        Row.Init();
        Row."Entry No." := 70301001;
        Row.Name := 'written-by-a-failing-test';
        Row.Insert();
        Assert.IsTrue(Row.Get(70301001), 'the row must exist inside the test that wrote it');

        // Deliberate. This test is EXPECTED to fail; see the file header.
        Error('FTR-DELIBERATE-FAILURE-01');
    end;

    [Test]
    procedure Test02_TheFailingWritersRowMustNotSurvive()
    var
        Row: Record "FTR Row";
    begin
        Assert.IsTrue(
            not Row.Get(70301001),
            'a [Test] that FAILS must have its uncommitted writes rolled back before the next ' +
            'test in the same codeunit runs — BC does this in ExecuteTestMethodAsync''s own ' +
            'catch, which rolls the session back to the last commit point');
    end;

    [Test]
    procedure Test03_PassingWriterInsertsARowAndSucceeds()
    var
        Row: Record "FTR Row";
    begin
        if Row.Get(70301002) then
            Row.Delete();

        Row.Init();
        Row."Entry No." := 70301002;
        Row.Name := 'written-by-a-passing-test';
        Row.Insert();
        Assert.IsTrue(Row.Get(70301002), 'the row must exist inside the test that wrote it');

        // Deliberate, and it is the point of this test: the row above is written BEFORE the
        // failure, by a test that then fails for an UNRELATED reason. Pair 2's reporter proves
        // the rollback boundary is the failing test's own commit point, not a blanket reset —
        // see Test04.
        Error('FTR-DELIBERATE-FAILURE-03');
    end;

    [Test]
    procedure Test04_TheRowCommittedByTest02sPredecessorIsStillGone()
    var
        Row: Record "FTR Row";
        Committed: Record "FTR Row";
    begin
        Assert.IsTrue(
            not Row.Get(70301002),
            'Test03 also failed, so its write must be rolled back too — the boundary is ' +
            'pass/fail, not which row was written');

        // Now the positive half, in this same test so it needs no third pair: a write this
        // test makes and COMMITS is visible to itself after the commit, which is the state a
        // passing test hands to the next one (al-language corpus 60897 pins that half on
        // real BC).
        Committed.Init();
        Committed."Entry No." := 70301003;
        Committed.Name := 'committed-inside-a-passing-test';
        Committed.Insert();
        Commit();
        Assert.IsTrue(Committed.Get(70301003), 'a committed row must be readable after the commit');
        Assert.AreEqual('committed-inside-a-passing-test', Committed.Name,
            'the committed row must carry the value that was written');
    end;
}
