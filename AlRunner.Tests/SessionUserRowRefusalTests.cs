// SessionUserRowRefusalTests — the #2941 review finding.
//
// RUNNER-MECHANISM tests. That BC's session user is a row in the User table is plain BC
// behaviour and is adjudicated upstream by a real service tier (corpus codeunit 60991). What
// these pin is the runner's own seed: RecordPatches.EnsureUserSystemTableRowSeeded must be able
// to tell its three outcomes apart, and must not claim to have written a row it did not write.
//
// THE DEFECT
//   The seed inserted through ALInsert(DataError.TrapError, …) and DISCARDED the bool. TrapError
//   exists precisely to report a refusal as `false` instead of raising, so throwing that bool
//   away threw away the only signal there was: "the row was written" and "the insert was refused"
//   became indistinguishable, neither was logged, and _userRowSeededForThisBundle was set true
//   either way. Both of the method's early-return paths were loud; only the path that did the
//   work was silent.
//
// THE RED THESE TESTS ENCODE
//   On the already-present path the OLD code ran its success trace and logged
//   "UserSystemTable: seeded User row 'TESTUSER'" — a false claim, because the insert had been
//   refused by the primary key and the row present was the one the Install trigger wrote. The
//   NEW code logs "was already present". That is the discriminator
//   SeedOnAnAlreadyPresentRow_SaysAlreadyPresent_NotSeeded asserts, and it fails against the
//   pre-fix runner.
//
// WHAT THE REVIEW PREDICTED, AND WHAT WAS MEASURED
//   The review named a concrete bite: BC's User table has a unique key on "User Name", so a
//   --test-data backup containing a TESTUSER would refuse the seed and silently defeat #2296.
//   MEASURED on BC 28.1.49838.53910 (SessionUserRowNameCollision, below) it does not: the
//   runner's in-memory provider enforces only the PRIMARY key on "User Security ID", so the
//   seed lands and the session user keeps its own row. The run is instead left holding two rows
//   with the same user name, which real BC would refuse — a separate provider gap, filed as
//   AlRunner#2983 rather than fixed here, and NOT asserted by the fixture, which would be
//   writing a wrong number into a test.
//
//   Consequence worth stating plainly: the Refused branch is correct and is what the review
//   asked for, but nothing reachable from AL can trigger it today. It is exercised by the
//   exception path only. The name-collision fixture is the canary — if unique secondary keys
//   ever become enforceable, its "the session user still gets its own row" test fails, which is
//   exactly when someone must decide what the seed does about a genuine refusal.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class SessionUserRowRefusalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string FixtureDir(string name) =>
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", name);

    /// <summary>
    /// Runs the runner over one fixture with AL_RUNNER_PERF=1, which is what puts the seed's own
    /// PerfTrace line on stderr. The line is the only place the three outcomes are distinguishable
    /// from outside the process — the AL-visible table state is identical for "inserted" and
    /// "already present", which is why the pre-fix code could get this wrong unnoticed.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) Run(string fixtureName, string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir(fixtureName)}\"");
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
        psi.Environment["AL_RUNNER_PERF"] = "1";

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within 180s for '{fixtureName}'.");
        }
        // WaitForExit(int) returns on process exit WITHOUT draining the async read callbacks;
        // only the parameterless overload waits for those. Skipping it makes assertions on the
        // last lines of output flaky under load (#2496).
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    // TestScratch.Dir, not a hand-rolled Path.GetTempPath() combine: a --cache root is the
    // expensive kind (the runner builds a ~273 MB BC cache into it), and an OWNED directory is
    // reclaimable by a later runner start if this host is killed before its finally block runs
    // (#2706/#2743, enforced by ScratchDirOwnershipGuardTests).
    private static string TempCache(string tag) => TestScratch.Dir($"al-runner-sur-{tag}");

    [Fact]
    public void SeedOnAnAlreadyPresentRow_SaysAlreadyPresent_NotSeeded()
    {
        var cacheDir = TempCache("already");
        try
        {
            var (exit, stdout, stderr) = Run("SessionUserRowAlreadyPresent", cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // THE DISCRIMINATOR. The pre-fix runner printed the "seeded" line here, because it
            // discarded ALInsert's false and ran its success trace regardless. Asserting the
            // exact text both ways is what makes this a RED→GREEN rather than a smoke test.
            Assert.Contains("UserSystemTable: User row 'TESTUSER' was already present", stderr);
            Assert.DoesNotContain("UserSystemTable: seeded User row", stderr);

            // The benign refusal must not be reported as a failure — this is the negative
            // control on the loud path. An implementation that shouted on every non-insert
            // would satisfy the refusal requirement and be wrong.
            Assert.DoesNotContain("REFUSED", stderr);

            // And the AL-visible state: the row the Install trigger wrote is untouched, there is
            // exactly one of it, and Get still consults the key.
            Assert.Contains("PASS  Codeunit70501.SuraSeedLeftTheAlreadyPresentRowExactlyAsItWas", stdout);
            Assert.Contains("PASS  Codeunit70501.SuraSeedAddedNoSecondRowForTheSessionUser", stdout);
            Assert.Contains("PASS  Codeunit70501.SuraAUserSecurityIdBelongingToNobodyIsStillNotFound", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SeedWithASameNamedForeignUserPresent_StillWritesTheSessionUsersOwnRow()
    {
        var cacheDir = TempCache("collision");
        try
        {
            var (exit, stdout, stderr) = Run("SessionUserRowNameCollision", cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Measured: the insert genuinely happens, so the seed reports the outcome it
            // actually reached. This is the assertion that would flip if unique secondary keys
            // became enforceable — at which point the line becomes the REFUSED one and the
            // fixture's AL test fails too, which is the intended alarm.
            Assert.Contains("UserSystemTable: seeded User row 'TESTUSER'", stderr);
            Assert.DoesNotContain("was already present", stderr);
            Assert.DoesNotContain("REFUSED", stderr);

            Assert.Contains("PASS  Codeunit70521.SurcTheSameNamedForeignUserIsInTheTable", stdout);
            Assert.Contains("PASS  Codeunit70521.SurcTheSessionUserStillGetsItsOwnRow", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
