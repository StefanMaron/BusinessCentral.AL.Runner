using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1850 — the cross-bundle module identity dedup added for #1683
/// (<see cref="DependencyLoader.TryGetByAppId"/> / <see cref="DependencyLoader.RegisterLoaded"/>)
/// keyed reuse on AppId ALONE. Two unrelated `tests/runner-extras` suites
/// (`event-var-outstream` and `report-run-execution`) accidentally declared the
/// identical app.json `id`; the loader silently reused the first suite's compiled
/// module for the second, so the second suite's 4 tests never ran and one of the
/// first suite's tests ran twice — every line still printed PASS and the process
/// exited 0. #1856 removed that one instance by consolidating the two suites under
/// a fresh GUID, but did not touch the mechanism: the next copy-pasted app.json
/// that forgets to regenerate its `id` hits the exact same silent-drop.
///
/// This class constructs the collision deliberately (the natural instance no
/// longer exists in the repo — see #1850's own history) at two levels:
///
///  - <see cref="TwoSiblingAppsShareId_DifferentApps_AbortsNamingBothPathsAndGuid"/>
///    spawns the real runner against two synthetic sibling suites sharing a GUID,
///    reproducing the ORIGINAL bug's shape byte-for-byte (one bundle root, no
///    app.json of its own, two subdirectories each with their own app.json — the
///    same discovery shape `tests/runner-extras` uses). RED (pre-fix): exits 0,
///    output contains neither "FATAL" nor the shared guid. GREEN (post-fix):
///    exits non-zero, output names both suite directories and the shared guid.
///
///  - The two mechanism tests below pin <see cref="DependencyLoader"/>'s own
///    identity comparison directly, deterministically and without spawning a
///    process: same AppId + same {Name, Publisher, Version} must keep reusing the
///    cached module (the #1683 guarantee this fix must NOT regress); same AppId +
///    a mismatch on any of those three must throw
///    <see cref="AlRunner.Infrastructure.AppIdCollisionException"/> naming both
///    source paths and the AppId.
/// </summary>
public class AppIdCollisionLoudFailureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void TwoSiblingAppsShareId_DifferentApps_AbortsNamingBothPathsAndGuid()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-appid-collision-1850", Guid.NewGuid().ToString("N"));
        var suiteADir = Path.Combine(root, "collide-a");
        var suiteBDir = Path.Combine(root, "collide-b");
        Directory.CreateDirectory(suiteADir);
        Directory.CreateDirectory(suiteBDir);

        var sharedId = Guid.NewGuid().ToString();

        // Two GENUINELY different apps (different name) that accidentally declare
        // the same id — the exact shape of the event-var-outstream / report-run-execution
        // collision described in #1850, minus the two real suites (consolidated away by
        // #1856; this test constructs a fresh instance so the mechanism stays covered).
        File.WriteAllText(Path.Combine(suiteADir, "app.json"), $$"""
        {
          "id": "{{sharedId}}",
          "name": "AC Collide Suite A 1850",
          "publisher": "Repro1850",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61880, "to": 61889 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(suiteADir, "CollideA.al"), """
        codeunit 61880 "AC Collide A Tests 1850"
        {
            Subtype = Test;

            [Test]
            procedure OnlyInSuiteA_Runs()
            begin
                if 1 <> 1 then
                    Error('unreachable');
            end;
        }
        """);

        File.WriteAllText(Path.Combine(suiteBDir, "app.json"), $$"""
        {
          "id": "{{sharedId}}",
          "name": "AC Collide Suite B 1850",
          "publisher": "Repro1850",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61890, "to": 61899 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(suiteBDir, "CollideB.al"), """
        codeunit 61890 "AC Collide B Tests 1850"
        {
            Subtype = Test;

            [Test]
            procedure OnlyInSuiteB_Runs()
            begin
                if 1 <> 1 then
                    Error('unreachable');
            end;
        }
        """);

        // ONE bundle root with no app.json of its own and two subdirectories each
        // declaring their own — the same discovery shape tests/runner-extras uses
        // (BuildAppGroups finds two sibling AppGroups within one process), so this
        // hits the exact TryGetByAppId/RegisterLoaded call sites #1850 reported.
        var (output, exitCode) = RunRunner(root);

        // The exact defect: a silent-reuse collision must never report success.
        Assert.NotEqual(0, exitCode);
        Assert.Contains("FATAL", output);
        Assert.Contains("duplicate app id", output);
        // Names the shared guid and BOTH suite directories — not just "something failed".
        Assert.Contains(sharedId, output);
        Assert.Contains("collide-a", output);
        Assert.Contains("collide-b", output);
    }

    [Fact]
    public void TryGetByAppId_SameIdentityRegisteredEarlier_ReusesCachedAssembly()
    {
        // The #1683 guarantee this fix must not regress: the SAME app (identical
        // Name/Publisher/Version) resolving twice under one AppId must keep reusing
        // the already-loaded module, not recompile or reject it.
        var appId = Guid.NewGuid();
        var asm = typeof(DependencyLoader).Assembly;
        DependencyLoader.RegisterLoaded(appId, asm, "Shared Suite 1850", "Repro1850", "1.0.0.0", "/bundles/collide-a");

        var resolved = DependencyLoader.TryGetByAppId(
            appId, "Shared Suite 1850", "Repro1850", "1.0.0.0", "/bundles/collide-a-again");

        Assert.Same(asm, resolved);
    }

    [Fact]
    public void TryGetByAppId_DifferentAppSameId_ThrowsNamingBothPathsAndGuid()
    {
        var appId = Guid.NewGuid();
        var asmA = typeof(DependencyLoader).Assembly;
        DependencyLoader.RegisterLoaded(
            appId, asmA, "AC Collide Suite A 1850", "Repro1850", "1.0.0.0", "/bundles/collide-a");

        var ex = Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
            DependencyLoader.TryGetByAppId(
                appId, "AC Collide Suite B 1850", "Repro1850", "1.0.0.0", "/bundles/collide-b"));

        Assert.Contains(appId.ToString(), ex.Message);
        Assert.Contains("/bundles/collide-a", ex.Message);
        Assert.Contains("/bundles/collide-b", ex.Message);
        Assert.Equal(appId, ex.AppId);
        Assert.Equal("/bundles/collide-a", ex.ExistingSourcePath);
        Assert.Equal("/bundles/collide-b", ex.NewSourcePath);
        // Genuinely different apps (different Name) — the "regenerate the id" wording
        // is correct here, not the version-skew wording. See the sibling test below.
        Assert.False(ex.IsVersionSkew);
        Assert.Contains("Regenerate the", ex.Message);
    }

    [Fact]
    public void TryGetByAppId_SameNamePublisherDifferentVersion_ThrowsVersionSkewMessage()
    {
        // PR #1862 review, Note 1: Name+Publisher match but Version differs is the
        // SAME app built twice (most likely a stale .app in the package cache
        // shadowing a rebuilt source suite), not two unrelated apps that need a new
        // id. "Regenerate the id" would be actively wrong advice here — the id is
        // correct; the fix is a rebuild or a package/AL-output cache clear. The
        // check itself must still abort (two live modules for one AL identity is
        // exactly the #1683 TargetException hazard), but the message must say so.
        var appId = Guid.NewGuid();
        var asmA = typeof(DependencyLoader).Assembly;
        DependencyLoader.RegisterLoaded(
            appId, asmA, "AC Version Skew Suite 1850", "Repro1850", "1.0.0.0", "/bundles/skew-old");

        var ex = Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
            DependencyLoader.TryGetByAppId(
                appId, "AC Version Skew Suite 1850", "Repro1850", "2.0.0.0", "/bundles/skew-new"));

        Assert.True(ex.IsVersionSkew);
        Assert.Contains(appId.ToString(), ex.Message);
        Assert.Contains("/bundles/skew-old", ex.Message);
        Assert.Contains("/bundles/skew-new", ex.Message);
        Assert.Contains("same app", ex.Message);
        Assert.Contains("stale build", ex.Message);
        Assert.DoesNotContain("Regenerate the", ex.Message);
    }

    [Fact]
    public void RegisterLoaded_DifferentAppSameId_ThrowsNamingBothPathsAndGuid()
    {
        var appId = Guid.NewGuid();
        var asmA = typeof(DependencyLoader).Assembly;
        var asmB = typeof(object).Assembly;
        DependencyLoader.RegisterLoaded(appId, asmA, "App A", "Vendor", "1.0.0.0", "/bundles/a");

        var ex = Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
            DependencyLoader.RegisterLoaded(appId, asmB, "App B", "Vendor", "1.0.0.0", "/bundles/b"));

        Assert.Contains(appId.ToString(), ex.Message);
        Assert.Contains("/bundles/a", ex.Message);
        Assert.Contains("/bundles/b", ex.Message);
    }
}
