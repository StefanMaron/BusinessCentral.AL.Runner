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
        => RunBundles(cacheDir, fixtureName);

    /// <summary>
    /// The same invocation over SEVERAL bundle directories in ONE process, in the order given.
    /// That is the shape <c>--watch</c>, <c>--server</c> and a plain multi-bundle command line
    /// all reduce to, and it is the only way to observe per-process state leaking across the
    /// per-bundle reset — see
    /// <see cref="AnAdoptionInOneBundleDoesNotLeakIntoTheNextBundlesSessionIdentity"/>.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunBundles(
        string cacheDir, params string[] fixtureNames)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var fixtureName in fixtureNames)
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
            throw new TimeoutException(
                $"al-runner did not exit within 180s for '{string.Join("', '", fixtureNames)}'.");
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

    /// <summary>
    /// THE CROSS-BUNDLE LEAK. Two bundles, one process, in that order: the first adopts a
    /// security id out of its own data, the second must not inherit it.
    ///
    /// <para>WHY A SECOND BUNDLE IS THE ONLY WAY TO SEE THIS. The seed's per-bundle flag is
    /// reset by <c>ResetUserSystemTableForNewBundle()</c>, but the skeleton <c>NavUser</c> that
    /// adoption pokes the security id into is built ONCE PER PROCESS, in
    /// <c>BcRuntime.ApplyAllPatches</c>. So an adoption is a process-wide write behind a
    /// per-bundle decision, and nothing inside a single-bundle run can observe the difference —
    /// delete the four-line restore block from that reset and every other test in this file
    /// still passes.</para>
    ///
    /// <para>THE RED. <c>SuraUserSecurityIdIsTheRunnerGeneratedOneWhenNothingIsAdopted</c>
    /// asserts the concrete generated id <c>{C0A1BDFA-…}</c>. Without the restore, bundle B
    /// starts with bundle A's adopted <c>{A17E9C42-…}</c> still on the session — its own Install
    /// trigger writes a row under that id (it inserts <c>UserSecurityId()</c>), the seed then
    /// takes the benign already-present path, and that test fails naming the leaked value. Run
    /// against the restore removed, it does.</para>
    ///
    /// <para>The two fixtures can share a process at all because their object id ranges do not
    /// overlap — 70520-70539 against 70500-70519 — and neither declares a dependency.</para>
    ///
    /// <para>--watch and --server are the two other multi-bundle modes and reduce to the same
    /// per-bundle reset; this asserts the mechanism through the cheapest of the three. Note that
    /// under <c>--watch</c> the adoption's own [warn] line is NOT visible: Program.cs sets both
    /// Console.Out and Console.Error to TextWriter.Null when the watch UI is on without
    /// --verbose. That is pre-existing and applies equally to every other line the seed writes,
    /// but it means "loud, never silent" is a claim about a normal run, not about the watch UI.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAdoptionInOneBundleDoesNotLeakIntoTheNextBundlesSessionIdentity()
    {
        var cacheDir = TempCache("crossbundle");
        try
        {
            var (exit, stdout, stderr) = RunBundles(
                cacheDir, "SessionUserRowNameCollision", "SessionUserRowAlreadyPresent");

            Assert.True(exit == 0,
                $"expected a clean run over both bundles. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // BUNDLE A DID ADOPT. Without this the test would pass vacuously on a run where the
            // collision never happened, and would then say nothing about leaking.
            Assert.Contains("ADOPTED the security id", stderr);
            Assert.Contains("A17E9C42-5B08-4D6F-9E31-0C7A2F84B155", stderr);
            Assert.Contains(
                "PASS  Codeunit70521.SurcTheSessionAdoptedTheExistingRowsSecurityId", stdout);

            // THE RESTORE RAN, between the two bundles. This is the line the four-line block in
            // ResetUserSystemTableForNewBundle emits, and nothing else in the suite reaches it.
            Assert.Contains(
                "UserSystemTable: restored the generated session security id for the next bundle",
                stderr);

            // THE CONSEQUENCE, asserted as a concrete id by the AL itself: bundle B's session is
            // the runner-generated {C0A1BDFA-…} again, not bundle A's {A17E9C42-…}.
            Assert.Contains(
                "PASS  Codeunit70501.SuraUserSecurityIdIsTheRunnerGeneratedOneWhenNothingIsAdopted",
                stdout);
            Assert.Contains("PASS  Codeunit70501.SuraSeedLeftTheAlreadyPresentRowExactlyAsItWas", stdout);
            Assert.Contains("PASS  Codeunit70501.SuraSeedAddedNoSecondRowForTheSessionUser", stdout);

            // EXACTLY ONE adoption in the process. Bundle B has nothing to adopt — its Install
            // trigger writes the session user's own security id — so a second adoption line here
            // would mean the restore had handed bundle B an identity that then collided again.
            var adoptions = stderr.Split("ADOPTED the security id").Length - 1;
            Assert.True(adoptions == 1,
                $"expected exactly one adoption across both bundles, saw {adoptions}\nstderr:\n{stderr}");

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
