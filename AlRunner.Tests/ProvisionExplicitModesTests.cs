// Issue #2085: every remediation route the runner prints must be executable using only
// the installed tool. `dotnet run --project tools/DownloadArtifacts -- <mode> <ver> <dir>`
// requires a source checkout of this repository that a `dotnet tool install -g
// msdyn365bc.al.runner` user never has — measured on the published 2.7.0, two of the three
// printed "Resolve it" routes were dead ends for exactly that audience. `al-runner provision
// --platform-apps/--test-apps/--service-tier [--force]` exposes the SAME
// AlRunner.Provisioning.ArtifactDownloader methods tools/DownloadArtifacts already wraps,
// straight from the shipped binary.
//
// These tests spawn the REAL runner binary against a REAL, hermetically empty artifact
// cache (an isolated $HOME, never the machine's actual
// ~/.local/share/al-runner/artifacts) and make a genuine download against the public BC
// artifact CDN — deliberately `--test-apps` (~20MB), the smallest of the three real sets,
// rather than the ~118MB platform-apps set or the multi-GB service-tier closure, to prove a
// REAL end-to-end download without paying for the largest one. Confirmed RED on unfixed
// `main` (pre-#2085, i.e. right after #2086): `--test-apps` is not a recognized flag at
// all, so the arg parser's fallback rejects it with "Unknown option '--test-apps'. Run with
// --help for the supported flags." and exits 2 — nothing is downloaded, because the only
// implemented route (tools/DownloadArtifacts) requires the checkout this test's isolated
// $HOME/binary-only setup deliberately does not have.
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ProvisionExplicitModesTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A real, already-published BC version — this test needs the CDN to actually have it
    // (unlike AutoProvisionDefaultTests's deliberately-nonexistent 1.2.3.4, which exists
    // purely to prove "an attempt was made" via a fast 404). Also the version this repo's
    // own AlRunner.csproj/AlRunner.Tests.csproj build against by default, so it is already
    // in wide use and unlikely to be withdrawn from the CDN out from under this test.
    private const string RealVersion = "28.1.49838.53910";

    private static (int ExitCode, string StdErr) Run(string isolatedHome, params string[] args)
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + " " + string.Join(' ', args);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Redirect $HOME so BcArtifacts.ArtifactsRoot resolves under a directory that has
        // NEVER existed — the exact "clean tool install, no artifact cache" scenario,
        // independent of whatever the machine actually running this test has cached.
        psi.Environment["HOME"] = isolatedHome;

        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"al-runner did not exit within 180s for: {argLine}. If the test machine " +
                "has no network reachability to the BC artifact CDN this will hang instead " +
                "of failing fast.");
        }
        proc.WaitForExit();
        lock (errSb) return (proc.ExitCode, errSb.ToString());
    }

    private static string NewIsolatedHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-provision-explicit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Composed from the shared TestArtifacts.StandardCacheDir(home) root (the layout
    // bc-tests.yml actually provisions), not spelled out here — TestArtifactsGateTests'
    // OnlyTheSharedHelperNamesTheArtifactCachePathsInCode enforces that only TestArtifacts
    // itself may name the raw ".local/share/al-runner/artifacts" path segments in code.
    private static string TestAppsDirFor(string home) =>
        Path.Combine(TestArtifacts.StandardCacheDir(home), RealVersion, "test-apps");

    /// <summary>
    /// Positive direction: `provision --test-apps --bc-version &lt;ver&gt;` downloads the
    /// real Microsoft test-toolkit set straight into the canonical
    /// &lt;artifacts&gt;/&lt;ver&gt;/test-apps directory and exits 0 — no --package-cache,
    /// no checkout, nothing but the installed binary and a version number. Asserts real
    /// content landed (specific, well-known app names), not merely that the directory
    /// exists — a no-op that created an empty directory would also pass a bare
    /// Directory.Exists check.
    /// </summary>
    [Fact]
    public void TestApps_FreshCache_DownloadsRealSetIntoCanonicalDir()
    {
        var home = NewIsolatedHome();
        try
        {
            var testAppsDir = TestAppsDirFor(home);
            Assert.False(Directory.Exists(testAppsDir), "precondition: fresh cache must not already have this dir");

            var (exit, stderr) = Run(home, "provision", "--test-apps", "--bc-version", RealVersion);

            Assert.True(exit == 0, $"provision --test-apps must exit 0. stderr:\n{stderr}");
            Assert.True(Directory.Exists(testAppsDir), $"expected {testAppsDir} to exist after provisioning. stderr:\n{stderr}");
            var apps = Directory.GetFiles(testAppsDir, "*.app");
            // Real content, not an empty/stub directory: Library Assert and Test Runner are
            // foundational apps every AL test bundle transitively depends on.
            Assert.True(apps.Length > 10,
                $"expected more than 10 .app files, got {apps.Length}: {string.Join(", ", apps.Select(Path.GetFileName))}");
            Assert.Contains(apps, a => Path.GetFileName(a).Contains("Library Assert", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(apps, a => Path.GetFileName(a).Contains("Test Runner", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Negative direction of the same feature: a SECOND invocation without --force must
    /// leave the already-downloaded set alone rather than re-fetching ~20MB on every
    /// re-run — the whole point of checking the canonical directory before downloading.
    /// Proven by the marker file's write time: if a re-download happened it would move;
    /// the "already present — skipping" short-circuit must leave it exactly where the
    /// FIRST invocation wrote it. A test that only checked "exit 0" would pass even if the
    /// runner silently re-downloaded every time, which is the failure this guards against.
    /// </summary>
    [Fact]
    public void TestApps_SecondInvocationWithoutForce_DoesNotRedownload()
    {
        var home = NewIsolatedHome();
        try
        {
            var testAppsDir = TestAppsDirFor(home);
            var (exit1, stderr1) = Run(home, "provision", "--test-apps", "--bc-version", RealVersion);
            Assert.True(exit1 == 0, $"first provision must exit 0. stderr:\n{stderr1}");
            var marker = Directory.GetFiles(testAppsDir, "*.app").First();
            var writeTimeBefore = File.GetLastWriteTimeUtc(marker);

            var (exit2, stderr2) = Run(home, "provision", "--test-apps", "--bc-version", RealVersion);

            Assert.True(exit2 == 0, $"second provision (no --force) must still exit 0. stderr:\n{stderr2}");
            Assert.Contains("already present", stderr2, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skipping", stderr2, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(marker));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// `--force` is the escape hatch from the skip above: it must re-run the download even
    /// though the directory already looks populated. Proven the same way as the negative
    /// test above, inverted — the marker's write time MUST move.
    /// </summary>
    [Fact]
    public void TestApps_Force_RedownloadsEvenWhenAlreadyPresent()
    {
        var home = NewIsolatedHome();
        try
        {
            var testAppsDir = TestAppsDirFor(home);
            var (exit1, stderr1) = Run(home, "provision", "--test-apps", "--bc-version", RealVersion);
            Assert.True(exit1 == 0, $"first provision must exit 0. stderr:\n{stderr1}");
            var marker = Directory.GetFiles(testAppsDir, "*.app").First();
            var writeTimeBefore = File.GetLastWriteTimeUtc(marker);
            System.Threading.Thread.Sleep(1100); // filesystem mtime resolution margin

            var (exit2, stderr2) = Run(home, "provision", "--test-apps", "--bc-version", RealVersion, "--force");

            Assert.True(exit2 == 0, $"forced provision must exit 0. stderr:\n{stderr2}");
            Assert.DoesNotContain("skipping", stderr2, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.GetLastWriteTimeUtc(marker) > writeTimeBefore,
                $"expected {marker}'s write time to move after --force. Before: {writeTimeBefore:o}, " +
                $"after: {File.GetLastWriteTimeUtc(marker):o}\nstderr:\n{stderr2}");
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// `provision --resolve-version PREFIX` mirrors tools/DownloadArtifacts's
    /// `resolve-version` mode from the installed binary: prints the latest full version for
    /// a prefix to stdout and exits 0. No artifact cache needed at all.
    /// </summary>
    [Fact]
    public void ResolveVersion_PrintsFullVersionToStdout()
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner"))
            + " provision --resolve-version 28.1";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["HOME"] = NewIsolatedHome();
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000), "provision --resolve-version must resolve quickly (one index fetch).");
        Assert.True(proc.ExitCode == 0, $"exit code: {proc.ExitCode}\nstderr:\n{stderr}");
        var resolved = stdout.Trim();
        Assert.StartsWith("28.1.", resolved);
        Assert.Equal(4, resolved.Split('.').Length);
    }

    /// <summary>
    /// Negative: `--platform-apps` (and its siblings) only make sense under the `provision`
    /// subcommand — a plain test run has no use for them. Rejected up front rather than
    /// silently accepted-and-ignored, which would look like support that isn't there.
    /// </summary>
    [Fact]
    public void PlatformApps_WithoutProvisionSubcommand_IsRejected()
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + " --platform-apps";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000));
        Assert.NotEqual(0, proc.ExitCode);
        Assert.Contains("only valid with the `provision` subcommand", stderr, StringComparison.Ordinal);
    }
}
