// DeferredAttemptDependencyFloorTests — #5233: the platform-apps deferral (#2232, #2223) runs the
// bundle in a captured child with the Microsoft platform apps withheld, and trusts an exit-0
// child. Microsoft's Test Runner is now loaded by default (#4816) and declares a Platform floor, so
// in that child its source compile has no System symbols and it drops most of its objects —
// 130453/130454, the per-test resets, among them — yet the dependency path reports and continues
// (#2247), so the child exits 0 and its degraded run was replayed as the verdict.
//
// Two halves, each pinned where it lives:
//   * the verdict: a withheld child whose dependency's floor cannot be supplied is not green, so
//     the parent runs with the apps and the user gets the same run `--verbose` gets;
//   * the cache: the dependency's compiled-deps key carries which of its floors were supplied, so
//     the partial DLL such a compile produces cannot be replayed for a run that has the floor.
//
// The run test needs both the runner-owned platform-apps and test-apps on the box (it asserts
// about the user's default configuration, no --package-cache and no pin) and skips on a box that
// has not provisioned them; a CI leg provisions both.
// These fixtures declare `application` because the warm skip fires only on that floor (see
// DeferredPlatformAppsWithholdTests, the same carve-out from no-base-app-in-csharp-tests.md).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class DeferredAttemptDependencyFloorTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // L passes only if the last error B left survives into it. On a service tier the Test Runner
    // clears it before every test (ALTestRunner Reset Environment, 130453), so L FAILS there, and
    // here whenever the complete Test Runner is loaded. It deliberately cannot be a test that
    // fails WITHOUT the resets: the deferral attempt would fail on its own, fall through to the run
    // with the apps, and the defect would stay invisible. The leak is what a degraded Test Runner
    // lets through as a green.
    private const string Probes = """
        codeunit 65231 "Dfl Probe Tests"
        {
            Subtype = Test;

            [Test]
            procedure B_LeavesLastError()
            begin
                asserterror Error('DFL-LEFT-BEHIND');
            end;

            [Test]
            procedure L_ExpectsTheLastErrorToLeak()
            begin
                if GetLastErrorText() <> 'DFL-LEFT-BEHIND' then
                    Error('DFL-CLEARED: the previous test''s last error did not survive into this one, saw [%1]', GetLastErrorText());
            end;
        }
        """;

    private static string WriteFloorOnlyBundle(string name)
    {
        var root = TestScratch.Dir(name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b5233000-0000-4000-8000-000000005233",
          "name": "DeferredAttemptFloor5233",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 65230, "to": 65239 } ],
          "platform": "27.0.0.0",
          "application": "27.0.0.0",
          "runtime": "14.0",
          "target": "Cloud"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Probe.al"), Probes);
        return root;
    }

    // The runner-owned caches a user's run reads with no --package-cache, holding both apps.
    private static void RequireRunnerOwnedTestToolAndPlatformApps()
    {
        var built = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        var dirs = built == null ? new List<string>() : AlRunner.Infrastructure.ProvisioningCheck.CollectRunnerOwnedProvisionDirs(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, $"{built.Major}.{built.Minor}").ToList();
        if (dirs.Any(d => File.Exists(Path.Combine(d, "Microsoft_Test Runner.app")))
            && dirs.Any(d => File.Exists(Path.Combine(d, "System.app")))
            && dirs.Any(d => Directory.Exists(d)
                && Directory.EnumerateFiles(d, "Microsoft_Base Application_*.app").Any()))
            return;
        var reason = $"no runner-owned test-apps + platform-apps holding Test Runner, System and Base Application for BC {built} under "
            + $"'{AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir}' (al-runner provision --test-apps --platform-apps).";
        if (TestArtifacts.RunningOnCi) Assert.Fail(TestArtifacts.CiMissingArtifactsMessage(reason));
        TestArtifacts.SkipIf(true, reason);
    }

    private static (string Output, int Exit) Run(string bundle, string cacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-auto-provision --show-pass --cache \"").Append(cacheDir).Append('"');
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        DefaultTestToolPin.Unpin(psi);
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    // A floor-only bundle on a box holding Test Runner as the default test tool. Before the fix
    // the warm skip's child compiled Test Runner without System, dropped 43 of its 47 objects,
    // 130453 among them, and exited 0; that run was replayed, so L passed on a Test Runner that
    // never cleared the last error, and the report said so only under "Action needed".
    [SkippableFact]
    public void FloorOnlyBundle_WithTheDefaultTestTool_RunsTheCompleteTestRunner()
    {
        TestArtifacts.SkipIfMissing();
        RequireRunnerOwnedTestToolAndPlatformApps();
        var bundle = WriteFloorOnlyBundle("al-runner-deferred-attempt-floor-5233-run");
        var cache = TestScratch.Dir("al-runner-deferred-attempt-floor-5233-run-cache");
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);

        var (output, exit) = Run(bundle, cache);

        Assert.DoesNotContain("EMIT-EXCLUDED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be supplied", output, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?m)^PASS\s+.*\bB_LeavesLastError\b"), output);
        Assert.Matches(new Regex(@"(?m)^FAIL\s+.*\bL_ExpectsTheLastErrorToLeak\b"), output);
        Assert.Contains("DFL-CLEARED", output, StringComparison.Ordinal);
        Assert.Equal(1, exit);
    }

    // ---- the cache half: no process, no artifacts -------------------------------------------

    private static AppManifest Package(string name, Version? platform)
        => new("Microsoft", name, new Version(28, 5, 0, 0), Guid.NewGuid(), Array.Empty<DependencyRef>(),
            Application: null, Platform: platform);

    private static AppManifest System(string version)
        => new("Microsoft", "System", Version.Parse(version), Guid.NewGuid(), Array.Empty<DependencyRef>());

    private static string KeyOf(AppManifest package, params AppManifest[] resolved)
    {
        var path = Path.Combine(TestScratch.Dir("al-runner-deferred-attempt-floor-5233-key"), "pkg.app");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "package bytes");
        return DependencyLoader.ComputeSourceDependencyCacheKey(
            package, path, resolved.Select(r => (r, "")).ToList());
    }

    // The defect's cache half: the partial DLL a compile without System writes and the complete
    // one a compile with it writes were stored under ONE key, so a run that had System was served
    // the partial one (measured: `source-cache HIT ... (16384 bytes` with all six dependencies
    // resolved) and any cache the broken build had already warmed kept doing so.
    [Fact]
    public void CacheKey_SeparatesACompileThatHadItsPlatformFloorFromOneThatDid_NotHave()
    {
        var testRunner = Package("Test Runner", new Version(28, 0, 0, 0));

        var without = KeyOf(testRunner);
        var withSystem = KeyOf(testRunner, System("28.0.54946.0"));
        var withOlderSystem = KeyOf(testRunner, System("27.0.0.0"));

        Assert.NotEqual(without, withSystem);
        // A System below the floor does not supply it, so it keys as the absent one.
        Assert.Equal(without, withOlderSystem);
        // And the supplied key is itself stable.
        Assert.Equal(withSystem, KeyOf(testRunner, System("28.0.54946.0")));
    }

    // Both directions of the same boundary: a package declaring no floor has nothing to supply, so
    // the closure around it must not change its key (and no existing key is orphaned for it).
    [Fact]
    public void CacheKey_OfAPackageWithNoFloor_IgnoresWhatElseWasResolved()
    {
        var noFloor = Package("Library Without Floor", platform: null);

        Assert.Equal(KeyOf(noFloor), KeyOf(noFloor, System("28.0.54946.0")));
        Assert.Null(DependencyLoader.SuppliedFloorsCacheTerm(noFloor, new[] { System("28.0.54946.0") }));
    }
}
