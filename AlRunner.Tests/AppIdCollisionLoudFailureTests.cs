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

        var root = TestScratch.Dir("al-runner-appid-collision-1850");
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

    // #5079: across --server requests (reuse epochs) a module answers only for the source it was
    // compiled from. Epoch-bumping tests stay in this class, which xUnit runs serially.
    [Fact]
    public void EarlierEpochsModule_IsReusedOnlyForTheSameSourceFingerprint()
    {
        var appId = Guid.NewGuid();
        var asm = typeof(DependencyLoader).Assembly;
        DependencyLoader.RegisterLoaded(appId, asm, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/x", "FP-X");
        DependencyLoader.BeginReuseEpoch();

        Assert.Null(DependencyLoader.TryGetByAppId(appId, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/y", "FP-Y"));
        Assert.Null(DependencyLoader.TryGetByAppId(appId, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/y", null));
        Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/y", "FP-X"));
    }

    [Fact]
    public void EarlierEpochsModule_IsReplacedByThisEpochsRegistration()
    {
        var appId = Guid.NewGuid();
        // Throwaway assemblies: replacing an entry retires the one it displaces (#3974).
        System.Reflection.Assembly Dynamic(string name) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var asmX = Dynamic("Reuse5079X");
        var asmY = Dynamic("Reuse5079Y");
        DependencyLoader.RegisterLoaded(appId, asmX, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/x", "FP-X");
        DependencyLoader.BeginReuseEpoch();
        DependencyLoader.RegisterLoaded(appId, asmY, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/y", "FP-Y");

        // Within this epoch a sibling resolving the id shares Y's module (#1683), not X's.
        Assert.Same(asmY, DependencyLoader.TryGetByAppId(appId, "Reuse 5079", "Repro5079", "1.0.0.0", "/bundles/z", "FP-Z"));
    }

    // #5520: a module answers for the folder it was compiled from. When that folder no longer
    // holds an app (the app moved), an ask from another folder is the same app at a new path, not
    // another bundle's copy of it.
    private static string AppDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), "{}");
        return dir;
    }

    private static System.Reflection.Assembly Dynamic(string name) =>
        System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName(name), System.Reflection.Emit.AssemblyBuilderAccess.Run);

    [Fact]
    public void MovedApp_FolderGone_IsNotServedFromItsOldFolder()
    {
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Moved5520Gone");
            var one = AppDir(root, "one");
            DependencyLoader.RegisterLoaded(appId, asm, "Moved 5520", "Repro5520", "1.0.0.0", one);
            Directory.Delete(one, recursive: true);
            var two = AppDir(root, "two");

            Assert.Null(DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", two));
            Assert.Equal(one, DependencyLoader.MovedSourcePath(appId, two));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_FolderLeftWithoutAppJson_IsAlsoMoved()
    {
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var one = AppDir(root, "one");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Moved5520Left"), "Moved 5520", "Repro5520", "1.0.0.0", one);
            File.Delete(Path.Combine(one, "app.json"));
            File.WriteAllText(Path.Combine(one, "leftover.txt"), "x");
            var two = AppDir(root, "two");

            Assert.Null(DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", two));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_OldFolderStillHoldsTheApp_IsStillCrossBundleReuse()
    {
        // #1683: two folders that both hold the app are two bundles carrying one identity; the
        // second reuses the first's module. Only a folder that no longer holds the app is "moved".
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Moved5520Copy");
            var one = AppDir(root, "one");
            var two = AppDir(root, "two");
            DependencyLoader.RegisterLoaded(appId, asm, "Moved 5520", "Repro5520", "1.0.0.0", one);

            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", two));
            Assert.Null(DependencyLoader.MovedSourcePath(appId, two));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_AskedAboutItsOwnFolder_IsNotMoved()
    {
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var one = AppDir(root, "one");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Moved5520Own"), "Moved 5520", "Repro5520", "1.0.0.0", one);

            // The folder itself is never "moved", even once it has lost its app.json: it is the ask.
            File.Delete(Path.Combine(one, "app.json"));
            Assert.Null(DependencyLoader.MovedSourcePath(appId, one));
            Assert.Null(DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", one)); // #2594: same folder recompiles
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_RegistrationFromTheNewFolder_ReplacesTheOldModule()
    {
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var asmOld = Dynamic("Moved5520Old");
            var asmNew = Dynamic("Moved5520New");
            var one = AppDir(root, "one");
            DependencyLoader.RegisterLoaded(appId, asmOld, "Moved 5520", "Repro5520", "1.0.0.0", one);
            Directory.Delete(one, recursive: true);
            var two = AppDir(root, "two");
            DependencyLoader.RegisterLoaded(appId, asmNew, "Moved 5520", "Repro5520", "1.0.0.0", two);

            // A sibling bundle resolving the id now shares the module compiled at the new folder.
            var three = AppDir(root, "three");
            Assert.Same(asmNew, DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", three));
            Assert.Equal(two, DependencyLoader.LoadedSourcePath(appId));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_WithABumpedVersion_IsNotACollision()
    {
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var one = AppDir(root, "one");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Moved5520V1"), "Moved 5520", "Repro5520", "1.0.0.0", one);
            Directory.Delete(one, recursive: true);
            var two = AppDir(root, "two");

            Assert.Null(DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "2.0.0.0", two));
            var asmNew = Dynamic("Moved5520V2");
            DependencyLoader.RegisterLoaded(appId, asmNew, "Moved 5520", "Repro5520", "2.0.0.0", two);
            Assert.Equal(two, DependencyLoader.LoadedSourcePath(appId));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_AModuleRegisteredFromAFileIsNeverMoved()
    {
        // A package path can vanish when the workspace is rewritten; that is not an app folder moving.
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Moved5520File");
            Directory.CreateDirectory(root);
            var pkg = Path.Combine(root, "dep.app");
            File.WriteAllText(pkg, "x");
            DependencyLoader.RegisterLoaded(appId, asm, "Moved 5520", "Repro5520", "1.0.0.0", pkg);
            File.Delete(pkg);
            var two = AppDir(root, "two");

            Assert.Null(DependencyLoader.MovedSourcePath(appId, two));
            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", two));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_RegisteredFromASubfolderBelowTheAppJson_IsStillCrossBundleReuse()
    {
        // --server registers bundleAbs, which can be a folder BELOW the one holding app.json
        // (FindBucketRoot climbs). That folder never holds an app.json itself, so it must not read
        // as "moved": a second folder asking for the id shares the module (#1892).
        var root = TestScratch.Dir("al-runner-moved-5520");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Moved5520Sub");
            var app = AppDir(root, "app");
            var sub = Path.Combine(app, "src");
            Directory.CreateDirectory(sub);
            var other = AppDir(root, "other");
            DependencyLoader.RegisterLoaded(appId, asm, "Moved 5520", "Repro5520", "1.0.0.0", sub);

            Assert.Null(DependencyLoader.MovedSourcePath(appId, other));
            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Moved 5520", "Repro5520", "1.0.0.0", other));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // #5528: --server registers bundleAbs, which can sit below the folder holding the app.json
    // (FindBucketRoot climbs). "Moved" is asked of that app root, not of the registered folder.
    private static string SubOf(string appRoot, string name)
    {
        var dir = Path.Combine(appRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MovedApp_RegisteredFromASubfolder_IsMovedWithItsAppRoot()
    {
        var root = TestScratch.Dir("al-runner-moved-5528");
        try
        {
            var appId = Guid.NewGuid();
            var one = AppDir(root, "one");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Moved5528V1"), "Moved 5528", "Repro5528", "1.0.0.0", SubOf(one, "test"));
            Directory.Delete(one, recursive: true);
            var twoTest = SubOf(AppDir(root, "two"), "test");

            // The version is bumped as well: asked before identity, so no collision (#5520).
            Assert.Null(DependencyLoader.TryGetByAppId(appId, "Moved 5528", "Repro5528", "2.0.0.0", twoTest));
            Assert.Equal(one, DependencyLoader.MovedSourcePath(appId, twoTest));

            var asmNew = Dynamic("Moved5528V2");
            DependencyLoader.RegisterLoaded(appId, asmNew, "Moved 5528", "Repro5528", "2.0.0.0", twoTest);
            Assert.Equal(twoTest, DependencyLoader.LoadedSourcePath(appId));
            Assert.Same(asmNew, DependencyLoader.TryGetByAppId(appId, "Moved 5528", "Repro5528", "2.0.0.0", AppDir(root, "three")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_SubfolderRegistration_AppRootStillHoldsTheApp_IsReuseWhicheverWasRegisteredFirst()
    {
        // #1683/#1892: [appA/test, appB] share an id and both roots still hold the app: reuse. The
        // order must not matter: appB registered first, then appA/test asks, and the other way round.
        var root = TestScratch.Dir("al-runner-moved-5528");
        try
        {
            var a = AppDir(root, "a");
            var b = AppDir(root, "b");
            var aTest = SubOf(a, "test");

            var first = Guid.NewGuid();
            var asmFirst = Dynamic("Moved5528AFirst");
            DependencyLoader.RegisterLoaded(first, asmFirst, "Moved 5528", "Repro5528", "1.0.0.0", aTest);
            Assert.Same(asmFirst, DependencyLoader.TryGetByAppId(first, "Moved 5528", "Repro5528", "1.0.0.0", b));
            Assert.Null(DependencyLoader.MovedSourcePath(first, b));

            var second = Guid.NewGuid();
            var asmSecond = Dynamic("Moved5528BFirst");
            DependencyLoader.RegisterLoaded(second, asmSecond, "Moved 5528", "Repro5528", "1.0.0.0", b);
            Assert.Same(asmSecond, DependencyLoader.TryGetByAppId(second, "Moved 5528", "Repro5528", "1.0.0.0", aTest));
            Assert.Null(DependencyLoader.MovedSourcePath(second, aTest));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_SubfolderRegistration_RootNamedByTheNearestAppJson()
    {
        // <ws>/app.json and <ws>/tests/app.json are two apps. A module registered from
        // <ws>/tests/src belongs to <ws>/tests: losing the outer app.json does not move it.
        var root = TestScratch.Dir("al-runner-moved-5528");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Moved5528Nested");
            var ws = AppDir(root, "ws");
            var inner = AppDir(ws, "tests");
            DependencyLoader.RegisterLoaded(appId, asm, "Moved 5528", "Repro5528", "1.0.0.0", SubOf(inner, "src"));
            File.Delete(Path.Combine(ws, "app.json"));
            var other = AppDir(root, "other");

            Assert.Null(DependencyLoader.MovedSourcePath(appId, other));
            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Moved 5528", "Repro5528", "1.0.0.0", other));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void MovedApp_APackageFileUnderAnAppFolder_IsNeverMoved()
    {
        // The climb applies to a folder only: a package file sitting under a workspace that holds an
        // app.json has no app root, so that workspace's app.json going away moves nothing.
        var root = TestScratch.Dir("al-runner-moved-5528");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Moved5528Pkg");
            var ws = AppDir(root, "ws");
            var pkg = Path.Combine(SubOf(ws, ".alpackages"), "dep.app");
            File.WriteAllText(pkg, "x");
            DependencyLoader.RegisterLoaded(appId, asm, "Moved 5528", "Repro5528", "1.0.0.0", pkg);
            File.Delete(Path.Combine(ws, "app.json"));
            var two = AppDir(root, "two");

            Assert.Null(DependencyLoader.MovedSourcePath(appId, two));
            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Moved 5528", "Repro5528", "1.0.0.0", two));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // #5532: a re-request of the SAME app tree after a version bump is a rebuild, not a collision, however
    // the second request spells the folder. The tree is the app root (the folder holding app.json), the
    // same notion #5528 records; a collision needs two roots that both still hold the app (#1850).
    private static void AssertRebuiltNotACollision(string registered, string asked, string appRoot)
    {
        var appId = Guid.NewGuid();
        var sibling = AppDir(Path.GetDirectoryName(appRoot.TrimEnd(Path.DirectorySeparatorChar))!, Path.GetFileName(appRoot.TrimEnd(Path.DirectorySeparatorChar)) + "-sibling");
        var asmNew = Dynamic("Tree5532V2" + appId.ToString("N"));
        DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532V1" + appId.ToString("N")), "Tree 5532", "Repro5532", "1.0.0.0", registered);

        // The lookup answers "recompile", exactly as it does for an identical path (#2556).
        Assert.Null(DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", asked));
        // The registration of the rebuild replaces the old module rather than throwing.
        DependencyLoader.RegisterLoaded(appId, asmNew, "Tree 5532", "Repro5532", "2.0.0.0", asked);
        Assert.Equal(asked, DependencyLoader.LoadedSourcePath(appId));
        // A sibling bundle resolving the id now shares the rebuild (#1683), not the retired v1 module.
        Assert.Same(asmNew, DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", sibling));
    }

    [Fact]
    public void SameAppTree_AppRootAskedAfterItsSubfolder_VersionBump_IsRebuiltNotACollision()
    {
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var a = AppDir(root, "a");
            AssertRebuiltNotACollision(SubOf(a, "test"), a, a);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SameAppTree_ASiblingSubfolderAsked_VersionBump_IsRebuiltNotACollision()
    {
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var a = AppDir(root, "a");
            AssertRebuiltNotACollision(SubOf(a, "test"), SubOf(a, "src"), a);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SameAppTree_AppRootRegisteredFirst_ThenItsSubfolder_VersionBump_IsRebuiltNotACollision()
    {
        // The other direction of one tree: root -> subfolder is as much a rebuild as subfolder -> root.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var a = AppDir(root, "a");
            var test = SubOf(a, "test");
            AssertRebuiltNotACollision(a, test, a);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SameAppTree_TrailingSeparatorSpellings_VersionBump_IsRebuiltNotACollision()
    {
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var a = AppDir(root, "a");
            var sep = Path.DirectorySeparatorChar.ToString();
            AssertRebuiltNotACollision(SubOf(a, "test"), SubOf(a, "test") + sep, a);                // a/test -> a/test/
            AssertRebuiltNotACollision(SubOf(a, "src") + sep, SubOf(a, "src"), a);                 // and back
            AssertRebuiltNotACollision(SubOf(a, "test"), a + sep, a);                              // the root itself, with a separator
            AssertRebuiltNotACollision(a + sep, SubOf(a, "test"), a);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SameAppTree_DotSegmentSpelling_VersionBump_IsRebuiltNotACollision()
    {
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var a = AppDir(root, "a");
            SubOf(a, "src");
            AssertRebuiltNotACollision(SubOf(a, "test"), Path.Combine(a, "src", "..", "test"), a);
            AssertRebuiltNotACollision(a, Path.Combine(a, ".", "test", ".."), a);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [SkippableFact]
    public void SameAppTree_ASymlinkedSpellingOfTheFolder_VersionBump_IsRebuiltNotACollision()
    {
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var a = AppDir(root, "a");
            SubOf(a, "test");
            var link = Path.Combine(root, "link-to-a");
            Skip.IfNot(TrySymlink(a, link), "the filesystem does not allow creating symlinks here");
            AssertRebuiltNotACollision(SubOf(a, "test"), Path.Combine(link, "test"), a);   // via the link, below it
            AssertRebuiltNotACollision(Path.Combine(link, "test"), a, a);                  // registered via the link
            AssertRebuiltNotACollision(a, link, a);                                        // the link is the root
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static bool TrySymlink(string target, string link)
    {
        try { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
        catch { return false; }
    }

    [Fact]
    public void SameAppTree_RenamedAppInTheSameFolder_IsRebuiltNotACollision()
    {
        // #2556 for an identical path says any identity change on one tree is an edit, not a collision.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var appId = Guid.NewGuid();
            var a = AppDir(root, "a");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532Old"), "Tree 5532", "Repro5532", "1.0.0.0", SubOf(a, "test"));

            Assert.Null(DependencyLoader.TryGetByAppId(appId, "Tree 5532 Renamed", "Repro5532", "1.0.0.0", a));
            DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532New"), "Tree 5532 Renamed", "Repro5532", "1.0.0.0", a);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SameAppTree_SameIdentity_StillSharesTheModule()
    {
        // #1683/#1892 is unchanged: one tree asked under another spelling with the identity it was
        // compiled under is the same module, not a recompile.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var appId = Guid.NewGuid();
            var asm = Dynamic("Tree5532Same");
            var a = AppDir(root, "a");
            DependencyLoader.RegisterLoaded(appId, asm, "Tree 5532", "Repro5532", "1.0.0.0", SubOf(a, "test"));

            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "1.0.0.0", SubOf(a, "src")));
            Assert.Same(asm, DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "1.0.0.0", a));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void TwoLiveAppRoots_AtDifferentVersions_RemainACollision()
    {
        // Two folders that both still hold the app at different versions are #1850's case: the
        // message keeps naming both and keeps saying one is a stale build.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var appId = Guid.NewGuid();
            var a = AppDir(root, "a");
            var b = AppDir(root, "b");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532A"), "Tree 5532", "Repro5532", "1.0.0.0", SubOf(a, "test"));

            var ex = Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
                DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", SubOf(b, "test")));
            Assert.True(ex.IsVersionSkew);
            Assert.Contains(SubOf(a, "test"), ex.Message);
            Assert.Contains(SubOf(b, "test"), ex.Message);
            Assert.Contains("stale build", ex.Message);

            Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
                DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532B"), "Tree 5532", "Repro5532", "2.0.0.0", b));
            Assert.Equal(SubOf(a, "test"), DependencyLoader.LoadedSourcePath(appId));   // the throw left the entry alone
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void NestedAppRoots_AreTwoTrees_SoAVersionBumpBetweenThemRemainsACollision()
    {
        // <ws>/app.json and <ws>/tests/app.json are two apps (FindBucketRoot names the nearest): asking
        // for one after registering the other is not "the same tree" because one contains the other.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var appId = Guid.NewGuid();
            var ws = AppDir(root, "ws");
            var inner = AppDir(ws, "tests");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532Nested"), "Tree 5532", "Repro5532", "1.0.0.0", SubOf(inner, "src"));

            Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
                DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", ws));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void APackageRegistration_HasNoAppRoot_SoItIsNeverTheSameTree()
    {
        // A null AppRoot (a package file) matches no folder: a version bump asked from a folder stays a
        // collision exactly as before, and nothing here dereferences the missing root.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var appId = Guid.NewGuid();
            Directory.CreateDirectory(root);
            var pkg = Path.Combine(root, "dep.app");
            File.WriteAllText(pkg, "x");
            DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532Pkg"), "Tree 5532", "Repro5532", "1.0.0.0", pkg);
            var a = AppDir(root, "a");

            Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
                DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", a));
            // And an ask from under no app.json at all has no root to match either.
            var bare = Path.Combine(root, "bare");
            Directory.CreateDirectory(bare);
            Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
                DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", bare));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [SkippableFact]
    public void TwoFoldersDifferingOnlyInCase_AreTwoTrees_WhereTheFilesystemSaysSo()
    {
        // The comparison follows the platform: Linux is case-sensitive, so `app` and `APP` are two
        // folders there and a version bump between them stays a collision. Skipped where one folder
        // answers to both spellings.
        var root = TestScratch.Dir("al-runner-tree-5532");
        try
        {
            var lower = AppDir(root, "app");
            var upper = Path.Combine(root, "APP");
            Directory.CreateDirectory(upper);
            Skip.If(Directory.GetDirectories(root).Length < 2, "the filesystem folds case: one folder answers to both spellings");
            File.WriteAllText(Path.Combine(upper, "app.json"), "{}");
            var appId = Guid.NewGuid();
            // Registered from a subfolder, so the SourcePath comparison (case-folding since #2556) does not decide it.
            DependencyLoader.RegisterLoaded(appId, Dynamic("Tree5532Case"), "Tree 5532", "Repro5532", "1.0.0.0", SubOf(lower, "test"));

            Assert.Throws<AlRunner.Infrastructure.AppIdCollisionException>(() =>
                DependencyLoader.TryGetByAppId(appId, "Tree 5532", "Repro5532", "2.0.0.0", upper));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
