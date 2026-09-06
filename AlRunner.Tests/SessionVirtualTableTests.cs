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
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class SessionVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "SessionVirtualTable");

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
    public void Session_ReadingSession_AllFixtureTestsPass()
    {
        var cacheDir = TestScratch.Dir("al-runner-svt-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The table answers a row at all, and exactly one row claims to be this session —
            // this is the direct RED this fixes.
            Assert.Contains("PASS  Codeunit70561.Session_HasARowForTheReadingSession", stdout);
            // Read-it-back, the whole point: a populator that invented a connection id passes
            // the row-exists assertion and fails this one.
            Assert.Contains("PASS  Codeunit70561.Session_MySessionRow_ConnectionIdIsWhatSessionIdReturns", stdout);
            // Same for the user: rules out a row whose "User ID" is blank or someone else.
            Assert.Contains("PASS  Codeunit70561.Session_MySessionRow_UserIdIsWhatUserIdReturns", stdout);
            // Get() reaches the row by primary key, so the key columns and the row's own
            // "Connection ID" must agree.
            Assert.Contains("PASS  Codeunit70561.Session_Get_ByConnectionId_FindsTheSameRow", stdout);
            // Rules out one row inserted with BC's per-field defaults everywhere but the key.
            Assert.Contains("PASS  Codeunit70561.Session_MySessionRow_CarriesALoginDateAndTime", stdout);
            Assert.Contains("PASS  Codeunit70561.Session_MySessionRow_CarriesAHostName", stdout);
            // Negative: a connection id belonging to no session still answers false. Passes
            // against an EMPTY table too, which is why it is not sufficient on its own.
            Assert.Contains("PASS  Codeunit70561.Session_GetOnAConnectionIdThatIsNotThisSession_ReturnsFalse", stdout);
            // Negative: nothing may claim to be a session other than this one.
            Assert.Contains("PASS  Codeunit70561.Session_FilterOnMySessionFalse_SelectsNothing", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
