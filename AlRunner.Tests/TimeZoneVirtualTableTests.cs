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
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TimeZoneVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TimeZoneVirtualTable");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s.");
        }
        // WaitForExit(int) returns as soon as the process exits and does NOT wait for the
        // async BeginOutputReadLine/BeginErrorReadLine callbacks to drain — only the
        // parameterless overload does. Without this the last stdout lines can still be in
        // flight when we read outSb, and an Assert.Contains on a line the runner definitely
        // printed fails intermittently, the more so the more loaded the machine is (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void TimeZone_HostZones_AllFixtureTestsPass()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-tzv-tests", "cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The table answers rows at all — this is the direct RED this fixes.
            Assert.Contains("PASS  Codeunit60781.TimeZone_IsNotEmpty", stdout);
            // "No." is a sequence over the host's list, so a provider that inserted rows
            // without numbering them, or numbered from 0, fails here.
            Assert.Contains("PASS  Codeunit60781.TimeZone_NumbersStartAtOneAndIncrementWithNoGaps", stdout);
            // The one that rules out N blank rows, which would satisfy both assertions above.
            Assert.Contains("PASS  Codeunit60781.TimeZone_EveryRowHasANonBlankId", stdout);
            // Get and FindSet must agree, so the row Get returns is a real row and not a
            // separately-built one.
            Assert.Contains("PASS  Codeunit60781.TimeZone_GetOne_AgreesWithTheFirstRowOfFindSet", stdout);
            // Negative: a number past the end still answers false. Passes against an EMPTY
            // table too, which is exactly why it is not sufficient on its own.
            Assert.Contains("PASS  Codeunit60781.TimeZone_GetOnANumberPastTheEnd_ReturnsFalse", stdout);
            // Negative: filtering discriminates.
            Assert.Contains("PASS  Codeunit60781.TimeZone_FilterOnNumber_DiscriminatesBetweenRows", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
