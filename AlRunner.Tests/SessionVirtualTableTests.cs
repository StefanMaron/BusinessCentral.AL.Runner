// SessionVirtualTableTests — issue #2940.
//
// A RUNNER-MECHANISM test, not a claim about what real BC does. It proves that OUR OWN
// population of the Session system virtual table (2000000009) answers a row at all, and that
// the row's identity columns are READ BACK from the skeleton NavSession rather than made up.
//
// Before the fix, table 2000000009 had no managed provider, so GetDataAccessForTableCore fell
// through to the plain in-memory temp store and every read answered zero rows: FindSet() was
// false, Count() was 0, and no AL caller could tell that apart from an idle server. Measured
// RED on this fixture before the fix: 6 of the 8 fixture tests failed. The two that passed
// are the two NEGATIVE ones, which pass vacuously against an empty table — that is exactly
// why they are not sufficient on their own and why the six positives exist.
//
// NO ASSERTION NAMES A CONCRETE IDENTITY, and that is deliberate twice over:
//   * the connection id, the user name and the host name are properties of the session and
//     the machine, so pinning a literal would fail for reasons that are configuration rather
//     than bugs;
//   * comparing the table against SessionId() / UserId() instead is the STRONGER claim, since
//     it is the one a fabricated value cannot satisfy.
//
// The BEHAVIORAL claim — that a real service tier answers this table with exactly one row,
// the reading session, flagged "My Session" — is proven upstream against a live BC tier by
// "Test Session Virtual Table" in StefanMaron/BusinessCentral.AL.Language.Tests, per
// .claude/rules/bc-behavior-tests-go-upstream.md.
using Xunit;

namespace AlRunner.Tests;

public sealed class SessionVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "SessionVirtualTable");

    [Fact]
    public async Task Session_ReadingSession_AllFixtureTestsPass()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected a clean run (every fixture test must pass). exit={r.ExitCode}\n{r}");

        // The table answers a row at all, and exactly one row claims to be this session —
        // this is the direct RED this fixes.
        r.AssertPassed("Codeunit70561.Session_HasARowForTheReadingSession");
        // Read-it-back, the whole point: a populator that invented a connection id passes
        // the row-exists assertion and fails this one.
        r.AssertPassed("Codeunit70561.Session_MySessionRow_ConnectionIdIsWhatSessionIdReturns");
        // Same for the user: rules out a row whose "User ID" is blank or someone else.
        r.AssertPassed("Codeunit70561.Session_MySessionRow_UserIdIsWhatUserIdReturns");
        // Get() reaches the row by primary key, so the key columns and the row's own
        // "Connection ID" must agree.
        r.AssertPassed("Codeunit70561.Session_Get_ByConnectionId_FindsTheSameRow");
        // Rules out one row inserted with BC's per-field defaults everywhere but the key.
        r.AssertPassed("Codeunit70561.Session_MySessionRow_CarriesALoginDateAndTime");
        r.AssertPassed("Codeunit70561.Session_MySessionRow_CarriesAHostName");
        // Negative: a connection id belonging to no session still answers false. Passes
        // against an EMPTY table too, which is why it is not sufficient on its own.
        r.AssertPassed("Codeunit70561.Session_GetOnAConnectionIdThatIsNotThisSession_ReturnsFalse");
        // Negative: nothing may claim to be a session other than this one.
        r.AssertPassed("Codeunit70561.Session_FilterOnMySessionFalse_SelectsNothing");
        // #3230: both columns read back off the session's Active Session row.
        r.AssertPassed("Codeunit70561.Session_MySessionRow_ApplicationNameIsActiveSessionsClientType");
        r.AssertPassed("Codeunit70561.Session_MySessionRow_DatabaseNameIsActiveSessionsDatabaseName");
        r.AssertNoFailures();
    }
}
