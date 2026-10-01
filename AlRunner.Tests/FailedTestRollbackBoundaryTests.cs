// FailedTestRollbackBoundaryTests — issue #2523.
//
// This is a RUNNER-MECHANISM test. It pins WHEN the runner unwinds the row store at the
// [Test] boundary, which is a property of our own TestExecutor.RunOne, not a claim we are
// asking anyone to take on faith about BC.
//
// What BC does, and where that is established:
//
//   * A test that PASSES has its writes committed. Real BC does this in
//     NavTestCodeunit.ExecuteTestMethodAsync — `if (transactionModel ==
//     TestTransactionModel.AutoCommit) activeSession.Commit();` on the success path, and
//     AutoCommit is the default. The al-language corpus pins the OBSERVABLE half of that
//     against a live service tier: "Test Isolation Rollback Scope" (60897) writes a row in
//     one test without an explicit Commit and reads it back in the next test of the same
//     codeunit, green on every supported BC version. So writes must survive here too, and a
//     fix that reset state between tests would be wrong.
//
//   * A test that FAILS has its writes rolled back. Same BC method, the catch around the
//     whole invocation:
//
//         catch (Exception ex)
//         {
//             if (activeSession.IsTransactionActive())
//                 activeSession.Rollback();
//             MarkExceptions(ex);
//         }
//
//     and Rollback() unwinds to the last commit point, which the corpus pins on real BC for
//     the trapped-error case in "Test AssertError Rollback" (60943): an uncommitted write
//     before an unrelated error is undone, and Commit() moves the surviving boundary forward.
//
// The runner did the first and not the second. TestExecutor.RunOne already called
// MarkCommitPoint() before every test and already had RollbackToCommitPoint() wired up — but
// only for [TransactionModel(TransactionModel::AutoRollback)] (#2400), which took the
// AutoRollback arm of BC's body and left the catch arm unimplemented. So a failing test's
// writes stayed visible to every later test in the codeunit.
//
// Why this lives here and not in the al-language corpus: the claim can only be observed by
// letting a test FAIL, and the corpus is green by construction — a deliberately failing test
// codeunit cannot live in it. That is the "behaviour not expressible in the corpus" escape
// hatch in .claude/rules/bc-behavior-tests-go-upstream.md, taken deliberately and named here
// rather than passed over. The two corpus suites above carry as much of the BC half as a
// service tier can adjudicate.
//
// The fixture fails two of its four tests ON PURPOSE, and this test asserts their exact
// messages — so a fixture that silently stopped failing is itself a failure here. Both
// deliberately-failing procedures carry "ExpectedToFail_" in their own AL procedure name, so
// a `FAIL  Codeunit70302.ExpectedToFail_...` line in CI output reads as intended at a glance —
// see #2739, where an unrelated notice worded as a problem on a GREEN leg cost real
// investigation time; this fixture must never add a second source of that same confusion.
using Xunit;

namespace AlRunner.Tests;

public sealed class FailedTestRollbackBoundaryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "FailedTestRollbackBoundary");

    [Fact]
    public async Task FailedTest_WritesAreRolledBack_PassingTestWritesAreNot()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);
        bool Failed(string method) => r.StatusOf("Codeunit70302." + method) is "fail" or "error";
        string? Message(string method) => r.Tests.FirstOrDefault(t => t.Name == "Codeunit70302." + method).Message;

        // The two deliberate failures must still be failing, by their EXPECTED-TO-FAIL
        // name, and for their own reason. If the fixture ever stops raising these, the two
        // reporters below would pass vacuously — there would be no failed test whose writes
        // could leak.
        Assert.True(Failed("ExpectedToFail_01_WriterInsertsARowThenFails"),
            $"the fixture's first EXPECTED failure must still fail.\n{r}");
        Assert.Contains("FTR-EXPECTED-TO-FAIL-01", Message("ExpectedToFail_01_WriterInsertsARowThenFails"));
        Assert.True(Failed("ExpectedToFail_03_WriterInsertsARowThenFailsForAnUnrelatedReason"),
            $"the fixture's second EXPECTED failure must still fail.\n{r}");
        Assert.Contains("FTR-EXPECTED-TO-FAIL-03", Message("ExpectedToFail_03_WriterInsertsARowThenFailsForAnUnrelatedReason"));

        // The claim. Both reporters must PASS: the failing writers' rows are gone, and a
        // committed row is readable. Before the fix these both failed, because the failing
        // tests' uncommitted Inserts survived into them.
        Assert.True(r.StatusOf("Codeunit70302.Reporter_02_TheFailingWritersRowMustNotSurvive") == "pass",
            "a FAILING test's uncommitted write must be rolled back before the next test in "
            + $"the same codeunit runs.\n{r}");
        Assert.True(
            r.StatusOf("Codeunit70302.Reporter_04_RolledBackRowIsGoneAndACommittedRowSurvivesInTheSameTest") == "pass",
            "the rollback boundary is the failing test's own commit point, and a committed "
            + $"row inside the same test must still be readable.\n{r}");

        // Exactly two failures, both by the EXPECTED-TO-FAIL name. Guards against a fix
        // that unwinds too much and takes the reporters down with it, or a fixture edit
        // that adds an unexpected failure under a name this test does not recognize.
        var fixtureFailures = r.Tests
            .Where(t => t.Status is "fail" or "error" && t.Name.StartsWith("Codeunit70302.", StringComparison.Ordinal))
            .Select(t => t.Name).ToList();
        var failCount = fixtureFailures.Count;
        Assert.True(failCount == 2,
            $"expected exactly the 2 EXPECTED failures, saw {failCount}.\n{r}");
        var expectedFailCount = fixtureFailures.Count(n => n.Contains(".ExpectedToFail_", StringComparison.Ordinal));
        Assert.True(expectedFailCount == 2,
            "every FAIL in this fixture must carry the ExpectedToFail_ name, so CI output "
            + $"never shows an unmarked failure here. saw {expectedFailCount} marked of "
            + $"{failCount} total.\n{r}");

        // A run with failing tests exits non-zero; that is the fixture working, not a
        // problem. Asserted so the expectation is explicit rather than unstated.
        Assert.True(r.ExitCode != 0, $"expected a non-zero exit from a bundle with deliberate failures, got {r.ExitCode}");
    }
}
