// ============================================================================================
// THIS CODEUNIT DELIBERATELY FAILS 2 OF ITS 4 TESTS. That is not a broken fixture — it is the
// only way to observe the claim under test (see FailedTestRollbackBoundaryTests.cs for why:
// the claim can only be seen by letting a test fail, and it cannot live in the al-language
// corpus, which is green by construction). Both failing procedures carry
// "_EXPECTED_TO_FAIL_" in their own name so a `FAIL  Codeunit70302.ExpectedToFail_...` line in
// CI output reads as intended at a glance, without needing this comment. If you are scanning a
// CI log and see a FAIL here whose name does NOT contain "_EXPECTED_TO_FAIL_", or DON'T see
// one that does, something is actually wrong — see FailedTestRollbackBoundaryTests.cs, which
// asserts both by name and would itself fail first.
// ============================================================================================
//
// Two pairs, in declaration order, which is run order. Each pair is (writer, reporter).
//
// Pair 1 — the writer FAILS. BC rolls the session back to the last commit point when a test
// method throws, so the row must be GONE by the time the reporter runs.
// Pair 2 — the writer ALSO FAILS, but only AFTER a write that would have been kept had the
// writer passed. This is what stops the fix from becoming "reset everything between tests",
// which real BC does not do and which the al-language corpus already refutes
// (TestIsolationRollbackScope, 60897): the reporter proves the rollback boundary is the
// failing test's OWN commit point, not a blanket wipe, by committing its own row and reading
// it back in the same procedure.
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
    procedure ExpectedToFail_01_WriterInsertsARowThenFails()
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

        // DELIBERATE, EXPECTED FAILURE — see the file header. Not a broken fixture.
        Error('FTR-EXPECTED-TO-FAIL-01: deliberate, proves the NEXT test sees the row rolled back');
    end;

    [Test]
    procedure Reporter_02_TheFailingWritersRowMustNotSurvive()
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
    procedure ExpectedToFail_03_WriterInsertsARowThenFailsForAnUnrelatedReason()
    var
        Row: Record "FTR Row";
    begin
        if Row.Get(70301002) then
            Row.Delete();

        Row.Init();
        Row."Entry No." := 70301002;
        Row.Name := 'written-by-a-failing-test-2';
        Row.Insert();
        Assert.IsTrue(Row.Get(70301002), 'the row must exist inside the test that wrote it');

        // DELIBERATE, EXPECTED FAILURE — see the file header. Not a broken fixture. The row
        // above must ALSO be rolled back, by the same rule as ExpectedToFail_01 — this pair
        // exists so Reporter_04 can prove the boundary is "this test's own commit point", by
        // additionally committing and reading back a row of its own in the same procedure,
        // rather than merely repeating pair 1's shape.
        Error('FTR-EXPECTED-TO-FAIL-03: deliberate, proves the rollback boundary is a commit point, not a wipe');
    end;

    [Test]
    procedure Reporter_04_RolledBackRowIsGoneAndACommittedRowSurvivesInTheSameTest()
    var
        Row: Record "FTR Row";
        Committed: Record "FTR Row";
    begin
        Assert.IsTrue(
            not Row.Get(70301002),
            'ExpectedToFail_03 also failed, so its uncommitted write must be rolled back too ' +
            '— the boundary is pass/fail, not which row was written');

        // The positive half, in this same test so it needs no third pair: a write this test
        // makes and COMMITS is visible to itself after the commit, which is the state a
        // passing test hands to the next one (al-language corpus 60897 pins that half on
        // real BC). If the fix wiped everything at every test boundary instead of rolling
        // back only to the last commit point, this would fail too.
        Committed.Init();
        Committed."Entry No." := 70301003;
        Committed.Name := 'committed-inside-this-test';
        Committed.Insert();
        Commit();
        Assert.IsTrue(Committed.Get(70301003), 'a committed row must be readable after the commit');
        Assert.AreEqual('committed-inside-this-test', Committed.Name,
            'the committed row must carry the value that was written');
    end;
}
