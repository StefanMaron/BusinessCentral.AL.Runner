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
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ProvisionExplicitModesTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 180_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // A real, already-published BC version — this test needs the CDN to actually have it
    // (unlike AutoProvisionDefaultTests's deliberately-nonexistent 1.2.3.4, which exists
    // purely to prove "an attempt was made" via a fast 404). Also the version this repo's
    // own AlRunner.csproj/AlRunner.Tests.csproj build against by DEFAULT (a bare `dotnet
    // build`/`dotnet test`, no `-p:_BCVersion`) — see Directory.Build.props — so it is
    // already in wide use and unlikely to be withdrawn from the CDN out from under this
    // test. Only usable where the version is passed EXPLICITLY (`--bc-version RealVersion`)
    // — a matrix CI leg builds against a DIFFERENT `-p:_BCVersion`, so any test that
    // resolves the version IMPLICITLY (no `--bc-version` on the command line) must use
    // <see cref="ThisBuildsEngineVersion"/> instead, which reads the actual value baked
    // into THIS test binary rather than assuming the dev-machine default (#2208: a matrix
    // leg built for 28.1.49838.54044 failed here when this constant was used for that).
    private const string RealVersion = "28.1.49838.53910";

    // The exact BC version THIS test assembly (and the AlRunner binary it spawns, built in
    // the same `dotnet build AlRunner.slnx -p:_BCVersion=...` invocation) was built
    // against — see the comment on RealVersion above for why this must NOT be a constant.
    private static readonly string ThisBuildsEngineVersion =
        AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()?.ToString() ?? RealVersion;

    /// <summary>Cap for a spawn that must NOT reach the network (<see cref="OfflineCdnEnvironment"/>):
    /// it answers in about a second, so a spawn running past this has gone online.</summary>
    private const int OfflineSpawnTimeoutMs = 60_000;

    private static (int ExitCode, string StdErr) Run(string isolatedHome, params string[] args)
        => Run(isolatedHome, SpawnTimeoutMs, null, args);

    private static (int ExitCode, string StdErr) Run(
        string isolatedHome, int timeoutMs, IReadOnlyDictionary<string, string>? environment, params string[] args)
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
        if (environment != null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"al-runner did not exit within {timeoutMs / 1000}s for: {argLine}. If the test machine " +
                "has no network reachability to the BC artifact CDN this will hang instead " +
                "of failing fast.");
        }
        proc.WaitForExit();
        lock (errSb) return (proc.ExitCode, errSb.ToString());
    }

    /// <summary>
    /// Environment that makes the spawned runner's every CDN request fail at once: a proxy on a
    /// loopback port nothing listens on. SocketsHttpHandler reads the proxy variables on Linux and
    /// connects there instead of the CDN, so the answer is a refused connection in well under a
    /// second whatever state the real CDN is in. #5420: a SLOW CDN made the live version of these
    /// resolution facts exceed <see cref="SpawnTimeoutMs"/>. Measured with a proxy that accepts and
    /// never answers: the probe for the exact build waits out HttpClient's 100 s default twice
    /// (it retries an undetermined answer once), so 200 s before any download starts.
    ///
    /// Windows resolves its proxy from system settings and ignores these variables, so the facts
    /// that use this skip there (<see cref="SkipUnlessEnvironmentProxyIsHonoured"/>).
    /// </summary>
    private static IReadOnlyDictionary<string, string> OfflineCdnEnvironment()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // bound-then-closed: connecting is refused

        var proxy = $"http://127.0.0.1:{port}";
        return new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = proxy, ["https_proxy"] = proxy,
            ["HTTP_PROXY"] = proxy, ["http_proxy"] = proxy,
            ["ALL_PROXY"] = proxy, ["all_proxy"] = proxy,
            ["NO_PROXY"] = "", ["no_proxy"] = "",
        };
    }

    private static void SkipUnlessEnvironmentProxyIsHonoured()
        => TestArtifacts.SkipIf(OperatingSystem.IsWindows(),
            "the offline CDN needs the proxy environment variables, which .NET ignores on Windows.");

    private static string NewIsolatedHome()
    {
        var dir = TestScratch.Dir("al-runner-provision-explicit");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Composed from the shared TestArtifacts.StandardCacheDir(home) root (the layout
    // bc-tests.yml actually provisions), not spelled out here — TestArtifactsGateTests'
    // OnlyTheSharedHelperNamesTheArtifactCachePathsInCode enforces that only TestArtifacts
    // itself may name the raw ".local/share/al-runner/artifacts" path segments in code.
    private static string TestAppsDirFor(string home, string version = RealVersion) =>
        Path.Combine(TestArtifacts.StandardCacheDir(home), version, "test-apps");

    /// <summary>
    /// Positive direction: `provision --test-apps --bc-version &lt;ver&gt;` downloads the
    /// real Microsoft test-toolkit set straight into the canonical
    /// &lt;artifacts&gt;/&lt;ver&gt;/test-apps directory and exits 0 — no --package-cache,
    /// no checkout, nothing but the installed binary and a version number. Asserts real
    /// content landed (specific, well-known app names), not merely that the directory
    /// exists — a no-op that created an empty directory would also pass a bare
    /// Directory.Exists check.
    /// </summary>
    [SkippableFact]
    public void TestApps_FreshCache_DownloadsRealSetIntoCanonicalDir()
    {
        LiveCdnGate.Require();
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
    [SkippableFact]
    public void TestApps_SecondInvocationWithoutForce_DoesNotRedownload()
    {
        LiveCdnGate.Require();
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
    /// Issue #2558's exact reported command: `provision --test-apps` used to accept ANY
    /// single .app file as proof of a complete toolkit — a directory left holding one
    /// unrelated .app by, e.g., an earlier interrupted extraction read as "already present"
    /// forever, and a re-run never attempted the real download. The entry guard now uses
    /// ProvisioningCheck.TestToolkitPresent (a real manifest parse for the specific,
    /// well-known sentinel app "Business Foundation Test Libraries"), not "does any .app
    /// file exist". Proven end-to-end against the real CDN: seed the canonical directory
    /// with one unrelated .app, run without --force, and the run must NOT say "already
    /// present" — it must actually fetch the real set, landing the sentinel.
    /// </summary>
    [SkippableFact]
    public void TestApps_OnlyUnrelatedAppPresent_DoesNotShortCircuit_DownloadsTheRealSet()
    {
        LiveCdnGate.Require();
        var home = NewIsolatedHome();
        try
        {
            var testAppsDir = TestAppsDirFor(home);
            Directory.CreateDirectory(testAppsDir);
            // A single, deliberately UNRELATED .app — not the real sentinel — mimicking an
            // interrupted extraction that landed one country test app and nothing else.
            File.WriteAllText(Path.Combine(testAppsDir, "leftover.app"), "not a real NAVX package");

            var (exit, stderr) = Run(home, "provision", "--test-apps", "--bc-version", RealVersion);

            Assert.True(exit == 0, $"provision --test-apps must exit 0 once the real download completes. stderr:\n{stderr}");
            Assert.DoesNotContain("already present", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fetching", stderr, StringComparison.OrdinalIgnoreCase);
            var apps = Directory.GetFiles(testAppsDir, "*.app");
            Assert.Contains(apps, a => Path.GetFileName(a).Contains("Library Assert", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(apps, a => Path.GetFileName(a).Contains("Business Foundation Test Libraries", StringComparison.OrdinalIgnoreCase));
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
    [SkippableFact]
    public void TestApps_Force_RedownloadsEvenWhenAlreadyPresent()
    {
        LiveCdnGate.Require();
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
    [SkippableFact]
    public void ResolveVersion_PrintsFullVersionToStdout()
    {
        LiveCdnGate.Require();
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
    /// Issue #2208 secondary defect: a provisioning failure exited 1, the code the
    /// documented exit ladder (`docs/server-mode.md`'s "Exit codes" section) reserves for
    /// "at least one test failed" — but `provision` never runs a test, so that code lies
    /// about what happened. A resolution failure here is an execution error (exit 2), the
    /// same code every other "provisioning went wrong before a run could start" path uses
    /// (see `BC version selection failed: ...` returning 2 elsewhere in Program.cs).
    /// Exercises a real failure (a version prefix the public BC CDN index does not carry),
    /// not a mocked one.
    /// </summary>
    [SkippableFact]
    public void ResolveVersion_NonexistentPrefix_ExitsExecutionErrorNotTestFailureCode()
    {
        LiveCdnGate.Require();
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner"))
            + " provision --resolve-version 999.9";
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
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000), "a nonexistent-prefix lookup must fail fast (one 404 index miss).");
        Assert.Equal(2, proc.ExitCode);
        Assert.Contains("could not resolve a full BC version for prefix '999.9'", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #2208 defect 1: with no `--bc-version`, no engine DLL shipped in `bin/` (the
    /// ordinary shadow-copy/re-exec layout — `EngineMajor(AppContext.BaseDirectory)` returns
    /// null there), and no bundle argument, the resolver used to give up and print "cannot
    /// determine which BC version to provision" even though the binary's own build version
    /// (`BcArtifacts.EngineBuiltVersion()`, baked in at compile time — no file needed) answers
    /// the question on every other code path ("selecting BC 28.1.49838.53910, the exact build
    /// this binary was compiled against").
    ///
    /// #5420: this is a RESOLUTION claim, and resolution finishes before any download starts, so
    /// the fact runs against a CDN that refuses every connection (<see cref="OfflineCdnEnvironment"/>)
    /// and asserts on what the runner says it will fetch: the engine's exact build, and the
    /// canonical directory for it. It used to download ~20 MB from the live CDN, and a slow CDN
    /// turned the required unit step red. That a download lands is proven by the explicit
    /// `--bc-version` facts above, which share the same download path.
    /// </summary>
    [SkippableFact]
    public void TestApps_NoBcVersionNoBundle_ResolvesEngineBuiltVersion()
    {
        SkipUnlessEnvironmentProxyIsHonoured();
        var home = NewIsolatedHome();
        try
        {
            var testAppsDir = TestAppsDirFor(home, ThisBuildsEngineVersion);
            Assert.False(Directory.Exists(testAppsDir), "precondition: fresh cache must not already have this dir");

            var (exit, stderr) = Run(home, OfflineSpawnTimeoutMs, OfflineCdnEnvironment(), "provision", "--test-apps");

            Assert.DoesNotContain("cannot determine which BC version to provision", stderr, StringComparison.Ordinal);
            // The CDN was unreachable by construction, so the exact build could not be confirmed and
            // the resolver held its tier rather than demoting (#2981): proof the offline condition
            // held, and that the answer is the engine's build, not something the CDN supplied.
            Assert.Contains($"targeting BC {ThisBuildsEngineVersion} (this binary's own engine build is " +
                $"{ThisBuildsEngineVersion}; tier 'cdn-exact-undetermined')", stderr, StringComparison.Ordinal);
            Assert.Contains($"fetching Microsoft test-toolkit apps for BC {ThisBuildsEngineVersion} → {testAppsDir}",
                stderr, StringComparison.Ordinal);
            // Nothing could be downloaded, so the run is the execution error the exit ladder names
            // for a provisioning failure (#2208), and no toolkit landed.
            Assert.Equal(2, exit);
            Assert.False(Directory.Exists(testAppsDir) && Directory.GetFiles(testAppsDir, "*.app").Length > 0,
                $"no .app can have been downloaded from a refusing CDN. stderr:\n{stderr}");
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Issue #2208 defect 2: given a bundle whose app.json declares a lower `application`
    /// major than this binary's engine (a project's manifest floor, not the version to
    /// provision against), the resolver used to derive the provisioning target FROM that
    /// manifest field and silently fetch the wrong major's platform-apps set into a
    /// directory the actual engine never scans. This proves the fix ignores the manifest
    /// major for version SELECTION (a mismatch is a warning only) and still targets this
    /// binary's own engine build — asserting on the concrete target directory the runner
    /// names, not just "some directory got created".
    /// </summary>
    [SkippableFact]
    public void TestApps_BundleDeclaresOlderMajor_StillTargetsEngineMajor()
    {
        SkipUnlessEnvironmentProxyIsHonoured();
        // The bundle's declared major must genuinely differ from THIS build's engine
        // major — a CI matrix leg builds against 27.x as often as 28.x (bc-tests.yml), so
        // a hardcoded "27" collided with the engine's own major on a 27.x leg and made
        // the negative assertion below fail on real, CORRECT output (#2208 follow-up).
        var engineMajor = Version.Parse(ThisBuildsEngineVersion).Major;
        var bundleMajor = engineMajor - 1;
        var home = NewIsolatedHome();
        var bundleDir = TestScratch.Dir("al-runner-provision-explicit-bundle");
        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"),
            "{ \"id\": \"11111111-1111-1111-1111-111111111111\", \"name\": \"Fixture\", " +
            "\"publisher\": \"Fixture\", \"version\": \"1.0.0.0\", \"application\": \"" + bundleMajor + ".0.0.0\" }");
        try
        {
            var testAppsDir = TestAppsDirFor(home, ThisBuildsEngineVersion); // the ENGINE's own build, not the bundle's major
            Assert.False(Directory.Exists(testAppsDir), "precondition: fresh cache must not already have this dir");

            // #5420: version SELECTION is the claim, so the CDN refuses every connection and the
            // assertion is on the target the runner announces, not on a ~20 MB download.
            var (exit, stderr) = Run(home, OfflineSpawnTimeoutMs, OfflineCdnEnvironment(),
                "provision", "--test-apps", "--force", bundleDir);

            Assert.Equal(2, exit);
            Assert.Contains($"fetching Microsoft test-toolkit apps for BC {ThisBuildsEngineVersion} → {testAppsDir}",
                stderr, StringComparison.Ordinal);
            Assert.DoesNotContain($"for BC {bundleMajor}.", stderr, StringComparison.Ordinal);
            var cacheRoot = TestArtifacts.StandardCacheDir(home);
            var versionDirs = Directory.Exists(cacheRoot)
                ? Directory.GetDirectories(cacheRoot).Select(Path.GetFileName).ToArray()
                : Array.Empty<string?>();
            Assert.DoesNotContain(versionDirs, d => d != null && d.StartsWith(bundleMajor + ".", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
            try { Directory.Delete(bundleDir, recursive: true); } catch { }
        }
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

    /// <summary>
    /// Issue #2560, defect 1: `provision --force` alone (the `provision` subcommand IS
    /// present, so the sibling "--force without `provision`" guard above does not fire, but
    /// none of --platform-apps/--test-apps/--service-tier is given either) used to match
    /// neither rejection guard and fall through into the ordinary auto-detect run path with
    /// --force silently discarded — no message, no error, nothing forced. No network/BC
    /// artifacts needed: this must be rejected before either is ever touched.
    /// </summary>
    /// <summary>
    /// #4923: the explicit modes return before the backup step, so `provision --test-apps
    /// --test-data` used to drop --test-data and exit 0. Rejected before any network access.
    /// </summary>
    [Theory]
    [InlineData("--platform-apps")]
    [InlineData("--test-apps")]
    [InlineData("--service-tier")]
    [InlineData("--resolve-version 28.4")]
    public void TestData_WithAnExplicitMode_IsRejected(string mode)
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner"))
            + $" provision {mode} --test-data --bc-version 0.0.0.1";
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
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(60_000));
        _ = stdoutTask.Result;
        Assert.Equal(2, proc.ExitCode);
        Assert.Contains("--test-data is not supported with", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_AloneWithNoModeFlag_IsRejected()
    {
        var argLine = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + " provision --force";
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
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000));
        Assert.Equal(2, proc.ExitCode);
        Assert.Contains("--force is only valid with", stderr, StringComparison.Ordinal);
        Assert.Contains("--platform-apps", stderr, StringComparison.Ordinal);
        Assert.Contains("--test-apps", stderr, StringComparison.Ordinal);
        Assert.Contains("--service-tier", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #2560, defect 2: `provision --resolve-version PREFIX --test-apps` used to print
    /// the resolved version and return 0 UNCONDITIONALLY, before ever reaching the named
    /// sub-step — silently discarding the --test-apps half of the command with no
    /// indication anything was skipped ("either honor the combination or reject it with
    /// exit 2... silently dropping it is the one option that should be off the table" per
    /// the issue). This proves the combination is now HONORED: the resolved version is
    /// printed to stdout (unchanged, still needed for script/agent consumption) AND the
    /// named sub-step actually runs, landing real content — not merely "exit 0".
    /// --test-apps chosen over --platform-apps/--service-tier for the same ~20MB-vs-100+MB
    /// reason the rest of this file already prefers it (see file header).
    /// </summary>
    [SkippableFact]
    public void ResolveVersion_CombinedWithTestApps_PrintsVersionAndDownloads()
    {
        LiveCdnGate.Require();
        var home = NewIsolatedHome();
        try
        {
            var (exit, stderr) = Run(home, "provision", "--resolve-version", "28.1", "--test-apps");

            Assert.True(exit == 0, $"must exit 0 once both the resolve and the download succeed. stderr:\n{stderr}");
            // The resolved version is whatever's currently latest under the 28.1 prefix —
            // find the actual directory the download landed in rather than hardcoding one.
            var artifactsRoot = TestArtifacts.StandardCacheDir(home);
            Assert.True(Directory.Exists(artifactsRoot), $"expected {artifactsRoot} to exist. stderr:\n{stderr}");
            var versionDirs = Directory.GetDirectories(artifactsRoot);
            Assert.Single(versionDirs);
            var landedTestAppsDir = Path.Combine(versionDirs[0], "test-apps");
            Assert.True(Directory.Exists(landedTestAppsDir),
                $"expected a test-apps dir under the resolved version {versionDirs[0]}. stderr:\n{stderr}");
            var apps = Directory.GetFiles(landedTestAppsDir, "*.app");
            Assert.True(apps.Length > 10,
                $"expected more than 10 .app files (a real download, not an empty dir), got {apps.Length}. stderr:\n{stderr}");
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
