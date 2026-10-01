// TimeZoneVirtualTableTests — issue #2584.
//
// A RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR OWN
// population of the "Time Zone" system virtual table (2000000164) answers rows at all, and
// that they are numbered and identified rather than blank.
//
// Before the fix, table 2000000164 had no managed provider, so GetDataAccessForTableCore fell
// through to the plain in-memory temp store and every read answered zero rows: Get() silently
// returned false and FindSet() raised.
//
// EVERY ASSERTION IS ABOUT SHAPE, NEVER A SPECIFIC ZONE ID, and that is deliberate. BC's own
// TimeZoneDataProvider enumerates the HOST's TimeZoneInfo.GetSystemTimeZones(), so the row set
// is a property of the machine: Windows ids on a Windows-hosted SaaS tier, IANA ids on this
// Linux host. Asserting "W. Europe Standard Time" would fail here for a reason that is
// documented behavior, not a bug — see docs/limitations.md, "Time Zone ids follow the host".
//
// The fixture is shaped so a provider inserting N BLANK rows would fail: the count and the
// 1..N numbering would both pass, and the non-blank-ID assertion would not. The two negative
// tests (a number past the end, and a filter selecting nothing) close the rest.
//
// The BEHAVIORAL claim is proven upstream against a live BC service tier by
// "Test Time Zone Virtual Table" in StefanMaron/BusinessCentral.AL.Language.Tests, per
// .claude/rules/bc-behavior-tests-go-upstream.md — asserting the same shape, for the same
// reason: no host-specific id can be asserted in a corpus that runs on more than one host.
using Xunit;

namespace AlRunner.Tests;

public sealed class TimeZoneVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TimeZoneVirtualTable");

    [Fact]
    public async Task TimeZone_HostZones_AllFixtureTestsPass()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"expected a clean run (every fixture test must pass). exit={r.ExitCode}\n{r}");

        // The table answers rows at all — this is the direct RED this fixes.
        r.AssertPassed("Codeunit60781.TimeZone_IsNotEmpty");
        // "No." is a sequence over the host's list, so a provider that inserted rows
        // without numbering them, or numbered from 0, fails here.
        r.AssertPassed("Codeunit60781.TimeZone_NumbersStartAtOneAndIncrementWithNoGaps");
        // The one that rules out N blank rows, which would satisfy both assertions above.
        r.AssertPassed("Codeunit60781.TimeZone_EveryRowHasANonBlankId");
        // Get and FindSet must agree, so the row Get returns is a real row and not a
        // separately-built one.
        r.AssertPassed("Codeunit60781.TimeZone_GetOne_AgreesWithTheFirstRowOfFindSet");
        // Negative: a number past the end still answers false. Passes against an EMPTY
        // table too, which is exactly why it is not sufficient on its own.
        r.AssertPassed("Codeunit60781.TimeZone_GetOnANumberPastTheEnd_ReturnsFalse");
        // Negative: filtering discriminates.
        r.AssertPassed("Codeunit60781.TimeZone_FilterOnNumber_DiscriminatesBetweenRows");
        r.AssertNoFailures();
    }
}
