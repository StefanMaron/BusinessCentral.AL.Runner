// TestPageSubscriberRefusalTests — issue #3105.
//
// This is a RUNNER-MECHANISM test. The BC claim it rests on — that `asserterror` around a
// TestPage control write traps the refusal and leaves ValidationErrorCount() = 1 — is settled
// upstream by corpus codeunits 60808, 60820 and 60836, all green on a real service tier
// (.claude/rules/bc-behavior-tests-go-upstream.md). What is pinned HERE is the one cell of that
// shape none of them reaches, and it is the cell issue #3105 reported broken:
//
//     the refusal is raised in a TABLE EVENT SUBSCRIBER that the platform's own event dispatch
//     invokes underneath Delete(true), several frames BELOW the page-global control's
//     OnValidate — not in the trigger body the way every existing test raises it.
//
// That is how Microsoft's Codeunit134614.TestRemoveSUPERPermissionsByUserAll refuses: page 9816
// "Permission Set by User"'s AllUsersHavePermission control (a page GLOBAL, not a Rec-bound
// field) deletes an "Access Control" row, and the System Application's
// "User Permissions Impl."(153).CheckSuperPermissionsBeforeDeleteAccessControl subscriber raises
// four frames down. A regression in the runner's dispatch-under-a-control-write path would leave
// 60808/60820/60836 green and break that shape silently, which is exactly the failure mode #3105
// describes.
//
// STATE OF #3105 WHEN THIS WAS WRITTEN, MEASURED — not a fix, a pin.
//
// The defect does not reproduce on main. Rebuilding the bundle #3105 names (TestAppPermissions +
// LibrarySingleServer + the two permission sets from Tests-SINGLESERVER, BC 28.1.49838.53910,
// both package caches), Codeunit134614.TestRemoveSUPERPermissionsByUserAll PASSES, and both
// assertions #3105 says are never reached run and pass. In a whole-codeunit run it still fails,
// but as the "already bound" cascade behind TestAddPermissionSet — whose root is the
// NavUserAccountHelper.IsPermissionSetAssigned NRE tracked in #3039, and whose cascade mechanism
// was settled as not-a-runner-defect in #2393. Disabling that ONE root test takes codeunit
// 134614 from 4P/11F to 13P/1F, this test among the 13.
//
// PROVING PROPERTY, demonstrated rather than asserted. Making the fixture's subscriber return
// instead of raising — the swallow #3105 hypothesised — turns the two refusal arms red with
// "NavNCLAssertErrorException: An error was expected inside an ASSERTERROR statement.", the exact
// message #3105 reports, while the accepting arm stays green. So the arms discriminate in both
// directions: an implementation that swallowed the subscriber's error fails the first two, and
// one that refused every write fails the third.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageSubscriberRefusalTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPageSubscriberRefusal");

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
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; the parameterless
        // overload does. See #2496.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    [Fact]
    public void ARefusalRaisedInATableSubscriber_ReachesTheAssertErrorAndTheControlsLedger()
    {
        var cacheDir = TestScratch.Dir("al-runner-tsr-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"every fixture test must pass. exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The claim: the subscriber's Error travels out of SetValue, carrying its own
            // message, and the delete it refused did not happen.
            Assert.Contains("PASS  Codeunit70604.SubscriberRefusal_ReachesTheAssertErrorAroundSetValue", stdout);

            // The ledger, read after asserterror swallowed the exception — the half Microsoft's
            // Codeunit134614 asserts and #3105 reported as never reached.
            Assert.Contains("PASS  Codeunit70604.SubscriberRefusal_RecordsExactlyOneValidationError", stdout);

            // The mirror, without which "throw on every SetValue" would satisfy the two above.
            Assert.Contains("PASS  Codeunit70604.UnguardedRow_IsDeletedAndRecordsNoValidationError", stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
