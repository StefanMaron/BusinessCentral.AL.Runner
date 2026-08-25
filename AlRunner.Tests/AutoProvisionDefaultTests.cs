// Issue #2028 (item 2 of #2024): a clean `dotnet tool install` on a machine with NO
// artifact cache must work without the user knowing anything about provisioning. Since
// PR #2023/#2026 stripped the BC engine assemblies out of the packed tool nupkg, the
// ONLY place they can come from is ~/.local/share/al-runner/artifacts/<version>/,
// populated exclusively by provisioning — and Program.cs used to default
// `autoProvision = false`, so a first run against an empty cache failed instead of
// self-healing.
//
// These tests spawn the REAL runner binary against a REAL, hermetically empty artifact
// cache (an isolated $HOME, never the machine's actual
// ~/.local/share/al-runner/artifacts) and make a genuine small network call against the
// public BC artifact CDN — deliberately NOT a download of the multi-gigabyte service-tier
// closure. Passing --bc-version 1.2.3.4 (a version that can never exist) means:
//   - the "explicit 4-part version" branch of RunProvisioning skips CDN index resolution
//     entirely and goes straight to a single HEAD request for that (nonexistent) version,
//   - which 404s immediately, so the assertion is "a network attempt was made and named",
//     never "the download succeeded" — no bytes beyond a 404 response are ever fetched.
// This is the cheapest test that can distinguish "the runner tried to provision
// automatically" from "the runner didn't". Per .claude/rules/bc-behavior-tests-go-upstream.md
// this is a runner-mechanism claim (default flag value + the CLI's own provisioning-gap
// message), not a BC-behaviour claim, so it belongs here, not in the al-language corpus.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class AutoProvisionDefaultTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A version that will never exist on the CDN — guarantees a fast 404 (or a fast
    // "no such prefix" from the index) instead of ever touching the multi-GB service-tier
    // ZIP. Four segments so RunProvisioning's "explicit full version" branch is taken,
    // skipping index resolution and going straight to one HEAD request.
    private const string NonexistentVersion = "1.2.3.4";

    private static (int ExitCode, string StdErr) RunIsolated(string isolatedHome, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append($" --bc-version {NonexistentVersion}");
        foreach (var a in extraArgs) args.Append(' ').Append(a);

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
        // Redirect $HOME so BcArtifacts.ArtifactsRoot (~/.local/share/al-runner/artifacts)
        // resolves under a directory that has NEVER existed — the exact "clean tool
        // install, no artifact cache" scenario issue #2028 requires, independent of
        // whatever the machine actually running this test has cached for real.
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
            throw new TimeoutException(
                "al-runner did not exit within 60s against an isolated empty artifact cache. " +
                "If the test machine has no network reachability to the BC artifact CDN, a " +
                "provisioning attempt can block on HttpClient's multi-minute timeout instead " +
                "of failing fast — see ArtifactDownloader.ServiceTier/TryHeadContentLength.");
        }
        proc.WaitForExit();
        lock (errSb) return (proc.ExitCode, errSb.ToString());
    }

    private static string NewIsolatedHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-auto-provision-default", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// RED on unfixed `main`: with autoProvision defaulting to false, a plain invocation
    /// against an empty cache never attempts a download at all — it goes straight to the
    /// "BC artifact root not found" throw with no "[provision]" line anywhere in stderr.
    /// GREEN: auto-provisioning is now on by default (issue #2024/#2028), so the SAME
    /// invocation, with NO --auto-provision flag, must attempt to fetch the requested
    /// version automatically.
    /// </summary>
    [Fact]
    public void NoFlags_EmptyArtifactCache_AttemptsAutomaticProvisioning()
    {
        var home = NewIsolatedHome();
        try
        {
            var (exit, stderr) = RunIsolated(home); // no --auto-provision, no --no-auto-provision

            Assert.Contains(
                $"[provision] downloading BC {NonexistentVersion} engine service-tier closure",
                stderr);
            // The version can never exist, so this must still fail — the claim under test
            // is "provisioning was ATTEMPTED automatically", not "it succeeded".
            Assert.Equal(2, exit);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The opt-out (issue #2028 requirement #2, for offline/air-gapped machines): with
    /// --no-auto-provision, the exact same empty-cache invocation must NOT touch the
    /// network at all, and must fail loud with the tool-install-valid fix command
    /// (`al-runner provision` / `--auto-provision`) named as the PRIMARY recommendation —
    /// not only the repo-checkout-only `dotnet run --project tools/DownloadArtifacts`
    /// command, which is useless to a `dotnet tool install` user (the exact gap impl-15
    /// found testing PR #2026 with an empty cache).
    /// </summary>
    [Fact]
    public void NoAutoProvision_EmptyArtifactCache_FailsLoudWithToolInstallValidCommand_NoNetworkAttempt()
    {
        var home = NewIsolatedHome();
        try
        {
            var (exit, stderr) = RunIsolated(home, "--no-auto-provision");

            Assert.DoesNotContain("[provision] downloading", stderr);
            Assert.Equal(2, exit);
            Assert.Contains("BC artifact root not found", stderr);
            // The universally-valid fix, whether the runner came from a tool install or a
            // checkout — must be present, not just the checkout-only fallback.
            Assert.Contains("al-runner provision", stderr);
            // The checkout-only command may still be offered as a secondary option, but
            // must not be the ONLY thing on offer — assert the primary recommendation
            // appears BEFORE it in the message.
            var toolInstallIdx = stderr.IndexOf("al-runner provision", StringComparison.Ordinal);
            var checkoutOnlyIdx = stderr.IndexOf("dotnet run --project tools/DownloadArtifacts", StringComparison.Ordinal);
            Assert.True(checkoutOnlyIdx < 0 || toolInstallIdx < checkoutOnlyIdx,
                $"The tool-install-valid fix ('al-runner provision') must be named before " +
                $"the repo-checkout-only fallback, not after it:\n{stderr}");
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
