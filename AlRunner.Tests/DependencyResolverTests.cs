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
        _root = Path.Combine(Path.GetTempPath(), "al-runner-resolver-tests", Guid.NewGuid().ToString("N"));
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
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
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
}
