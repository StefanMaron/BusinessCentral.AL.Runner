// SessionUserRowRefusalTests — the #2941 review finding.
//
// RUNNER-MECHANISM tests. That BC's session user is a row in the User table is plain BC
// behaviour and is adjudicated upstream by a real service tier (corpus codeunit 60991). What
// these pin is the runner's own seed: RecordPatches.EnsureUserSystemTableRowSeeded must be able
// to tell its outcomes apart, and must not claim to have written a row it did not write.
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
//   duplicate user name from a TRIGGER --
//   SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:` arm calls
//   IsUserFieldUniqueAsync(recordBuffer, 2, insert: true) and throws
//   NavNCLUserTableUserNameMustBeUniqueException.Create() before writing. The runner did not
//   reproduce that arm, so it held two rows sharing a user name where BC would hold one.
//
//   AlRunner/Patches/UserTableTriggerPatches.cs reproduces it now, so the seed IS refused over a
//   same-named foreign user.
//
// WHAT THE SEED DOES WITH THAT REFUSAL: ADOPT (maintainer decision, 2026-09-06)
//   BC's refusal settles the ROW and not the SESSION. The first implementation of #2983 answered
//   "the session is a user in no row", which is the state #2296 exists to remove; the maintainer
//   chose ADOPTION instead. The session takes the colliding row's security id as its own, so
//   UserSecurityId() returns a value that came out of the data.
//
//   The objection that survived that decision is about SILENCE, not adoption: UserSecurityId()
//   now depends on the contents of a backup file, and AL asserting session identity sees a
//   different value with and without --test-data. So the seed prints a [warn] line naming the
//   user, the adopted id, the generated id it replaced and where it came from -- and
//   SeedWithASameNamedForeignUserPresent_AdoptsThatRowsSecurityIdAndSaysSo asserts every part of
//   it, including the "[warn] " prefix, because a "[Component]"-tagged line is suppressed at
//   default verbosity (#3068) and would make "loud, never silent" untrue.
//
//   THE TWO DIRECTIONS. This fixture asserts the ADOPTED concrete id
//   {A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}; SessionUserRowAlreadyPresent asserts the
//   runner-GENERATED {C0A1BDFA-0000-0000-0000-545553545553} for the case where there is nothing
//   to adopt. One implementation cannot satisfy both by returning a constant.
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
            // NEGATIVE DIRECTION for the adoption in the sibling test below: nothing here is
            // adopted, so no adoption line may appear at all.
            Assert.DoesNotContain("ADOPTED the security id", stderr);

            Assert.Contains(
                "PASS  Codeunit70501.SuraUserSecurityIdIsTheRunnerGeneratedOneWhenNothingIsAdopted",
                stdout);
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
    public void SeedWithASameNamedForeignUserPresent_AdoptsThatRowsSecurityIdAndSaysSo()
    {
        var cacheDir = TempCache("collision");
        try
        {
            var (exit, stdout, stderr) = Run("SessionUserRowNameCollision", cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // THE #2983 DISCRIMINATOR, after the maintainer's decision to ADOPT.
            //
            // Three implementations are distinguishable here and only one passes. The pre-#2983
            // runner printed "UserSystemTable: seeded User row 'TESTUSER'" — it wrote a second row
            // under a name real BC keeps unique. The first #2983 implementation printed
            // "was REFUSED and is NOT present" — correct about the row, and it left the session as
            // a user in no row at all. This one adopts, and the line below is the whole reason
            // adopting is allowed to be silent about nothing.
            Assert.Contains("ADOPTED the security id", stderr);
            Assert.DoesNotContain("was REFUSED and is NOT present", stderr);
            Assert.DoesNotContain("UserSystemTable: seeded User row", stderr);

            // THE ADOPTION IS VISIBLE, in the terms the objection to adopting demanded: the
            // user, the id taken, the id it replaced, and that it came from the data. A reader
            // of the log must never have to wonder why UserSecurityId() answered what it did.
            Assert.Contains("'TESTUSER'", stderr);
            Assert.Contains("A17E9C42-5B08-4D6F-9E31-0C7A2F84B155", stderr);   // adopted
            Assert.Contains("C0A1BDFA-0000-0000-0000-545553545553", stderr);   // replaced
            Assert.Contains("came from your data", stderr);
            Assert.Contains("See AlRunner#2983", stderr);

            // AND IT SURVIVES THE DEFAULT LOG COMPONENT FILTER. #3068 / the 42-test precedent:
            // a "[Component]"-tagged line is suppressed at default verbosity, which would make
            // "loud, never silent" untrue. This run sets no verbosity flag, so seeing the text
            // at all is the assertion — but pin the prefix too, so a later refactor cannot move
            // it behind a tag without failing here.
            Assert.Contains("[warn] UserSystemTable: the session user", stderr);

            Assert.Contains("PASS  Codeunit70521.SurcTheSameNamedForeignUserIsInTheTable", stdout);
            Assert.Contains(
                "PASS  Codeunit70521.SurcTheSessionAdoptedTheExistingRowsSecurityId", stdout);
            Assert.Contains(
                "PASS  Codeunit70521.SurcTheSessionUserResolvesToTheRowTheDataProvided", stdout);
            Assert.Contains(
                "PASS  Codeunit70521.SurcAdoptionAddedNoSecondRowAndLeftUserIdAlone", stdout);
            Assert.Contains(
                "PASS  Codeunit70521.SurcTheAdoptedUserHasItsUserPropertyRow", stdout);
            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
