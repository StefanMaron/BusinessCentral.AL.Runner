// DependencyResolverTests — version-aware resolution contract.
//
// Root cause being tested
// -----------------------
// DependencyResolver previously indexed .app packages with "first-wins" semantics
// and ignored the declared minimum version when selecting among candidates. An ISV
// that vendors a stale Microsoft symbol-only .app (e.g. Tests-TestLibraries v17.0)
// in its .alpackages dir could cause the resolver to bind to v17 even when v28.1
// was available in a package-cache dir, because the ISV .alpackages dir is indexed
// first. BC then compiled against v17 symbols, baking v17 function IDs into emitted
// C#. At runtime, BC 28.1 dispatch only recognises current IDs → NavNCLCompilationException.
//
// Fix: resolver now keeps ALL candidates per AppId / (Name, Publisher) and selects
// the highest-version candidate whose version >= the declared minimum. The minimum-
// version semantics match what a real BC build (alc) does.
//
// Test strategy
// -------------
// Unit tests against DependencyResolver in isolation, using synthetic minimal .app
// fixtures written to a per-test temp directory. Asserts concrete versions and paths.

using System.IO.Compression;
using System.Text;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class DependencyResolverTests : IDisposable
{
    private readonly string _root;

    public DependencyResolverTests()
    {
        _root = TestScratch.Dir("al-runner-resolver-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Part 1: highest-satisfying-version selection ───────────────────────────

    /// <summary>
    /// Two dirs: A has v17.0.0.0, B has v28.1.49838.50621 for the SAME AppId.
    /// A dep declaring minimum v17 must bind to v28.1, not v17.
    /// This is the exact scenario that triggered the stale-symbol workaround.
    /// FAILS on old (first-wins) code, PASSES after the fix.
    /// </summary>
    [Fact]
    public void TwoVersions_SameAppId_HigherVersionChosen_WhenBothSatisfyMinimum()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000001";
        var dirA = MakeDir("A");
        var dirB = MakeDir("B");

        WriteApp(dirA, "TestLib_v17.app",   appId, "Tests-TestLibraries", "Microsoft", "17.0.0.0");
        WriteApp(dirB, "TestLib_v28.app",   appId, "Tests-TestLibraries", "Microsoft", "28.1.49838.50621");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(17, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(28, 1, 49838, 50621), result[0].Manifest.Version);
        Assert.Contains("v28", result[0].AppPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dirs in reverse order (v28 dir first, v17 dir second) — should still pick v28.1.
    /// </summary>
    [Fact]
    public void TwoVersions_SameAppId_HigherVersionChosen_RegardlessOfDirOrder()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000002";
        var dirA = MakeDir("C");
        var dirB = MakeDir("D");

        WriteApp(dirA, "TestLib_v28.app",   appId, "Tests-TestLibraries", "Microsoft", "28.1.49838.50621");
        WriteApp(dirB, "TestLib_v17.app",   appId, "Tests-TestLibraries", "Microsoft", "17.0.0.0");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(17, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(28, 1, 49838, 50621), result[0].Manifest.Version);
    }

    /// <summary>
    /// Only one version available; it satisfies the minimum → resolves to that version.
    /// </summary>
    [Fact]
    public void OnlyVersion_SatisfiesMinimum_ReturnsIt()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000003";
        var dir = MakeDir("E");
        WriteApp(dir, "TestLib.app", appId, "MyApp", "Publisher", "5.0.0.0");

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "MyApp", "Publisher",
            new Version(5, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(5, 0, 0, 0), result[0].Manifest.Version);
    }

    // ── Part 2: version-not-satisfied error message ────────────────────────────

    /// <summary>
    /// Dep requires minimum v29.0; only v17 and v28.1 are available.
    /// Must throw DependencyVersionMismatchException whose message names the available
    /// versions. (This is a version-mismatch problem, not a provisioning gap — #2095.)
    /// </summary>
    [Fact]
    public void MinimumNotSatisfied_ThrowsWithVersionDetail()
    {
        var appId = "aaaaaaaa-0000-0000-0000-000000000004";
        var dirA = MakeDir("F");
        var dirB = MakeDir("G");

        WriteApp(dirA, "Lib_v17.app",  appId, "TestLib", "Microsoft", "17.0.0.0");
        WriteApp(dirB, "Lib_v28.app",  appId, "TestLib", "Microsoft", "28.1.49838.50621");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        var dep = new DependencyRef(Guid.Parse(appId), "TestLib", "Microsoft",
            new Version(29, 0, 0, 0));

        var ex = Assert.Throws<AlRunner.Infrastructure.DependencyVersionMismatchException>(
            () => resolver.Resolve(new[] { dep }));
        // Error must mention the too-low versions so the problem is obviously a version issue.
        Assert.Contains("29.0", ex.Message);
        Assert.Contains("17.0", ex.Message);
        Assert.Contains("28.1", ex.Message);
    }

    // ── Part 2b: completely-absent dep → MissingDependencyException ───────────

    /// <summary>
    /// A dep declared in the manifest is completely absent from every cache dir.
    /// Must throw MissingDependencyException (not InvalidOperationException) so Program.cs
    /// can emit a loud provisioning-gap message and abort before a doomed compile.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ThrowsMissingDependencyException()
    {
        var emptyDir = MakeDir("MDE_empty");
        var resolver = new DependencyResolver(new[] { emptyDir });
        var dep = new DependencyRef(
            Guid.Parse("bee8cf2f-494a-42f4-aabd-650e87934d39"),
            "Business Foundation Test Libraries", "Microsoft", new Version(28, 2, 0, 0));

        Assert.Throws<AlRunner.Infrastructure.MissingDependencyException>(
            () => resolver.Resolve(new[] { dep }));
    }

    /// <summary>
    /// MissingDependencyException carries the dep's identity + searched dirs.
    /// The exception message names the publisher, name, and searched dir so the user sees
    /// exactly what is missing and where it was looked for.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ExceptionNamesDepAndSearchedDir()
    {
        var dir = MakeDir("MDE_detail");
        var resolver = new DependencyResolver(new[] { dir });
        var depId = Guid.Parse("bee8cf2f-494a-42f4-aabd-650e87934d39");
        var dep = new DependencyRef(
            depId, "Business Foundation Test Libraries", "Microsoft", new Version(28, 2, 0, 0));

        var ex = Assert.Throws<AlRunner.Infrastructure.MissingDependencyException>(
            () => resolver.Resolve(new[] { dep }));

        Assert.Equal("Microsoft", ex.DepPublisher);
        Assert.Equal("Business Foundation Test Libraries", ex.DepName);
        Assert.Equal("28.2.0.0", ex.DepVersion);
        Assert.Equal(depId, ex.DepAppId);
        Assert.Contains(dir, ex.SearchedDirs);
    }

    /// <summary>
    /// ToDetailedMessage for a Microsoft dep names the al-runner provision command and
    /// the DownloadArtifacts test-apps fix so the user can resolve it in one command.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ToDetailedMessage_NamesProvisionCommandForMicrosoftDep()
    {
        var dir = MakeDir("MDE_msg_ms");
        var ex = new AlRunner.Infrastructure.MissingDependencyException(
            "Microsoft", "Business Foundation Test Libraries", "28.2.0.0",
            Guid.Parse("bee8cf2f-494a-42f4-aabd-650e87934d39"),
            new[] { dir });

        var msg = ex.ToDetailedMessage("28.2.50931.52786");

        // Names the missing dep.
        Assert.Contains("Business Foundation Test Libraries", msg);
        Assert.Contains("28.2.0.0", msg);
        Assert.Contains("Microsoft", msg);
        // Names the provision command.
        Assert.Contains("al-runner provision", msg);
        // Names the DownloadArtifacts test-apps fix.
        Assert.Contains("test-apps", msg);
        Assert.Contains("28.2.50931.52786", msg);
        // Frames it as a provisioning gap, not a user-code error.
        Assert.Contains("PROVISIONING gap", msg);
        Assert.Contains("your code is NOT the problem", msg);
    }

    /// <summary>
    /// ToDetailedMessage for a non-Microsoft dep does NOT mention al-runner provision
    /// (a third-party dep can't be auto-provisioned from the MS CDN).
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ToDetailedMessage_NoProvisionForThirdPartyDep()
    {
        var dir = MakeDir("MDE_msg_3p");
        var ex = new AlRunner.Infrastructure.MissingDependencyException(
            "Contoso", "Contoso Core Library", "5.0.0.0",
            Guid.NewGuid(), new[] { dir });

        var msg = ex.ToDetailedMessage("28.2.50931.52786");

        Assert.Contains("Contoso Core Library", msg);
        Assert.Contains("PROVISIONING gap", msg);
        // Should NOT suggest the MS provision path for a third-party dep.
        Assert.DoesNotContain("test-apps", msg);
        Assert.DoesNotContain("platform-apps", msg);
    }

    /// <summary>
    /// #2095: the non-Microsoft branch names the flag CONCRETELY (with an example dir)
    /// and says where that dir usually lives, for an agent that has never used this tool.
    /// </summary>
    [Fact]
    public void DepCompletelyAbsent_ToDetailedMessage_ThirdPartyDep_NamesPackageCacheFlagConcretely()
    {
        var dir = MakeDir("MDE_msg_3p_concrete");
        var ex = new AlRunner.Infrastructure.MissingDependencyException(
            "Contoso", "Contoso Core Library", "5.0.0.0",
            Guid.NewGuid(), new[] { dir });

        var msg = ex.ToDetailedMessage("28.2.50931.52786");

        Assert.Contains("--package-cache <dir>", msg);
        Assert.Contains(".alpackages", msg);
    }

    /// <summary>
    /// #2095: MissingDependencyException is recognized by the shared
    /// IDependencyProvisioningDiagnostic marker Program.cs uses to special-case both
    /// dependency-resolution exceptions ahead of the generic COMPILE-FAIL path.
    /// </summary>
    [Fact]
    public void MissingDependencyException_ImplementsSharedProvisioningDiagnosticInterface()
    {
        var ex = new AlRunner.Infrastructure.MissingDependencyException(
            "Contoso", "Contoso Core Library", "5.0.0.0", Guid.NewGuid(), Array.Empty<string>());

        Assert.IsAssignableFrom<AlRunner.Infrastructure.IDependencyProvisioningDiagnostic>(ex);
    }

    // ── Part 2c: dep found but every version too old → DependencyVersionMismatchException ──

    /// <summary>
    /// #2095: DependencyVersionMismatchException.ToDetailedMessage names the "VERSION gap"
    /// (not "PROVISIONING gap" — the dep IS in the cache, just too old), tells the reader
    /// to obtain a newer build, and does NOT repeat the searched directories (already
    /// implied by "Available (all too old)").
    /// </summary>
    [Fact]
    public void DependencyVersionMismatch_ToDetailedMessage_NamesVersionGapAndNewerBuildAdvice()
    {
        var ex = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            "Acme Corp", "Acme Add-On", "2.0.0.0", Guid.NewGuid(),
            new[] { "/some/cache/dir" }, "1.0.0.0");

        var msg = ex.ToDetailedMessage();

        Assert.Contains("VERSION gap", msg);
        Assert.Contains("your code is NOT the problem", msg);
        Assert.Contains("Acme Add-On", msg);
        Assert.Contains("2.0.0.0", msg);
        Assert.Contains("1.0.0.0", msg);
        Assert.Contains("--package-cache", msg);
        // Not a compile failure, not the missing-dep wording.
        Assert.DoesNotContain("COMPILE-FAIL", msg);
        Assert.DoesNotContain("PROVISIONING gap", msg);
    }

    /// <summary>
    /// #2095 root cause: the short .Message unconditionally appended "Stack: " even when
    /// the too-old dependency was a ROOT of the resolve call (empty chain), leaving a
    /// dangling "Stack: " with nothing after it. Must be omitted entirely, not printed empty.
    /// </summary>
    [Fact]
    public void DependencyVersionMismatch_RootLevelDependency_NoDanglingStackSegment()
    {
        var appId = "eeeeeeee-0000-0000-0000-000000000001";
        var dir = MakeDir("VM_root");
        WriteApp(dir, "App_v1.app", appId, "RootDep", "SomePub", "1.0.0.0");

        var resolver = new DependencyResolver(new[] { dir });
        // Root-level dep (empty stack) requiring a version that isn't available.
        var dep = new DependencyRef(Guid.Parse(appId), "RootDep", "SomePub", new Version(2, 0, 0, 0));

        var ex = Assert.Throws<AlRunner.Infrastructure.DependencyVersionMismatchException>(
            () => resolver.Resolve(new[] { dep }));

        Assert.Null(ex.DependencyStack);
        Assert.DoesNotContain("Stack:", ex.Message);
        Assert.DoesNotContain("Dependency chain:", ex.ToDetailedMessage());
    }

    /// <summary>
    /// DependencyVersionMismatchException implements the same shared marker interface as
    /// MissingDependencyException, so Program.cs recognizes both without a type check per
    /// exception name.
    /// </summary>
    [Fact]
    public void DependencyVersionMismatchException_ImplementsSharedProvisioningDiagnosticInterface()
    {
        var ex = new AlRunner.Infrastructure.DependencyVersionMismatchException(
            "Acme Corp", "Acme Add-On", "2.0.0.0", Guid.NewGuid(),
            Array.Empty<string>(), "1.0.0.0");

        Assert.IsAssignableFrom<AlRunner.Infrastructure.IDependencyProvisioningDiagnostic>(ex);
    }

    /// <summary>
    /// Version near-miss (dep found but below minimum) throws DependencyVersionMismatchException,
    /// not MissingDependencyException — the two need different advice (#2095).
    /// </summary>
    [Fact]
    public void VersionNearMiss_ThrowsDependencyVersionMismatchException_NotMissingDependencyException()
    {
        var appId = "dddddddd-0000-0000-0000-000000000001";
        var dir = MakeDir("MDE_nearmiss");
        WriteApp(dir, "App_v5.app", appId, "SomeLib", "SomePub", "5.0.0.0");

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "SomeLib", "SomePub",
            new Version(10, 0, 0, 0)); // requires v10 but only v5 exists

        // Must be DependencyVersionMismatchException (version near-miss), NOT
        // MissingDependencyException (completely absent).
        var ex = Assert.Throws<AlRunner.Infrastructure.DependencyVersionMismatchException>(
            () => resolver.Resolve(new[] { dep }));
        Assert.IsNotType<AlRunner.Infrastructure.MissingDependencyException>(ex);
        Assert.Contains("5.0", ex.Message);
    }

    // ── Part 3: Name+Publisher fallback ───────────────────────────────────────

    /// <summary>
    /// Dep declares AppId=empty (no GUID); resolver must fall back to Name+Publisher lookup
    /// and still pick the highest satisfying version.
    /// </summary>
    [Fact]
    public void NamePublisherFallback_PicksHighestSatisfyingVersion()
    {
        var appId = "bbbbbbbb-0000-0000-0000-000000000001";
        var dirA = MakeDir("H");
        var dirB = MakeDir("I");

        WriteApp(dirA, "App_v10.app", appId, "FooApp", "BarPub", "10.0.0.0");
        WriteApp(dirB, "App_v20.app", appId, "FooApp", "BarPub", "20.0.0.0");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        // Note: AppId = Guid.Empty → name+publisher lookup path.
        var dep = new DependencyRef(Guid.Empty, "FooApp", "BarPub", new Version(10, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(20, 0, 0, 0), result[0].Manifest.Version);
    }

    // ── Part 4: AppId near-miss must NOT fall through to Name+Publisher ────────

    /// <summary>
    /// Dep specifies AppId X. The index has AppId X but only at v5 (too old for min=v10).
    /// A DIFFERENT app with the same (Name, Publisher) but AppId Y is also in the index.
    /// The resolver must NOT silently pick AppId Y — that is a different package.
    /// It must throw/return-false, reporting the version near-miss.
    /// </summary>
    [Fact]
    public void AppIdNearMiss_DoesNotFallThroughToNamePublisher()
    {
        var appIdX = "cccccccc-0000-0000-0000-000000000001";
        var appIdY = "cccccccc-0000-0000-0000-000000000002";
        var dirA = MakeDir("J");
        var dirB = MakeDir("K");

        // AppId X with old version in dirA.
        WriteApp(dirA, "AppX_v5.app",  appIdX, "Shared", "Vendor", "5.0.0.0");
        // AppId Y with same name/publisher but different AppId in dirB (newer version).
        WriteApp(dirB, "AppY_v20.app", appIdY, "Shared", "Vendor", "20.0.0.0");

        var resolver = new DependencyResolver(new[] { dirA, dirB });
        // Ask for AppId X with minimum v10 (which only X is indexed for, but X is too old).
        var dep = new DependencyRef(Guid.Parse(appIdX), "Shared", "Vendor",
            new Version(10, 0, 0, 0));

        var ex = Assert.Throws<AlRunner.Infrastructure.DependencyVersionMismatchException>(
            () => resolver.Resolve(new[] { dep }));
        // Should report that v5 was found (near-miss) — not silently succeed.
        Assert.Contains("5.0", ex.Message);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string MakeDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes a minimal NAVX .app file (header + ZIP with NavxManifest.xml).</summary>
    private static void WriteApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
    {
        File.WriteAllBytes(Path.Combine(dir, fileName), MakeMinimalApp(appId, name, publisher, version));
    }

    // ── Part N: same-version tie-break — an executable (R2R) package must win ──
    //
    // Root cause: SelectBestVersion only promoted a candidate on `>` (strictly higher
    // version), so two packages of the SAME app at the SAME version were decided by
    // index order — i.e. by which --package-cache / .alpackages dir was scanned first.
    // A workspace's .alpackages typically holds the SYMBOL-ONLY dev package for
    // System Application / Base Application; the R2R runtime package lives in the
    // provisioned package cache. When the symbol-only copy won, every codeunit in that
    // app became unresolvable at runtime and NavCodeunitHandle_CreateTarget silently
    // substituted a NoOpCodeunit for the system range — so the first procedure call on
    // e.g. Codeunit "Environment Information" died with the cryptic
    // "Function ID N was called. The object with ID 0 does not have a member with that ID."
    // Measured on Pageworks 2026-07-27: the install trigger of every bundle aborted, so
    // the whole suite reported 0 tests run.

    /// <summary>
    /// Symbol-only package indexed FIRST, R2R package (same AppId, same version) second.
    /// The resolver must bind to the R2R package — the one that can actually execute.
    /// </summary>
    [Fact]
    public void SameVersion_R2RChosenOverSymbolOnly_WhenSymbolOnlyIndexedFirst()
    {
        var appId = "cccccccc-0000-0000-0000-000000000001";
        var symDir = MakeDir("sym-first");
        var r2rDir = MakeDir("r2r-second");

        WriteApp(symDir, "SysApp_symbols.app", appId, "System Application", "Microsoft",
            "28.2.50931.51111", r2r: false);
        WriteApp(r2rDir, "SysApp_runtime.app", appId, "System Application", "Microsoft",
            "28.2.50931.51111", r2r: true);

        var resolver = new DependencyResolver(new[] { symDir, r2rDir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft",
            new Version(28, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("SysApp_runtime.app", Path.GetFileName(result[0].AppPath));
        Assert.True(AppLoader.IsR2R(result[0].AppPath));
    }

    /// <summary>
    /// Mirror image: R2R indexed first. Still the R2R package — the tie-break must not
    /// merely flip the order preference.
    /// </summary>
    [Fact]
    public void SameVersion_R2RChosenOverSymbolOnly_WhenR2RIndexedFirst()
    {
        var appId = "cccccccc-0000-0000-0000-000000000002";
        var r2rDir = MakeDir("r2r-first");
        var symDir = MakeDir("sym-second");

        WriteApp(r2rDir, "SysApp_runtime.app", appId, "System Application", "Microsoft",
            "28.2.50931.51111", r2r: true);
        WriteApp(symDir, "SysApp_symbols.app", appId, "System Application", "Microsoft",
            "28.2.50931.51111", r2r: false);

        var resolver = new DependencyResolver(new[] { r2rDir, symDir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft",
            new Version(28, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("SysApp_runtime.app", Path.GetFileName(result[0].AppPath));
    }

    /// <summary>
    /// A code-bearing package beats a strictly HIGHER symbol-only one, so long as both clear
    /// the declared minimum.
    ///
    /// This reverses the rule this test previously asserted ("version is the primary key;
    /// a higher symbol-only version still wins"). That reading conflated two things: BC's
    /// minimum-version semantics, which the `&lt; dep.Version` filter in SelectBestVersion
    /// already enforces, and a claim that the HIGHEST acceptable version must win, which BC
    /// does not require. A symbol-only package cannot execute at all — resolution picking it
    /// over an executable peer does not honour version semantics, it silently disables the
    /// app: NavCodeunitHandle_CreateTarget substitutes a NoOpCodeunit and the first call
    /// fails with "The object with ID 0 does not have a member with that ID."
    ///
    /// Measured, not theoretical. The al-language corpus commits
    /// .alpackages/System Application.app at v27.5.46862.48827, symbols-only. On the BC 27.0
    /// and 27.3 matrix legs the provisioned code-bearing app sorts below it, so
    /// `Codeunit "Temp Blob"` lost its body: 17 corpus failures on each leg, identical sets,
    /// across CreateInStream/CreateOutStream and every report dataset built on one. The 27.5
    /// and 28.x legs passed only because their provisioned build happened to outrank 48827.
    /// Both legs go to 1904/1904 with executability ranked first.
    /// </summary>
    [Fact]
    public void LowerCodeBearingVersion_Beats_HigherSymbolOnlyVersion()
    {
        var appId = "cccccccc-0000-0000-0000-000000000003";
        var dir = MakeDir("mixed");

        WriteApp(dir, "Lib_v28_1_r2r.app", appId, "Tests-TestLibraries", "Microsoft",
            "28.1.49838.50794", r2r: true);
        WriteApp(dir, "Lib_v28_2_sym.app", appId, "Tests-TestLibraries", "Microsoft",
            "28.2.50931.51111", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(28, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(28, 1, 49838, 50794), result[0].Manifest.Version);
        Assert.Equal("Lib_v28_1_r2r.app", Path.GetFileName(result[0].AppPath));
    }

    /// <summary>
    /// The negative direction of the rule above, and the one that keeps it honest: ranking
    /// executability first must NOT reach below the declared minimum to find something
    /// executable. A code-bearing package under dep.Version stays excluded, and the
    /// symbol-only package that does clear the minimum is the answer.
    /// </summary>
    [Fact]
    public void CodeBearingBelowMinimum_IsNotChosen_OverSymbolOnlyThatMeetsIt()
    {
        var appId = "cccccccc-0000-0000-0000-000000000009";
        var dir = MakeDir("mixed-below-min");

        WriteApp(dir, "Lib_v27_r2r.app", appId, "Tests-TestLibraries", "Microsoft",
            "27.5.46862.53242", r2r: true);
        WriteApp(dir, "Lib_v28_2_sym.app", appId, "Tests-TestLibraries", "Microsoft",
            "28.2.50931.51111", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(28, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal(new Version(28, 2, 50931, 51111), result[0].Manifest.Version);
        Assert.Equal("Lib_v28_2_sym.app", Path.GetFileName(result[0].AppPath));
    }

    /// <summary>
    /// Negative direction: when the ONLY candidate is symbol-only, resolution must still
    /// succeed with that package (the runner falls back to service-tier DLL dispatch) —
    /// the tie-break must not turn "no R2R available" into an unresolved dependency.
    /// </summary>
    [Fact]
    public void SymbolOnlyAlone_StillResolves_WhenNoR2RCandidateExists()
    {
        var appId = "cccccccc-0000-0000-0000-000000000004";
        var dir = MakeDir("sym-only");

        WriteApp(dir, "SysApp_symbols.app", appId, "System Application", "Microsoft",
            "28.2.50931.51111", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft",
            new Version(28, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("SysApp_symbols.app", Path.GetFileName(result[0].AppPath));
        Assert.False(AppLoader.IsR2R(result[0].AppPath));
    }

    private static void WriteApp(string dir, string fileName,
        string appId, string name, string publisher, string version, bool r2r)
    {
        File.WriteAllBytes(Path.Combine(dir, fileName),
            MakeMinimalApp(appId, name, publisher, version, r2r));
    }

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version)
        => MakeMinimalApp(appId, name, publisher, version, r2r: false);

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version, bool r2r)
        => MakeMinimalApp(appId, name, publisher, version, r2r, alSource: false);

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version,
        bool r2r, bool alSource)
        => MakeMinimalApp(appId, name, publisher, version, r2r, alSource, platform: null);

    /// <summary>
    /// <paramref name="platform"/> becomes the App element's <c>Platform</c> attribute — the
    /// floor the real `al` compiler turns into an implicit Microsoft/System dependency, and
    /// the only dependency Microsoft's test-toolkit packages declare (Library Assert's manifest:
    /// <c>Platform="28.0.0.0"</c>, an empty <c>&lt;Dependencies /&gt;</c>).
    /// </summary>
    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version,
        bool r2r, bool alSource, string? platform)
        => MakeMinimalApp(appId, name, publisher, version, r2r, alSource, platform, application: null);

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version,
        bool r2r, bool alSource, string? platform, string? application)
    {
        var platformAttr = platform == null ? "" : $" Platform=\"{platform}\"";
        var applicationAttr = application == null ? "" : $" Application=\"{application}\"";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"{applicationAttr}{platformAttr}/>
            </Package>
            """;

        // Build ZIP containing NavxManifest.xml.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using (var es = entry.Open())
                es.Write(Encoding.UTF8.GetBytes(xml));
            if (r2r)
            {
                // AppLoader.IsR2R only looks for a publishedartifacts/*.dll entry.
                var dll = zip.CreateEntry("publishedartifacts/" + name + ".dll");
                using var ds = dll.Open();
                ds.Write(new byte[] { 0x4D, 0x5A });
            }
            if (alSource)
            {
                // AppLoader.HasAlSource only looks for a src/*.al entry. This is the shape of
                // Microsoft's real test toolkit: no DLL, but AL the Tier-3 compile can build.
                var al = zip.CreateEntry("src/" + name + ".al");
                using var als = al.Open();
                als.Write(Encoding.UTF8.GetBytes("codeunit 130002 \"" + name + "\" { }"));
            }
        }
        var zipBytes = ms.ToArray();

        // NAVX wrapper: magic "NAVX" + LE uint32 ZIP offset (8) + ZIP bytes.
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    // ── #3719: a resolved package's OWN platform floor joins the closure ──────
    //
    // Library Assert's manifest declares Platform="28.0.0.0" and no <Dependencies>. A consumer
    // whose app.json declares neither `platform` nor `application` (LethAL's sandbox-data
    // fixture; nothing in AL requires the keys) therefore resolved a closure with no System.app
    // in it, Library Assert was source-compiled against that closure, and BC's emitter died on
    // `Table 'Field' is missing` / `namespace 'Reflection' is unknown` — reported as EMIT-ZERO.
    // Adding `"platform"` to the CONSUMER made the same fixture pass, 66 tests. The resolver must
    // follow a resolved package's own Platform/Application floors the way it follows its
    // <Dependencies>, so the dependency compiles against what ITS manifest asks for.

    [Fact]
    public void ResolvedPackageDeclaringPlatform_PullsSystemIntoTheClosure_BeforeIt()
    {
        var dir = MakeDir("PlatformFloor");
        var systemId = "00000000-0000-0000-0000-00000000c0de";
        var assertId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        File.WriteAllBytes(Path.Combine(dir, "System.app"),
            MakeMinimalApp(systemId, "System", "Microsoft", "28.0.54265.0", r2r: false, alSource: false, platform: null));
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(assertId, "Library Assert", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(assertId), "Library Assert", "Microsoft", new Version(28, 0, 0, 0)),
        });

        var names = result.Select(r => r.Manifest.Name).ToList();
        Assert.Equal(new[] { "System", "Library Assert" }, names); // topological: the floor first
    }

    /// <summary>
    /// The floor is Optional, exactly like the implicit roots a consumer's app.json yields: a
    /// cache without System.app resolves the package alone rather than throwing. (Whether the
    /// compile then fails is the loader's business, and it does fail loudly.)
    /// </summary>
    [Fact]
    public void ResolvedPackageDeclaringPlatform_SystemAbsent_ResolvesThePackageAlone()
    {
        var dir = MakeDir("PlatformFloorAbsent");
        var assertId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(assertId, "Library Assert", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true, platform: "28.0.0.0"));

        var result = new DependencyResolver(new[] { dir }).Resolve(new[]
        {
            new DependencyRef(Guid.Parse(assertId), "Library Assert", "Microsoft", new Version(28, 0, 0, 0)),
        });

        Assert.Equal(new[] { "Library Assert" }, result.Select(r => r.Manifest.Name).ToArray());
    }

    /// <summary>
    /// The Application floor is followed too, not only Platform — an implementation handling
    /// `Platform` alone passes every other fact here. Microsoft's test packages really declare
    /// it: Tests-ERM's manifest is <c>Platform="28.0.0.0" Application="28.1.0.0"</c>.
    /// </summary>
    [Fact]
    public void ResolvedPackageDeclaringApplication_PullsApplicationIntoTheClosure()
    {
        var dir = MakeDir("ApplicationFloor");
        var applicationId = "00000000-0000-0000-0000-0000000a9911";
        var ermId = "00000000-0000-0000-0000-0000000e2222";
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Application.app"),
            MakeMinimalApp(applicationId, "Application", "Microsoft", "28.1.49838.54368", r2r: false, alSource: false, platform: null));
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Tests-ERM.app"),
            MakeMinimalApp(ermId, "Tests-ERM", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true,
                platform: null, application: "28.1.0.0"));

        var result = new DependencyResolver(new[] { dir }).Resolve(new[]
        {
            new DependencyRef(Guid.Parse(ermId), "Tests-ERM", "Microsoft", new Version(28, 0, 0, 0)),
        });

        Assert.Equal(new[] { "Application", "Tests-ERM" }, result.Select(r => r.Manifest.Name).ToArray());
    }

    /// <summary>
    /// #3719: a package this run SYNTHESIZED from source must carry its own app.json floors, or
    /// the resolver has nothing to follow and the sibling is source-compiled without the platform
    /// symbols it asked for — the reported bug, on a path the runner manufactures itself.
    /// BuildNavxManifestXml filters <c>&lt;Dependencies&gt;</c> to non-Optional entries, which is
    /// exactly where the implicit floors live, so they have to travel as App attributes.
    /// </summary>
    [Fact]
    public void SynthesizedPackage_CarriesTheAppJsonFloors_SoTheResolverCanFollowThem()
    {
        var src = MakeDir("SynthSource");
        File.WriteAllText(Path.Combine(src, "app.json"), """
        {
          "id": "00000000-0000-0000-0000-0000000b1111",
          "name": "Synth Sibling",
          "publisher": "Contoso",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "28.0.0.0",
          "application": "28.1.0.0",
          "runtime": "13.0"
        }
        """);

        var identity = AlRunner.Infrastructure.InProcessAppPackager.ReadIdentity(Path.Combine(src, "app.json"));
        Assert.NotNull(identity);
        Assert.Equal(new Version(28, 0, 0, 0), identity!.Platform);
        Assert.Equal(new Version(28, 1, 0, 0), identity.Application);

        // Round-trip: package it the way SiblingCompile does, read it back the way
        // DependencyResolver does.
        File.WriteAllText(Path.Combine(src, "Helper.Codeunit.al"), "codeunit 63900 \"Synth Helper\" { }");
        var outDir = MakeDir("SynthOut");
        var appPath = Path.Combine(outDir, "Contoso_Synth_Sibling.app");
        AlRunner.Infrastructure.InProcessAppPackager.EmitAppPackageToFile(src, identity, appPath);

        var manifest = AppLoader.ReadManifest(appPath);
        Assert.NotNull(manifest);
        Assert.Equal(new Version(28, 0, 0, 0), manifest!.Platform);
        Assert.Equal(new Version(28, 1, 0, 0), manifest.Application);
        Assert.Equal(
            new[] { "Application", "System" },
            AppLoader.ImplicitRoots(manifest).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The trap AppLoader.ImplicitRoots' own doc comment names: every Microsoft platform app's
    /// manifest carries these attributes and they reference each other (Application → Base
    /// Application → Application …), so following a PLATFORM app's floors would cycle. They
    /// are not followed; only a non-platform package's are. Base Application declaring a
    /// Platform floor resolves to exactly itself, no System, no cycle exception.
    /// </summary>
    [Fact]
    public void MicrosoftPlatformAppDeclaringPlatform_FloorIsNotFollowed()
    {
        var dir = MakeDir("PlatformFloorGuard");
        var systemId = "00000000-0000-0000-0000-00000000c0de";
        var baseId = "437dbf0e-84ff-417a-965d-ed2bb9650972";
        File.WriteAllBytes(Path.Combine(dir, "System.app"),
            MakeMinimalApp(systemId, "System", "Microsoft", "28.0.54265.0", r2r: false, alSource: false, platform: "28.0.54265.0"));
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Base Application.app"),
            MakeMinimalApp(baseId, "Base Application", "Microsoft", "28.1.49838.54169", r2r: true, alSource: false, platform: "28.0.0.0"));

        var result = new DependencyResolver(new[] { dir }).Resolve(new[]
        {
            new DependencyRef(Guid.Parse(baseId), "Base Application", "Microsoft", new Version(28, 0, 0, 0)),
        });

        Assert.Equal(new[] { "Base Application" }, result.Select(r => r.Manifest.Name).ToArray());
    }

    // -- #3794: a floor that cannot be supplied is named, not skipped in silence --
    //
    // #3793 made Visit follow a resolved package's floors. When the floor cannot be met,
    // Visit reaches `if (dep.Optional || IsMicrosoftPlatformApp(...))` and returns BEFORE
    // the nearMissVersions branch, so both "System.app absent" and "System.app present but
    // below the declared floor" print one line -- `[deps] dependency not found in cache,
    // skipping: Microsoft/System` -- and the run then dies in the dependent's source
    // compile with the generic `EMIT-ZERO - BC Compilation.Emit() returned 0 sources`,
    // naming neither the floor, nor the versions that were found, nor the repair.
    //
    // The skip itself stays: it is what lets the al-language corpus declare System
    // Application >= 27.5 and still run green on the 27.0 and 27.3 legs, where no download
    // can clear the floor (DropUnsatisfiableFloors documents the same tolerance on the
    // provisioning side). What changes is that a floor belonging to a package this run MAY
    // source-compile is reported, on ProvisioningGaps rather than UnservableDependencies:
    // "no loader tier can implement this" is a certain failure and an unmet floor is not,
    // since the dependent may still be served from the compiled-dependency cache, the
    // service-tier DLL index, or an already-loaded assembly. Deciding it where the compile
    // actually happens is #3812.

    /// <summary>
    /// The dependent ships AL source and no R2R payload, so Tier-3 is the only route that can
    /// implement it. Its Platform floor is unmet -- System.app is present at 27.0, below the
    /// declared 28.0 -- and that must be stated, naming the dependent, the floor, and the
    /// version that was actually found.
    /// </summary>
    [Fact]
    public void SourceCompilablePackageFloor_SystemBelowFloor_IsReportedAsAProvisioningGap()
    {
        var dir = MakeDir("FloorBelowMinimum");
        var systemId = "00000000-0000-0000-0000-00000000c0de";
        var assertId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        File.WriteAllBytes(Path.Combine(dir, "System.app"),
            MakeMinimalApp(systemId, "System", "Microsoft", "27.0.38460.0", r2r: false, alSource: false, platform: null));
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(assertId, "Library Assert", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(assertId), "Library Assert", "Microsoft", new Version(28, 0, 0, 0)),
        });

        // Still resolves -- a below-floor platform app is not a hard failure, per the corpus
        // 27.0/27.3 tolerance above.
        Assert.Contains("Library Assert", result.Select(r => r.Manifest.Name));

        var report = Assert.Single(resolver.ProvisioningGaps, d => d.Contains("Library Assert", StringComparison.Ordinal));
        Assert.Contains("Microsoft/System", report, StringComparison.Ordinal);
        Assert.Contains("28.0.0.0", report, StringComparison.Ordinal);   // the declared floor
        Assert.Contains("27.0.38460.0", report, StringComparison.Ordinal); // what was found instead
        Assert.Contains(dir, report, StringComparison.Ordinal);          // where it looked
        Assert.Contains("provision", report, StringComparison.OrdinalIgnoreCase); // how to repair it
    }

    /// <summary>
    /// The absent case, which is #3719's own reproduction: no System.app at all. The package
    /// still resolves alone (that behaviour is pinned by
    /// <see cref="ResolvedPackageDeclaringPlatform_SystemAbsent_ResolvesThePackageAlone"/>),
    /// and the run now says why the compile that follows will fail.
    /// </summary>
    [Fact]
    public void SourceCompilablePackageFloor_SystemAbsent_IsReportedAsAProvisioningGap()
    {
        var dir = MakeDir("FloorAbsentReported");
        var assertId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(assertId, "Library Assert", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(assertId), "Library Assert", "Microsoft", new Version(28, 0, 0, 0)),
        });

        var report = Assert.Single(resolver.ProvisioningGaps, d => d.Contains("Library Assert", StringComparison.Ordinal));
        Assert.Contains("Microsoft/System", report, StringComparison.Ordinal);
        Assert.Contains("28.0.0.0", report, StringComparison.Ordinal);
        Assert.Contains("provision", report, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The negative direction, and the one that keeps this quiet on the legs that need it:
    /// an R2R dependent is served by Tier-2 and never source-compiled, so its unmet floor is
    /// the ordinary tolerated skip and must produce NO report. Without this the 27.0 and
    /// 27.3 corpus legs would gain an unconditional message on every run.
    /// </summary>
    [Fact]
    public void PrecompiledPackageFloor_SystemAbsent_IsNotReported()
    {
        var dir = MakeDir("FloorAbsentPrecompiled");
        var depId = "00000000-0000-0000-0000-0000000c1111";
        File.WriteAllBytes(Path.Combine(dir, "Contoso_Precompiled.app"),
            MakeMinimalApp(depId, "Precompiled", "Contoso", "1.0.0.0", r2r: true, alSource: false, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(depId), "Precompiled", "Contoso", new Version(1, 0, 0, 0)),
        });

        Assert.DoesNotContain(resolver.ProvisioningGaps, d => d.Contains("Microsoft/System", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the floor that IS supplied produces no report either -- so the two facts above
    /// are the floor talking, not every source-bearing package generating a message.
    /// </summary>
    [Fact]
    public void SourceCompilablePackageFloor_SystemAtTheFloor_IsNotReported()
    {
        var dir = MakeDir("FloorMet");
        var systemId = "00000000-0000-0000-0000-00000000c0de";
        var assertId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        File.WriteAllBytes(Path.Combine(dir, "System.app"),
            MakeMinimalApp(systemId, "System", "Microsoft", "28.0.54265.0", r2r: false, alSource: false, platform: null));
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(assertId, "Library Assert", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(assertId), "Library Assert", "Microsoft", new Version(28, 0, 0, 0)),
        });

        Assert.Equal(new[] { "System", "Library Assert" }, result.Select(r => r.Manifest.Name).ToArray());
        Assert.DoesNotContain(resolver.ProvisioningGaps, d => d.Contains("Microsoft/System", StringComparison.Ordinal));
    }

    /// <summary>
    /// The gate is three predicates, and only the AL-source one had a fact of its own: the
    /// R2R negative used a fixture with no AL source either, so a wrong gate consisting only
    /// of `if (!HasAlSource) return` passed everything (#3810 review). This package has BOTH
    /// an R2R payload AND AL source, so it separates them — Tier-2 serves it, and it must
    /// stay silent.
    /// </summary>
    [Fact]
    public void PackageWithBothR2RAndAlSource_FloorAbsent_IsNotReported()
    {
        var dir = MakeDir("FloorR2RPlusSource");
        var depId = "00000000-0000-0000-0000-0000000c2222";
        File.WriteAllBytes(Path.Combine(dir, "Contoso_Both.app"),
            MakeMinimalApp(depId, "Both", "Contoso", "1.0.0.0", r2r: true, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(depId), "Both", "Contoso", new Version(1, 0, 0, 0)),
        });

        Assert.Empty(resolver.ProvisioningGaps);
    }

    /// <summary>
    /// One floor unmet for two source-bearing packages is two reports, one per owner — they
    /// name different packages and a reader needs both. The same (owner, floor) pair twice is
    /// not, and a second Resolve on one resolver must not duplicate what the first said
    /// (#3810 review).
    /// </summary>
    [Fact]
    public void UnsuppliableFloor_IsReportedOncePerOwner_AndNotRepeatedAcrossResolves()
    {
        var dir = MakeDir("FloorDedup");
        var oneId = "00000000-0000-0000-0000-0000000d1111";
        var twoId = "00000000-0000-0000-0000-0000000d2222";
        File.WriteAllBytes(Path.Combine(dir, "Contoso_One.app"),
            MakeMinimalApp(oneId, "One", "Contoso", "1.0.0.0", r2r: false, alSource: true, platform: "28.0.0.0"));
        File.WriteAllBytes(Path.Combine(dir, "Contoso_Two.app"),
            MakeMinimalApp(twoId, "Two", "Contoso", "1.0.0.0", r2r: false, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        var roots = new[]
        {
            new DependencyRef(Guid.Parse(oneId), "One", "Contoso", new Version(1, 0, 0, 0)),
            new DependencyRef(Guid.Parse(twoId), "Two", "Contoso", new Version(1, 0, 0, 0)),
        };
        resolver.Resolve(roots);

        Assert.Equal(2, resolver.ProvisioningGaps.Count);
        Assert.Single(resolver.ProvisioningGaps, g => g.Contains("Contoso/One", StringComparison.Ordinal));
        Assert.Single(resolver.ProvisioningGaps, g => g.Contains("Contoso/Two", StringComparison.Ordinal));

        resolver.Resolve(roots);

        Assert.Equal(2, resolver.ProvisioningGaps.Count);
    }

    /// <summary>
    /// An unmet floor is not an unservable package, and must not be filed as one: #1689's list
    /// means "no loader tier can implement this", which is certain, while this package has AL
    /// source and may well be served from a cache without compiling at all.
    /// </summary>
    [Fact]
    public void UnsuppliableFloor_DoesNotEnterTheUnservableList()
    {
        var dir = MakeDir("FloorNotUnservable");
        var assertId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(assertId, "Library Assert", "Microsoft", "28.1.49838.54169", r2r: false, alSource: true, platform: "28.0.0.0"));

        var resolver = new DependencyResolver(new[] { dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(assertId), "Library Assert", "Microsoft", new Version(28, 0, 0, 0)),
        });

        Assert.Single(resolver.ProvisioningGaps);
        Assert.Empty(resolver.UnservableDependencies);
    }

    // ── #1689: a resolved package that NO loader tier can implement ───────────
    //
    // Reported shape: a symbols-only `Library Assert` in .alpackages satisfies resolution,
    // the bundle compiles green, and every call into it dies with "Function ID N was
    // called. The object with ID 0 does not have a member with that ID" — naming neither
    // the app nor the codeunit.
    //
    // The pre-existing symbols-only diagnostic could not catch it twice over: it only fired
    // when OTHER code-bearing copies existed below the minimum version (here there are no
    // other copies at all), and it was printed only under --verbose.
    //
    // The discriminator is NOT !IsR2R. Microsoft's real toolkit ships no publishedartifacts
    // DLL but DOES ship src/*.al, and the loader's Tier-3 source compile implements it —
    // verified against the real 28.1.49838.53479 artifact, where `Microsoft_Library
    // Assert.app` is 22 KB with IsR2R=false and one src/*.al, and a bundle resolving it
    // scores 2/2 PASS. Gating on !IsR2R alone would fire on every healthy toolkit run.

    /// <summary>
    /// Neither R2R nor AL source, no other copy anywhere: unservable. Must be reported,
    /// naming the app and the winning path.
    /// </summary>
    [Fact]
    public void SymbolsOnlyWithNoAlSource_AndNoOtherCopy_IsReportedAsUnservable()
    {
        var appId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        var dir = MakeDir("symbols-only-alone");
        WriteApp(dir, "Microsoft_Library Assert.app", appId, "Library Assert", "Microsoft",
            "28.1.49838.53479", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[] { new DependencyRef(Guid.Parse(appId), "Library Assert", "Microsoft", new Version(22, 0, 0, 0)) });
        Assert.Single(result);

        var report = Assert.Single(resolver.UnservableDependencies);
        Assert.Contains("Microsoft/Library Assert", report);
        Assert.Contains("NO IMPLEMENTATION", report);
        Assert.Contains(Path.Combine(dir, "Microsoft_Library Assert.app"), report);
        // Names the failure the developer would otherwise meet unexplained.
        Assert.Contains("object with ID 0", report);
    }

    /// <summary>
    /// NEGATIVE — the healthy Microsoft test-toolkit shape: no DLL, but AL source present.
    /// Tier-3 compiles it, so this must stay silent. This is the regression guard that
    /// stops the fix from breaking every working toolkit resolution.
    /// </summary>
    [Fact]
    public void SymbolsOnlyButShipsAlSource_IsNotReported()
    {
        var appId = "dd0be2ea-f733-4d65-bb34-a28f4624fb14";
        var dir = MakeDir("no-dll-but-al");
        File.WriteAllBytes(Path.Combine(dir, "Microsoft_Library Assert.app"),
            MakeMinimalApp(appId, "Library Assert", "Microsoft", "28.1.49838.53479",
                r2r: false, alSource: true));

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[] { new DependencyRef(Guid.Parse(appId), "Library Assert", "Microsoft", new Version(22, 0, 0, 0)) });
        Assert.Single(result);

        Assert.Empty(resolver.UnservableDependencies);
    }

    /// <summary>
    /// NEGATIVE — #2739: a symbols-only package paired with a committed Tier-1 sidecar DLL
    /// under <c>&lt;bundle&gt;/.deps-bin/</c> is fully servable, because DependencyLoader
    /// prefers that DLL over every other tier. Reporting it as "NO IMPLEMENTATION … no other
    /// copy was found" fired on every green CI leg and, on 2026-09-05, produced a wrong
    /// diagnosis of a red PR whose actual cause was several lines further down.
    ///
    /// The 2-byte {0x4D,0x5A} stub below is deliberately NOT a loadable assembly, and this
    /// test is still right to expect silence HERE: the resolver's probe is File.Exists and
    /// nothing more, so "a sidecar is present" is all it can honestly claim. #2750 noted that
    /// this encodes a gap — a sidecar that exists but cannot be loaded — and closed it one
    /// layer down instead: DependencyLoader.LoadOne now reports that as a provisioning gap,
    /// loud at default verbosity and repeated in the run summary. See
    /// CorruptSidecarLoudnessTests. Making the RESOLVER load-test every sidecar would move a
    /// full Assembly.Load into dependency resolution for every bundle, to answer a question
    /// the loader answers anyway a moment later.
    /// </summary>
    [Fact]
    public void SymbolsOnlyButHasPrecompiledSidecarDll_IsNotReported()
    {
        var appId = "c7a1b2d3-4e5f-4a6b-8c9d-0e1f2a3b4c5d";
        var bundleRoot = MakeDir("sidecar-bundle");
        var pkgDir = Directory.CreateDirectory(Path.Combine(bundleRoot, ".alpackages")).FullName;
        WriteApp(pkgDir, "AL_Runner_Fixtures_Sidecar_Dep_1.0.0.0.app", appId,
            "Sidecar Dep", "AL Runner Fixtures", "1.0.0.0", r2r: false);

        // Exactly the name DependencyLoader.LoadOne builds: <Publisher>_<Name>_<Version>.dll.
        var depsBin = Directory.CreateDirectory(Path.Combine(bundleRoot, ".deps-bin")).FullName;
        File.WriteAllBytes(
            Path.Combine(depsBin, "AL_Runner_Fixtures_Sidecar_Dep_1.0.0.0.dll"), new byte[] { 0x4D, 0x5A });

        var resolver = new DependencyResolver(
            new[] { pkgDir }, Array.Empty<string>(), bundleRoot);
        var result = resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(appId), "Sidecar Dep", "AL Runner Fixtures", new Version(1, 0, 0, 0))
        });
        Assert.Single(result);

        Assert.Empty(resolver.UnservableDependencies);
    }

    /// <summary>
    /// POSITIVE control for the test above — the SAME symbols-only package with NO sidecar
    /// DLL beside it must still be reported. Without this, an implementation that simply
    /// stopped reporting unservable dependencies altogether would pass the negative test,
    /// and the real #1689 diagnostic would be silently lost.
    /// </summary>
    [Fact]
    public void SymbolsOnlyWithoutSidecarDll_IsStillReported()
    {
        var appId = "c7a1b2d3-4e5f-4a6b-8c9d-0e1f2a3b4c5e";
        var bundleRoot = MakeDir("no-sidecar-bundle");
        var pkgDir = Directory.CreateDirectory(Path.Combine(bundleRoot, ".alpackages")).FullName;
        WriteApp(pkgDir, "AL_Runner_Fixtures_Sidecar_Dep_1.0.0.0.app", appId,
            "Sidecar Dep", "AL Runner Fixtures", "1.0.0.0", r2r: false);
        // No .deps-bin directory at all.

        var resolver = new DependencyResolver(
            new[] { pkgDir }, Array.Empty<string>(), bundleRoot);
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(appId), "Sidecar Dep", "AL Runner Fixtures", new Version(1, 0, 0, 0))
        });

        var report = Assert.Single(resolver.UnservableDependencies);
        Assert.Contains("AL Runner Fixtures/Sidecar Dep", report);
        Assert.Contains("NO IMPLEMENTATION", report);
    }

    /// <summary>
    /// #2739 — a sidecar DLL for a DIFFERENT version must not satisfy the probe. The loader
    /// builds the file name from the resolved manifest's exact version, so a v2 DLL cannot
    /// serve a v1 resolution, and claiming otherwise would suppress a real gap.
    /// </summary>
    [Fact]
    public void SidecarDllForAnotherVersion_DoesNotSuppressTheReport()
    {
        var appId = "c7a1b2d3-4e5f-4a6b-8c9d-0e1f2a3b4c5f";
        var bundleRoot = MakeDir("wrong-version-sidecar");
        var pkgDir = Directory.CreateDirectory(Path.Combine(bundleRoot, ".alpackages")).FullName;
        WriteApp(pkgDir, "AL_Runner_Fixtures_Sidecar_Dep_1.0.0.0.app", appId,
            "Sidecar Dep", "AL Runner Fixtures", "1.0.0.0", r2r: false);

        var depsBin = Directory.CreateDirectory(Path.Combine(bundleRoot, ".deps-bin")).FullName;
        File.WriteAllBytes(
            Path.Combine(depsBin, "AL_Runner_Fixtures_Sidecar_Dep_2.0.0.0.dll"), new byte[] { 0x4D, 0x5A });

        var resolver = new DependencyResolver(
            new[] { pkgDir }, Array.Empty<string>(), bundleRoot);
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(appId), "Sidecar Dep", "AL Runner Fixtures", new Version(1, 0, 0, 0))
        });

        var report = Assert.Single(resolver.UnservableDependencies);
        Assert.Contains("NO IMPLEMENTATION", report);
    }

    /// <summary>
    /// NEGATIVE — Microsoft platform apps are legitimately symbols-only; their runtime comes
    /// from the service tier. The existing carve-out must survive.
    /// </summary>
    [Fact]
    public void SymbolsOnlyMicrosoftPlatformApp_IsNotReported()
    {
        var appId = "eeeeeeee-0000-0000-0000-000000000001";
        var dir = MakeDir("platform-symbols-only");
        WriteApp(dir, "SysApp.app", appId, "System Application", "Microsoft",
            "28.1.49838.53479", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[] { new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft", new Version(28, 0, 0, 0)) });
        Assert.Single(result);

        Assert.Empty(resolver.UnservableDependencies);
    }

    /// <summary>
    /// NEGATIVE — an executable winner is servable by definition.
    /// </summary>
    [Fact]
    public void ExecutableWinner_IsNotReported()
    {
        var appId = "ffffffff-0000-0000-0000-000000000001";
        var dir = MakeDir("r2r-winner");
        WriteApp(dir, "Lib.app", appId, "SomeLib", "SomeVendor", "28.1.49838.53479", r2r: true);

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[] { new DependencyRef(Guid.Parse(appId), "SomeLib", "SomeVendor", new Version(1, 0, 0, 0)) });
        Assert.Single(result);

        Assert.Empty(resolver.UnservableDependencies);
    }

    /// <summary>
    /// The pre-existing "code-bearing copies exist but are below the minimum" diagnostic
    /// still fires, and stays on the verbose-only Diagnostics channel rather than being
    /// promoted to the always-on one.
    /// </summary>
    [Fact]
    public void CodeBearingCopiesBelowMinimum_StillUseTheVersionDiagnostic()
    {
        var appId = "aaaaaaaa-1111-0000-0000-000000000001";
        var dir = MakeDir("below-min");
        WriteApp(dir, "Lib_v28_symbols.app", appId, "SomeLib", "SomeVendor", "28.0.0.0", r2r: false);
        WriteApp(dir, "Lib_v5_r2r.app",      appId, "SomeLib", "SomeVendor", "5.0.0.0",  r2r: true);

        var resolver = new DependencyResolver(new[] { dir });
        var result = resolver.Resolve(new[] { new DependencyRef(Guid.Parse(appId), "SomeLib", "SomeVendor", new Version(28, 0, 0, 0)) });
        Assert.Single(result);

        Assert.Empty(resolver.UnservableDependencies);
        Assert.Contains(resolver.Diagnostics, d => d.Contains("SYMBOLS-ONLY"));
    }

    // ── Issue #2251 pin-bump fallout: Microsoft PLATFORM apps must prefer the
    //    engine-matching code-bearing build over a declared-minimum-satisfying
    //    symbol-only one ──────────────────────────────────────────────────────
    //
    // Root cause: SelectBestVersion's `< dep.Version` filter is correct for an ordinary
    // ISV dependency, where the declared minimum really is a compatibility floor. It is
    // WRONG for the five Microsoft platform apps (System, System Application, Base
    // Application, Business Foundation, Application, see IsMicrosoftPlatformApp): those
    // ship as ONE package per exact BC build, so an engine running build 27.0 can never
    // have a REAL, executable "System Application 27.5" -- only the al-language corpus's
    // own checked-in .alpackages symbols-only stand-in claims that version. Enforcing the
    // filter for these five names means a bundle whose app.json declares a platform
    // minimum higher than the engine it is running under (routine: the corpus targets
    // 27.5+ APIs but AL Runner's own CI matrix tests down to 27.0) resolves to the
    // symbols-only stand-in and crashes downstream with "The object with ID 0 does not
    // have a member with that ID" the first time ANY of its procedures is invoked --
    // reached for real via Codeunit "Reten. Pol. Allowed Tables" from the System
    // Application Test Library's OnInstallAppPerCompany trigger, unrelated to whatever
    // AL the corpus itself declares.
    //
    // This was masked for a long time by an ACCIDENT: the al-language corpus's checked-in
    // "AL Internals Test Fixture" package carried a stale <Dependency Name="Base
    // Application" MinVersion="27.0.0.0"/> left over from a since-removed app.json
    // dependency. DependencyResolver.Visit's "already resolved, skip" cache is keyed only
    // by the target AppId, so THAT weaker, transitively-visited edge (visited first, since
    // the fixture is the first entry in the corpus's own dependencies array) silently won
    // and let the real 27.0 platform build resolve -- with the root app's own, stricter
    // 27.5.0.0 requirement never actually enforced. Refreshing that fixture package to
    // match its current (dependency-less) app.json removed the accidental weaker edge and
    // exposed the underlying bug: nothing about it was a fix for THIS class of failure.
    //
    // The correct, general fix: for Microsoft platform apps specifically, when the
    // minimum-satisfying winner cannot execute (neither R2R nor AL source) and a BELOW-
    // minimum candidate exists that CAN execute, prefer the executable one. Scoped to
    // IsMicrosoftPlatformApp only -- CodeBearingBelowMinimum_IsNotChosen_OverSymbolOnlyThatMeetsIt
    // above is the control proving ordinary (non-platform) dependencies keep the strict
    // minimum-version semantics unchanged.

    /// <summary>
    /// Positive: "System Application" (a Microsoft platform app) with a below-minimum
    /// R2R candidate and an above-minimum symbol-only candidate must resolve to the
    /// R2R one -- the exact shape that crashed the al-language corpus's BC 27.0/27.3
    /// legs once the fixture's accidental compensating dependency was removed.
    /// </summary>
    [Fact]
    public void PlatformApp_BelowMinimumR2R_Beats_AboveMinimumSymbolOnly()
    {
        var appId = "cccccccc-0000-0000-0000-00000000000a";
        var dir = MakeDir("platform-below-min");

        WriteApp(dir, "SysApp_27_0_r2r.app", appId, "System Application", "Microsoft",
            "27.0.38460.53934", r2r: true);
        WriteApp(dir, "SysApp_27_5_sym.app", appId, "System Application", "Microsoft",
            "27.5.46862.48827", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft",
            new Version(27, 5, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("SysApp_27_0_r2r.app", Path.GetFileName(result[0].AppPath));
        Assert.True(AppLoader.IsR2R(result[0].AppPath));
    }

    /// <summary>
    /// Same shape again with NOTHING satisfying the declared minimum at all (only the
    /// below-minimum R2R candidate exists) -- must still resolve to it rather than
    /// throwing DependencyVersionMismatchException, since a real BC service tier at
    /// this exact engine build has no other answer for "System Application" either.
    /// </summary>
    [Fact]
    public void PlatformApp_BelowMinimumR2R_ResolvesAlone_WhenNoCandidateMeetsMinimum()
    {
        var appId = "cccccccc-0000-0000-0000-00000000000b";
        var dir = MakeDir("platform-below-min-alone");

        WriteApp(dir, "SysApp_27_0_r2r.app", appId, "System Application", "Microsoft",
            "27.0.38460.53934", r2r: true);

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft",
            new Version(27, 5, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("SysApp_27_0_r2r.app", Path.GetFileName(result[0].AppPath));
    }

    /// <summary>
    /// Negative control: the SAME below-minimum-R2R / above-minimum-symbol-only shape,
    /// but for an ORDINARY (non-platform) Microsoft-published dependency. This must
    /// still enforce the declared minimum strictly and pick the symbol-only package --
    /// the fallback introduced above is scoped to IsMicrosoftPlatformApp and must not
    /// leak into general dependency resolution.
    /// </summary>
    [Fact]
    public void NonPlatformApp_BelowMinimumR2R_DoesNotBeat_AboveMinimumSymbolOnly()
    {
        var appId = "cccccccc-0000-0000-0000-00000000000c";
        var dir = MakeDir("non-platform-below-min");

        WriteApp(dir, "Lib_v27_r2r.app", appId, "Tests-TestLibraries", "Microsoft",
            "27.0.0.0", r2r: true);
        WriteApp(dir, "Lib_v28_sym.app", appId, "Tests-TestLibraries", "Microsoft",
            "28.0.0.0", r2r: false);

        var resolver = new DependencyResolver(new[] { dir });
        var dep = new DependencyRef(Guid.Parse(appId), "Tests-TestLibraries", "Microsoft",
            new Version(28, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("Lib_v28_sym.app", Path.GetFileName(result[0].AppPath));
    }

    // ── Part N+1: a source-built package supersedes a packaged copy of the same app ──
    //
    // Root cause (#2688): when a bundle passed on the command line provides an app that a
    // package cache ALSO ships compiled, SiblingCompile correctly notices the packaged copy
    // is stale and rebuilds from source into a workspace dir, then prepends that dir to the
    // caches — its own comment says this makes the source build "win over a stale cached
    // .app". It does not. SelectBestVersion ranks on executability then version and ignores
    // directory order entirely, so the packaged copy (a real BC artifact carries a full
    // four-part build number, e.g. 28.4.53241.53989) outranks the source build's app.json
    // version (28.4.0.0) every time. Both copies then load under one app id and the run dies
    // on AppIdCollisionException.
    //
    // Measured against microsoft/BCApps releases/28.4: `System Application Test Library`
    // declares GetLastContextInfoRequestUri in source, the shipped 28.4.53241.53989 .app
    // predates it, and SharePointClientTest.Codeunit drops out with three AL0132 errors —
    // a branch head is never the same commit as a cut artifact, so this is the normal case,
    // not an edge case.

    /// <summary>
    /// Same AppId in two dirs: a source-built package at the app.json version (28.4.0.0)
    /// and a packaged copy at a higher artifact build (28.4.53241.53989). With the source
    /// dir declared as source-superseding, the resolver must bind to the SOURCE build even
    /// though it loses on version.
    /// </summary>
    [Fact]
    public void SourceBuiltPackage_Wins_OverHigherVersionedPackagedCopy()
    {
        var appId = "dddddddd-0000-0000-0000-000000000001";
        var artifactDir = MakeDir("artifact-test-apps");
        var sourceDir = MakeDir("workspace-deps");

        WriteApp(artifactDir, "Microsoft_System Application Test Library.app", appId,
            "System Application Test Library", "Microsoft", "28.4.53241.53989");
        WriteApp(sourceDir, "Microsoft_System_Application_Test_Library_28_4_0_0.app", appId,
            "System Application Test Library", "Microsoft", "28.4.0.0");

        var resolver = new DependencyResolver(
            new[] { artifactDir, sourceDir },
            sourceSupersedingDirs: new[] { sourceDir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application Test Library",
            "Microsoft", new Version(28, 4, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("28.4.0.0", result[0].Manifest.Version.ToString());
        Assert.Equal(sourceDir, Path.GetDirectoryName(result[0].AppPath));
    }

    /// <summary>
    /// Negative: declaring a source-superseding dir must not change resolution for an app
    /// that dir does NOT provide. Without this, the fix could degrade into "the first dir
    /// always wins", silently undoing the highest-satisfying-version contract every other
    /// test in this file pins.
    /// </summary>
    [Fact]
    public void SourceSupersedingDir_LeavesOtherAppsOnHighestVersion()
    {
        var otherId = "dddddddd-0000-0000-0000-000000000002";
        var providedId = "dddddddd-0000-0000-0000-000000000003";
        var cacheDir = MakeDir("cache-untouched");
        var sourceDir = MakeDir("workspace-untouched");

        // The source dir provides ONE app, and it is not the one being resolved.
        WriteApp(sourceDir, "Provided_1_0_0_0.app", providedId, "Provided", "Microsoft", "1.0.0.0");
        WriteApp(cacheDir, "Other_v17.app", otherId, "Other", "Microsoft", "17.0.0.0");
        WriteApp(cacheDir, "Other_v28.app", otherId, "Other", "Microsoft", "28.4.53241.53989");

        var resolver = new DependencyResolver(
            new[] { sourceDir, cacheDir },
            sourceSupersedingDirs: new[] { sourceDir });
        var dep = new DependencyRef(Guid.Parse(otherId), "Other", "Microsoft",
            new Version(17, 0, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("28.4.53241.53989", result[0].Manifest.Version.ToString());
    }

    /// <summary>
    /// Negative: a Microsoft PLATFORM app is deliberately exempt. Those ship one package per
    /// exact BC build and their real runtime lives in the extracted service-tier DLLs, so the
    /// engine-matching R2R artifact must keep winning — that is issue #2251, whose corpus
    /// incident PlatformApp_BelowMinimumR2R_Beats_AboveMinimumSymbolOnly pins. Superseding
    /// those from source would hand every codeunit in them to a package that cannot execute.
    /// </summary>
    [Fact]
    public void SourceBuiltPlatformApp_DoesNotSupersede_TheEngineMatchingR2RArtifact()
    {
        var appId = "dddddddd-0000-0000-0000-000000000004";
        var artifactDir = MakeDir("platform-artifact");
        var sourceDir = MakeDir("platform-source");

        WriteApp(artifactDir, "Microsoft_System Application.app", appId,
            "System Application", "Microsoft", "28.4.53241.53989", r2r: true);
        WriteApp(sourceDir, "Microsoft_System_Application_28_4_0_0.app", appId,
            "System Application", "Microsoft", "28.4.0.0", r2r: false);

        var resolver = new DependencyResolver(
            new[] { sourceDir, artifactDir },
            sourceSupersedingDirs: new[] { sourceDir });
        var dep = new DependencyRef(Guid.Parse(appId), "System Application", "Microsoft",
            new Version(28, 4, 0, 0));

        var result = resolver.Resolve(new[] { dep });

        Assert.Single(result);
        Assert.Equal("28.4.53241.53989", result[0].Manifest.Version.ToString());
    }
}
