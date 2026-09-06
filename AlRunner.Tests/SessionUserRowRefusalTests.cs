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
// WHAT THE REVIEW PREDICTED, AND WHERE IT LANDED (#2983)
//   The review named a concrete bite: a unique key on User."User Name" would make a --test-data
//   backup containing a TESTUSER refuse the seed and silently defeat #2296. When it was first
//   measured on BC 28.1.49838.53910 the seed landed anyway, because the mechanism is an index
//   on neither side: the runner's store for this table is BC's own CreateTempDataAccess, which
//   enforces the PRIMARY key on "User Security ID" and nothing else, and real BC refuses a
//   duplicate user name from a TRIGGER —
//   SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:` arm calls
//   IsUserFieldUniqueAsync(recordBuffer, 2, insert: true) and throws
//   NavNCLUserTableUserNameMustBeUniqueException.Create() before writing. The runner did not
//   reproduce that arm, so it held two rows sharing a user name where BC would hold one.
//
//   AlRunner/Patches/UserTableTriggerPatches.cs reproduces it now, and the review's prediction
//   is therefore the LIVE behaviour rather than a hypothetical: the seed IS refused over a
//   same-named foreign user, and the Refused branch #2941 built — until now reachable only from
//   the exception path — is reached from AL for the first time.
//   SeedWithASameNamedForeignUserPresent_IsRefusedAndSaysSo below asserts exactly that, naming
//   BC's own exception text, and it is the RED→GREEN for #2983 on the runner side: against the
//   pre-fix runner it fails, because the seed reported "seeded User row 'TESTUSER'".
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
    public void SeedWithASameNamedForeignUserPresent_IsRefusedAndSaysSo()
    {
        var cacheDir = TempCache("collision");
        try
        {
            var (exit, stdout, stderr) = Run("SessionUserRowNameCollision", cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // THE #2983 DISCRIMINATOR. Against the pre-fix runner this line reads
            // "UserSystemTable: seeded User row 'TESTUSER'", because nothing refused the
            // duplicate name. It now reads the Refused one, and it names BC's OWN exception —
            // the runner raises NavNCLUserTableUserNameMustBeUniqueException through BC's own
            // static factory, so the text below is BC's, not a runner paraphrase.
            Assert.Contains("was REFUSED and is NOT present", stderr);
            Assert.Contains("NavNCLUserTableUserNameMustBeUniqueException", stderr);
            Assert.Contains("The user name must be unique.", stderr);
            Assert.DoesNotContain("UserSystemTable: seeded User row", stderr);
            Assert.DoesNotContain("was already present", stderr);

            // The refusal is REPORTED, not merely acted on: #2941's whole finding was that the
            // seed could reach an outcome and say nothing about it.
            Assert.Contains("See AlRunner#2296", stderr);

            Assert.Contains("PASS  Codeunit70521.SurcTheSameNamedForeignUserIsInTheTable", stdout);
            Assert.Contains(
                "PASS  Codeunit70521.SurcTheSessionUserIsRefusedItsOwnRowOverTheDuplicateName",
                stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
