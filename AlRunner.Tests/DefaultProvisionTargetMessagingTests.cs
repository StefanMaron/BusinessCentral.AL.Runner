// DefaultProvisionTargetMessagingTests — proves Program.cs's version-selection message
// text agrees with what actually happened, for issue #2033.
//
// #2033's core fix is BcArtifacts.ResolveProvisionTargetCore (see
// DefaultProvisionTargetTests.cs) — auto-provisioning targets the engine's own build/minor
// instead of collapsing to "latest in major" on an empty cache. But Program.cs's message
// selection is a SEPARATE piece of logic (it picks which of five hand-written strings to
// print based on which tier won), and it is not exercised by that pure-function test at
// all — it lives inline in Main. The first draft of this fix had a real bug there, caught
// only by spawning the actual binary end-to-end: the "major fallback" case originally
// always said "... is not cached and not available from the CDN", but with
// --no-auto-provision the CDN is never even asked — no network step exists on that path at
// all. The message claimed a check that never happened.
//
// This test pins the two message variants against a real, hermetically empty artifact
// cache: --no-auto-provision (offline; never consults the CDN, so the message must say
// only "no cached", never "CDN") and the auto-provisioning default (does consult the CDN,
// so a genuine double-miss message legitimately can).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefaultProvisionTargetMessagingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static (int ExitCode, string StdErr) RunIsolated(string isolatedHome, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append($" \"{Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp")}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["HOME"] = isolatedHome;

        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 60s against an isolated empty artifact cache.");
        }
        proc.WaitForExit();
        lock (errSb) return (proc.ExitCode, errSb.ToString());
    }

    private static string NewIsolatedHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-provision-target-msg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The bug caught by the manual end-to-end verification: --no-auto-provision never
    /// touches the network, so the major-fallback warning must describe only what's
    /// CACHED, never claim the CDN was consulted (it wasn't).
    /// </summary>
    [SkippableFact]
    public void NoAutoProvision_EmptyCache_MajorFallbackWarning_NeverClaimsCdnWasChecked()
    {
        TestArtifacts.SkipIf(AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion() == null,
            "no baked-in BcEngineVersion on this build — nothing to assert a message about.");

        var home = NewIsolatedHome();
        try
        {
            var (exit, stderr) = RunIsolated(home, "--no-auto-provision");

            Assert.Equal(2, exit);
            // Must describe the LOCAL CACHE state truthfully...
            Assert.Contains("[bc] warning: no cached BC", stderr);
            Assert.Contains("KNOWN-DEGRADED", stderr);
            // ...and must NEVER claim network was consulted when --no-auto-provision
            // guarantees it wasn't.
            Assert.DoesNotContain("CDN", stderr);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// THE #2033 proving case, end-to-end against the real CDN: with auto-provisioning ON
    /// (the default) and a genuinely empty cache, the runner must target the engine's OWN
    /// exact build — the "[bc] ... provisioning BC &lt;exact build&gt;" message, never the
    /// KNOWN-DEGRADED warning that the pre-fix "collapse to major, then let provisioning
    /// fetch latest-in-major" behaviour produced on a first run (issue #2020's exact
    /// symptom). Makes a genuine small network call — a version-index lookup plus a HEAD
    /// probe — but is killed the instant the target message appears in stderr, BEFORE the
    /// multi-hundred-MB service-tier download that follows it gets underway, so this stays
    /// a fast, CI-safe test rather than a real provisioning run (that full run is proven
    /// separately, manually, and recorded in the PR description).
    /// </summary>
    [SkippableFact]
    public void AutoProvisionDefault_EmptyCache_TargetsEngineExactBuild_NeverDegradedWarning()
    {
        var engineVersion = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engineVersion == null,
            "no baked-in BcEngineVersion on this build — nothing to assert a message about.");

        var home = NewIsolatedHome();
        var expected = $"[bc] no --bc-version given — provisioning BC {engineVersion}, the exact build " +
            "this binary was compiled against.";
        try
        {
            var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
            args.Append($" \"{Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp")}\"");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", Arguments = args.ToString(),
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            psi.Environment["HOME"] = home;

            var errSb = new StringBuilder();
            var found = new ManualResetEventSlim(false);
            using var proc = Process.Start(psi)!;
            proc.OutputDataReceived += (_, e) => { };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (errSb) errSb.AppendLine(e.Data);
                if (e.Data.Contains(expected, StringComparison.Ordinal)) found.Set();
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var signalled = found.Wait(30_000);
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(5_000);

            string captured;
            lock (errSb) captured = errSb.ToString();

            Assert.True(signalled,
                $"expected line never appeared within 30s (CDN unreachable from this environment?):\n{captured}");
            Assert.DoesNotContain("KNOWN-DEGRADED", captured);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
