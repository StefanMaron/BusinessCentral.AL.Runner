// ProvisioningCheckTests — the engine-artifact completeness gate and its loud, detailed
// "how to fix" report (the runner's "no silent download" policy in action).
// Also covers the platform-app R2R check (symbol-only vs R2R .app detection).

using System.IO.Compression;
using System.Text;
using Xunit;
using AlRunner;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class ProvisioningCheckTests : IDisposable
{
    private readonly string _dir;

    public ProvisioningCheckTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "al-runner-prov", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    private void WriteCompleteClosure()
    {
        foreach (var f in new[]
        {
            "Microsoft.Dynamics.Nav.Ncl.dll",
            "Microsoft.Dynamics.Nav.Types.dll",
            "Microsoft.Dynamics.Nav.Common.dll",
            "Microsoft.Dynamics.Nav.Language.dll",
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
            "Microsoft.Identity.ServiceEssentials.Core.dll",
        }) Touch(f);
    }

    [Fact]
    public void Check_CompleteClosure_IsOk()
    {
        WriteCompleteClosure();
        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir);
        Assert.True(report.Ok);
        Assert.Empty(report.MissingFiles);
    }

    [Fact]
    public void Check_MissingEngineDll_IsReportedByName()
    {
        WriteCompleteClosure();
        File.Delete(Path.Combine(_dir, "Microsoft.Dynamics.Nav.Ncl.dll"));

        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir);
        Assert.False(report.Ok);
        Assert.Contains("Microsoft.Dynamics.Nav.Ncl.dll", report.MissingFiles);
        Assert.DoesNotContain("Microsoft.Dynamics.Nav.Types.dll", report.MissingFiles);
    }

    [Fact]
    public void Check_MissingClosureSentinel_IsReported()
    {
        WriteCompleteClosure();
        File.Delete(Path.Combine(_dir, "Microsoft.Identity.ServiceEssentials.Core.dll"));

        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir);
        Assert.False(report.Ok);
        Assert.Contains("Microsoft.Identity.ServiceEssentials.Core.dll", report.MissingFiles);
    }

    [Fact]
    public void Check_MissingDir_ReportsEverythingMissing()
    {
        var gone = Path.Combine(_dir, "does-not-exist");
        var report = ProvisioningCheck.Check("28.2.50931.52786", gone);
        Assert.False(report.Ok);
        // Names both core engine and the closure sentinel so the message is complete.
        Assert.Contains("Microsoft.Dynamics.Nav.Ncl.dll", report.MissingFiles);
        Assert.Contains("Microsoft.Identity.ServiceEssentials.Core.dll", report.MissingFiles);
    }

    [Fact]
    public void DetailedMessage_NamesPaths_ManualCommand_AndOneCommandFix()
    {
        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir); // empty dir → all missing
        var msg = report.ToDetailedMessage("/some/project");

        // Every missing item's FULL path is named (human/agent can act).
        Assert.Contains(Path.Combine(_dir, "Microsoft.Dynamics.Nav.Ncl.dll"), msg);
        // The exact manual download command, with version and target dir.
        Assert.Contains("service-tier 28.2.50931.52786", msg);
        Assert.Contains(_dir, msg);
        // The one-command auto-resolve, targeting the project.
        Assert.Contains("al-runner provision", msg);
        Assert.Contains("/some/project", msg);
        Assert.Contains("--auto-provision", msg);
        // And it is explicit that the runner will NOT silently download.
        Assert.Contains("will not auto-download", msg);
    }

    // ── Platform-app R2R check ────────────────────────────────────────────────

    /// <summary>Helper: write a minimal symbol-only (not R2R) NAVX .app to a directory.</summary>
    private static void WriteSymbolOnlyApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
    {
        File.WriteAllBytes(Path.Combine(dir, fileName), MakeMinimalNavxApp(appId, name, publisher, version));
    }

    /// <summary>Helper: write a minimal R2R NAVX .app (has publishedartifacts/*.dll).</summary>
    private static void WriteR2RApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
    {
        File.WriteAllBytes(Path.Combine(dir, fileName), MakeR2RNavxApp(appId, name, publisher, version));
    }

    /// <summary>Builds a NAVX .app with no publishedartifacts (symbol-only).</summary>
    private static byte[] MakeMinimalNavxApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        return WrapNavx(xml);
    }

    /// <summary>Builds a NAVX .app with a publishedartifacts/*.dll entry (R2R-like).</summary>
    private static byte[] MakeR2RNavxApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        return WrapNavx(xml, includePublishedArtifact: true);
    }

    private static byte[] WrapNavx(string manifestXml, bool includePublishedArtifact = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using (var es = entry.Open())
                es.Write(Encoding.UTF8.GetBytes(manifestXml));

            if (includePublishedArtifact)
            {
                var dll = zip.CreateEntry("publishedartifacts/app.dll");
                using var ds = dll.Open();
                ds.Write(new byte[] { 0x4D, 0x5A }); // fake PE header
            }
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    [Fact]
    public void CheckPlatformApps_SymbolOnlySystemApp_ReportsIssue()
    {
        var dir = Path.Combine(_dir, "pkg");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_system application_28.2.0.0.app",
            "00000000-0000-0000-0000-000000000001", "System Application", "Microsoft", "28.2.0.0");

        var report = ProvisioningCheck.CheckPlatformApps("28.2.0.0", new[] { dir });

        Assert.False(report.Ok);
        Assert.Single(report.Issues);
        Assert.Equal("System Application", report.Issues[0].Name);
        Assert.Contains("28.2.0.0", report.Issues[0].AppVersion);
    }

    [Fact]
    public void CheckPlatformApps_SymbolOnlySystemApp_MessageNamesAppAndFix()
    {
        var dir = Path.Combine(_dir, "pkg2");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_system application_28.2.0.0.app",
            "00000000-0000-0000-0000-000000000002", "System Application", "Microsoft", "28.2.0.0");

        var report = ProvisioningCheck.CheckPlatformApps("28.2.0.0", new[] { dir });
        var msg = report.ToDetailedMessage();

        Assert.Contains("System Application", msg);
        Assert.Contains("platform-apps", msg);
        Assert.Contains("al-runner provision", msg);
        Assert.Contains("symbol-only", msg);
    }

    [Fact]
    public void CheckPlatformApps_R2RSystemApp_IsOk()
    {
        var dir = Path.Combine(_dir, "pkg3");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "microsoft_system application_28.2.0.0.app",
            "00000000-0000-0000-0000-000000000003", "System Application", "Microsoft", "28.2.0.0");

        var report = ProvisioningCheck.CheckPlatformApps("28.2.0.0", new[] { dir });

        Assert.True(report.Ok);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void CheckPlatformApps_NoPlatformAppsInCache_IsOk()
    {
        // Empty cache — platform apps absent is fine (served by service-tier DLLs).
        var report = ProvisioningCheck.CheckPlatformApps("28.2.0.0", new[] { _dir });
        Assert.True(report.Ok);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void CheckPlatformApps_BothR2RAndSymbolOnly_IsOk()
    {
        // If there's ALSO an R2R version, no issue (loader picks R2R via Tier 2).
        var dir = Path.Combine(_dir, "pkg4");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_system application_28.1.0.0.app",
            "00000000-0000-0000-0000-000000000004", "System Application", "Microsoft", "28.1.0.0");
        WriteR2RApp(dir, "microsoft_system application_28.2.0.0.app",
            "00000000-0000-0000-0000-000000000004", "System Application", "Microsoft", "28.2.0.0");

        var report = ProvisioningCheck.CheckPlatformApps("28.2.0.0", new[] { dir });
        Assert.True(report.Ok);
    }

    [Fact]
    public void BuildPlatformAppMissingR2RMessage_ContainsNameAndFix()
    {
        var msg = ProvisioningCheck.BuildPlatformAppMissingR2RMessage(
            "Microsoft", "System Application", "28.2.0.0",
            "/pkg/microsoft_system application_28.2.0.0.app", "28.2.50931.52786");

        Assert.Contains("System Application", msg);
        Assert.Contains("28.2.50931.52786", msg);
        Assert.Contains("platform-apps", msg);
        Assert.Contains("al-runner provision", msg);
        Assert.Contains("provision-gap", msg);
        Assert.Contains("symbol/dev package", msg);
    }

    [Fact]
    public void IsKnownPlatformRuntimeApp_KnownNames_ReturnsTrue()
    {
        Assert.True(ProvisioningCheck.IsKnownPlatformRuntimeApp("System Application"));
        Assert.True(ProvisioningCheck.IsKnownPlatformRuntimeApp("Base Application"));
        Assert.True(ProvisioningCheck.IsKnownPlatformRuntimeApp("Business Foundation"));
        // Case-insensitive
        Assert.True(ProvisioningCheck.IsKnownPlatformRuntimeApp("system application"));
        Assert.True(ProvisioningCheck.IsKnownPlatformRuntimeApp("BASE APPLICATION"));
    }

    [Fact]
    public void IsKnownPlatformRuntimeApp_UnknownName_ReturnsFalse()
    {
        Assert.False(ProvisioningCheck.IsKnownPlatformRuntimeApp("Tests-TestLibraries"));
        Assert.False(ProvisioningCheck.IsKnownPlatformRuntimeApp("Business Foundation Test Libraries"));
        Assert.False(ProvisioningCheck.IsKnownPlatformRuntimeApp("My Custom App"));
        // "System" (platform) is NOT a known platform runtime app — it's served by Ncl
        Assert.False(ProvisioningCheck.IsKnownPlatformRuntimeApp("System"));
    }

    // ── DeriveProvisionMajorMinor ─────────────────────────────────────────────
    // The missing/symbol-only platform apps carry their OWN real version (e.g. 28.2.x.y)
    // in PlatformAppsReport.Issues, which can differ from the engine's SelectedVersion
    // (e.g. 28.1.x.y — the engine is version-agnostic w.r.t. the R2R apps it dispatches
    // to). Auto-provision must download the apps' minor, not truncate the engine's.

    [Fact]
    public void DeriveProvisionMajorMinor_UsesFirstIssueAppVersion_NotFallback()
    {
        var report = new ProvisioningCheck.PlatformAppsReport(
            "28.1.49838.50794",
            new[] { ("System Application", "28.2.50931.51111", "/pkg/sysapp.app") },
            new[] { "/pkg" });

        var mm = ProvisioningCheck.DeriveProvisionMajorMinor(report, "28.1.49838.50794");

        Assert.Equal("28.2", mm);
    }

    [Fact]
    public void DeriveProvisionMajorMinor_NoIssues_FallsBackToFallbackVersion()
    {
        var report = new ProvisioningCheck.PlatformAppsReport(
            "28.1.49838.50794",
            Array.Empty<(string, string, string)>(),
            new[] { "/pkg" });

        var mm = ProvisioningCheck.DeriveProvisionMajorMinor(report, "28.1.49838.50794");

        Assert.Equal("28.1", mm);
    }

    [Fact]
    public void DeriveProvisionMajorMinor_ShortFallback_ReturnsAsIs()
    {
        var report = new ProvisioningCheck.PlatformAppsReport(
            "28.1", Array.Empty<(string, string, string)>(), new[] { "/pkg" });

        var mm = ProvisioningCheck.DeriveProvisionMajorMinor(report, "28.1");

        Assert.Equal("28.1", mm);
    }

    [Fact]
    public void DeriveProvisionMajorMinor_SingleTokenVersion_ReturnedAsIs()
    {
        var report = new ProvisioningCheck.PlatformAppsReport(
            "28", Array.Empty<(string, string, string)>(), new[] { "/pkg" });

        var mm = ProvisioningCheck.DeriveProvisionMajorMinor(report, "28");

        Assert.Equal("28", mm);
    }

    // ── TestToolkitPresent ────────────────────────────────────────────────────

    [Fact]
    public void TestToolkitPresent_EmptyDir_ReturnsFalse()
    {
        Assert.False(ProvisioningCheck.TestToolkitPresent(new[] { _dir }));
    }

    [Fact]
    public void TestToolkitPresent_NonexistentDir_ReturnsFalse()
    {
        var gone = Path.Combine(_dir, "does-not-exist");
        Assert.False(ProvisioningCheck.TestToolkitPresent(new[] { gone }));
    }

    [Fact]
    public void TestToolkitPresent_OnlyPlatformApp_ReturnsFalse()
    {
        var dir = Path.Combine(_dir, "pkg-platform-only");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "microsoft_system application_28.2.0.0.app",
            "00000000-0000-0000-0000-000000000010", "System Application", "Microsoft", "28.2.0.0");

        Assert.False(ProvisioningCheck.TestToolkitPresent(new[] { dir }));
    }

    [Fact]
    public void TestToolkitPresent_OnlyNonMicrosoftApp_ReturnsFalse()
    {
        var dir = Path.Combine(_dir, "pkg-isv-only");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "isv_business foundation test libraries_1.0.0.0.app",
            "00000000-0000-0000-0000-000000000011", "Business Foundation Test Libraries", "Contoso ISV", "1.0.0.0");

        Assert.False(ProvisioningCheck.TestToolkitPresent(new[] { dir }));
    }

    [Fact]
    public void TestToolkitPresent_BusinessFoundationTestLibraries_ReturnsTrue()
    {
        var dir = Path.Combine(_dir, "pkg-bftl");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_business foundation test libraries_28.2.0.0.app",
            "bee8cf2f-494a-42f4-aabd-650e87934d39", "Business Foundation Test Libraries", "Microsoft", "28.2.0.0");

        Assert.True(ProvisioningCheck.TestToolkitPresent(new[] { dir }));
    }

    [Fact]
    public void TestToolkitPresent_OnlyApplicationTestLibrary_ReturnsFalse()
    {
        // Regression guard for the real clean-cache case: a project's own .alpackages
        // vendors "Application Test Library" but NOT "Business Foundation Test Libraries".
        // The toolkit is NOT fully provisioned, so this must be false (download must fire).
        // A looser OR-match on Application Test Library reported true here and skipped the
        // test-apps download, then the test bundle failed to compile on the missing BFTL.
        var dir = Path.Combine(_dir, "pkg-atl");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_application test library_28.2.0.0.app",
            "00000000-0000-0000-0000-000000000012", "Application Test Library", "Microsoft", "28.2.0.0");

        Assert.False(ProvisioningCheck.TestToolkitPresent(new[] { dir }));
    }

    // ── DerivePresentPlatformMajorMinor ───────────────────────────────────────

    [Fact]
    public void DerivePresentPlatformMajorMinor_NoAppsPresent_FallsBackToFallbackVersion()
    {
        var mm = ProvisioningCheck.DerivePresentPlatformMajorMinor(new[] { _dir }, "28.1.49838.50794");
        Assert.Equal("28.1", mm);
    }

    [Fact]
    public void DerivePresentPlatformMajorMinor_NonexistentDir_FallsBackToFallbackVersion()
    {
        var gone = Path.Combine(_dir, "does-not-exist");
        var mm = ProvisioningCheck.DerivePresentPlatformMajorMinor(new[] { gone }, "28.1.49838.50794");
        Assert.Equal("28.1", mm);
    }

    [Fact]
    public void DerivePresentPlatformMajorMinor_ShortFallback_ReturnsAsIs()
    {
        var mm = ProvisioningCheck.DerivePresentPlatformMajorMinor(new[] { _dir }, "28.1");
        Assert.Equal("28.1", mm);
    }

    [Fact]
    public void DerivePresentPlatformMajorMinor_BaseApplicationPresent_UsesItsMajorMinor()
    {
        var dir = Path.Combine(_dir, "pkg-baseapp");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "microsoft_base application_28.3.0.0.app",
            "00000000-0000-0000-0000-000000000013", "Base Application", "Microsoft", "28.3.0.0");

        // Fallback deliberately a different minor — the present app's version must win.
        var mm = ProvisioningCheck.DerivePresentPlatformMajorMinor(new[] { dir }, "28.1.49838.50794");
        Assert.Equal("28.3", mm);
    }

    [Fact]
    public void DerivePresentPlatformMajorMinor_SystemApplicationPresent_UsesItsMajorMinor()
    {
        var dir = Path.Combine(_dir, "pkg-sysapp");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_system application_28.4.0.0.app",
            "00000000-0000-0000-0000-000000000014", "System Application", "Microsoft", "28.4.0.0");

        var mm = ProvisioningCheck.DerivePresentPlatformMajorMinor(new[] { dir }, "28.1.49838.50794");
        Assert.Equal("28.4", mm);
    }

    // ── Issue #1653: --auto-provision download destination ──────────────────
    // --auto-provision was writing platform R2R apps + the MS test toolkit into whichever
    // --package-cache dir the caller passed first (the project's own .alpackages), instead
    // of the runner-owned artifact cache the standalone `provision` command already uses.
    // These two helpers are the single source of truth for that destination — they must
    // resolve under the runner's artifact root, NEVER under a caller-supplied package-cache
    // path, regardless of what package-cache dirs happen to be in scope.

    [Fact]
    public void PlatformAppsDirFor_IsUnderArtifactsRoot_NotAProjectPackageCacheDir()
    {
        var artifactsRoot = Path.Combine(_dir, "artifacts");
        var projectPackageCache = Path.Combine(_dir, "app", ".alpackages"); // what a caller's --package-cache[0] would be

        var dir = ProvisioningCheck.PlatformAppsDirFor(artifactsRoot, "28.1.49838.50794");

        Assert.Equal(Path.Combine(artifactsRoot, "28.1.49838.50794", "platform-apps"), dir);
        Assert.NotEqual(projectPackageCache, dir);
        Assert.StartsWith(artifactsRoot, dir);
    }

    [Fact]
    public void TestAppsDirFor_IsUnderArtifactsRoot_NotAProjectPackageCacheDir()
    {
        var artifactsRoot = Path.Combine(_dir, "artifacts");
        var projectPackageCache = Path.Combine(_dir, "app", ".alpackages");

        var dir = ProvisioningCheck.TestAppsDirFor(artifactsRoot, "28.1.49838.50794");

        Assert.Equal(Path.Combine(artifactsRoot, "28.1.49838.50794", "test-apps"), dir);
        Assert.NotEqual(projectPackageCache, dir);
        Assert.StartsWith(artifactsRoot, dir);
    }

    [Fact]
    public void PlatformAppsDirFor_And_TestAppsDirFor_AreDistinctSiblingDirs()
    {
        var artifactsRoot = Path.Combine(_dir, "artifacts");

        var platform = ProvisioningCheck.PlatformAppsDirFor(artifactsRoot, "28.1.49838.50794");
        var testApps = ProvisioningCheck.TestAppsDirFor(artifactsRoot, "28.1.49838.50794");

        Assert.NotEqual(platform, testApps);
        Assert.Equal(Path.GetDirectoryName(platform), Path.GetDirectoryName(testApps));
    }

    // ── CollectBundleAlpackagesDirs (issue #1678) ─────────────────────────────
    // The startup gate that decides whether --auto-provision fires (or the run fails
    // loud without it) used to scan ONLY the home-rooted default package caches, never
    // the target bundles' own `.alpackages` — exactly where a standard AL project's
    // symbol download lives. This helper is the fix's single source of truth for the
    // bundle-rooted half of that scan; these tests pin its exact contract.

    [Fact]
    public void CollectBundleAlpackagesDirs_FindsNestedAlpackagesDir()
    {
        var bundle = Path.Combine(_dir, "bundle1");
        var pkgDir = Path.Combine(bundle, ".alpackages");
        Directory.CreateDirectory(pkgDir);

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        Assert.Single(found);
        Assert.Equal(pkgDir, found[0]);
    }

    [Fact]
    public void CollectBundleAlpackagesDirs_ParentOfManySuites_FindsEveryNestedAlpackagesDir()
    {
        var bundle = Path.Combine(_dir, "parent");
        var pkg1 = Path.Combine(bundle, "suite1", ".alpackages");
        var pkg2 = Path.Combine(bundle, "suite2", ".alpackages");
        Directory.CreateDirectory(pkg1);
        Directory.CreateDirectory(pkg2);

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        Assert.Equal(2, found.Count);
        Assert.Contains(pkg1, found);
        Assert.Contains(pkg2, found);
    }

    [Fact]
    public void CollectBundleAlpackagesDirs_NoAlpackagesAnywhere_ReturnsEmpty()
    {
        var bundle = Path.Combine(_dir, "bundle-no-pkgs");
        Directory.CreateDirectory(bundle);

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });

        Assert.Empty(found);
    }

    [Fact]
    public void CollectBundleAlpackagesDirs_NonexistentBundlePath_SkippedNotThrown()
    {
        var gone = Path.Combine(_dir, "does-not-exist");

        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { gone });

        Assert.Empty(found);
    }

    [Fact]
    public void CollectBundleAlpackagesDirs_DuplicateAcrossBundles_DeduplicatedOnce()
    {
        var bundle = Path.Combine(_dir, "bundle-dup");
        var pkgDir = Path.Combine(bundle, ".alpackages");
        Directory.CreateDirectory(pkgDir);

        // The SAME bundle passed twice (e.g. a caller-supplied bundle list with an
        // accidental duplicate) must not duplicate the result.
        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle, bundle });

        Assert.Single(found);
    }

    [Fact]
    public void CollectBundleAlpackagesDirs_EmptyBundleList_ReturnsEmpty()
    {
        var found = ProvisioningCheck.CollectBundleAlpackagesDirs(Array.Empty<string>());

        Assert.Empty(found);
    }

    // ── End-to-end composition (issue #1678) ──────────────────────────────────
    // Reproduces the exact defect at the unit level: a standard AL project's bundle
    // carries a symbol-only Microsoft platform app in its OWN .alpackages (never in any
    // home-rooted default cache). Before the fix, feeding CheckPlatformApps only the
    // default caches reported "Ok" vacuously for this shape; the fix folds the bundle's
    // own .alpackages into the scanned set via CollectBundleAlpackagesDirs, so the gate
    // now sees the same symbol-only package the real dependency loader trips over deep in
    // dispatch — and can act on it (fail loud, or --auto-provision) BEFORE that happens.
    [Fact]
    public void CollectBundleAlpackagesDirs_FeedsIntoCheckPlatformApps_DetectsBundleOnlyGap()
    {
        var bundle = Path.Combine(_dir, "project");
        var pkgDir = Path.Combine(bundle, ".alpackages");
        Directory.CreateDirectory(pkgDir);
        WriteSymbolOnlyApp(pkgDir, "microsoft_system application_28.1.0.0.app",
            "00000000-0000-0000-0000-000000001678", "System Application", "Microsoft", "28.1.0.0");

        // Simulates the OLD, buggy call site: only the (empty) default caches, no bundle
        // .alpackages folded in. Must be vacuously Ok — this IS the bug being fixed.
        var withoutBundleDirs = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", Array.Empty<string>());
        Assert.True(withoutBundleDirs.Ok);

        // The fix: fold CollectBundleAlpackagesDirs(bundles) into the scanned set.
        var bundleAlpackagesDirs = ProvisioningCheck.CollectBundleAlpackagesDirs(new[] { bundle });
        var withBundleDirs = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", bundleAlpackagesDirs);

        Assert.False(withBundleDirs.Ok);
        Assert.Single(withBundleDirs.Issues);
        Assert.Equal("System Application", withBundleDirs.Issues[0].Name);
    }

    // ── Issue #1996: manifest-driven need detection ───────────────────────────
    // The gate above only ever flags an app that is PRESENT as symbol-only. An empty
    // cache (or one that simply doesn't vendor the app yet) reports "Ok" vacuously —
    // absence is not evidence of completeness. These tests drive the manifest (the
    // independent source of truth for what a bundle actually needs) instead of what
    // happens to already be on disk.

    [Fact]
    public void DetermineManifestNeeds_ApplicationTestLibraryDependency_NeedsBothPlatformAndTest()
    {
        // Application Test Library ships in the w1 PLATFORM-apps set (see
        // ArtifactDownloader.PlatformApps' wantedPrefixes), NOT the test-apps set — this is
        // the exact app the issue's repro fails to resolve. BUT its own manifest
        // transitively depends on the MS test toolkit (Any, from there Library Assert/
        // Business Foundation Test Libraries) — confirmed via a live BC 28.1 download while
        // fixing this issue — so needing it must ALSO trigger the test-apps set.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 0, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.True(needs.NeedsPlatformApps);
        Assert.True(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_ImplicitApplicationAndSystemRootsAlone_NeitherFlagSet()
    {
        // Mirrors ReadDependencies' synthesis of implicit `application`/`platform` roots
        // (Guid.Empty, "Application"/"System", "Microsoft", Optional: true) — present on
        // essentially every AL Runner bundle. These alone must NOT set NeedsPlatformApps:
        // the apps they represent (System/Base Application, Business Foundation) have a
        // service-tier DLL dispatch fallback the runner already uses when absent, so
        // requiring literal presence here would regress nearly the whole corpus into a
        // spurious "needs download".
        var roots = new[]
        {
            new DependencyRef(Guid.Empty, "Application", "Microsoft", new Version(28, 1, 0, 0), Optional: true),
            new DependencyRef(Guid.Empty, "System", "Microsoft", new Version(28, 1, 0, 0), Optional: true),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.False(needs.NeedsPlatformApps);
        Assert.False(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_LibraryAssertDependency_NeedsTestNotPlatform()
    {
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Library Assert", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.True(needs.NeedsTestApps);
        Assert.False(needs.NeedsPlatformApps);
    }

    [Fact]
    public void DetermineManifestNeeds_NonMicrosoftPublisher_Ignored()
    {
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Contoso ISV", new Version(1, 0, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.False(needs.NeedsPlatformApps);
        Assert.False(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_UnknownMicrosoftExtension_TriggersNeither()
    {
        // AC #7: a Microsoft-published app outside the known test-framework/platform
        // roots must NOT trigger test-apps (or platform-apps) provisioning — otherwise
        // any Microsoft dependency creates an unsatisfiable completeness check.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Power BI Reports", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.False(needs.NeedsPlatformApps);
        Assert.False(needs.NeedsTestApps);
    }

    // ── NoFallbackPlatformAppsPresent ──────────────────────────────────────────
    // Deliberately narrower than "all curated platform apps present": System/Base
    // Application and Business Foundation have a service-tier DLL dispatch fallback (the
    // runner runs their codeunits even with NO .app vendored — see KnownPlatformRuntimeApps'
    // doc comment), so their absence alone is not a gap; only PRESENT-BUT-SYMBOL-ONLY is
    // (CheckPlatformApps, unchanged). Application Test Library has NO such fallback (see
    // ArtifactDownloader.PlatformApps) — its absence is always a real gap. Scoping the
    // "must literally be present" check to just this app avoids a blast-radius regression:
    // almost every bundle's app.json carries implicit `application`/`platform` roots, so a
    // check requiring literal Base/System Application presence would newly fail nearly
    // every bundle that today runs fine via the DLL-dispatch fallback.

    [Fact]
    public void NoFallbackPlatformAppsPresent_EmptyDir_ReturnsFalse()
    {
        Assert.False(ProvisioningCheck.NoFallbackPlatformAppsPresent(new[] { _dir }));
    }

    [Fact]
    public void NoFallbackPlatformAppsPresent_OnlyLegacyThreePresent_StillReturnsFalse()
    {
        // Base/System Application + Business Foundation present is NOT evidence that
        // Application Test Library is — the two are independent artifact-set members.
        var dir = Path.Combine(_dir, "pkg-legacy-three");
        Directory.CreateDirectory(dir);
        var names = new[] { "System Application", "Base Application", "Business Foundation" };
        int i = 0;
        foreach (var n in names)
            WriteR2RApp(dir, $"app{i++}.app", Guid.NewGuid().ToString(), n, "Microsoft", "28.1.0.0");

        Assert.False(ProvisioningCheck.NoFallbackPlatformAppsPresent(new[] { dir }));
    }

    [Fact]
    public void NoFallbackPlatformAppsPresent_ApplicationTestLibraryPresent_ReturnsTrue()
    {
        var dir = Path.Combine(_dir, "pkg-atl-present");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.1.0.0");

        Assert.True(ProvisioningCheck.NoFallbackPlatformAppsPresent(new[] { dir }));
    }

    // ── DecideManifestProvisioning ─────────────────────────────────────────────

    [Fact]
    public void DecideManifestProvisioning_EmptyCache_ManifestNeedsPlatform_ShouldDownload()
    {
        // The exact shape of issue #1996's repro: an empty package cache + a bundle whose
        // app.json depends on Microsoft/Application Test Library. CheckPlatformApps alone
        // reports "Ok" (nothing found = nothing symbol-only), which is the bug.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 0, 0, 0)),
        };
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", Array.Empty<string>());
        Assert.True(legacyReport.Ok); // sanity: confirms the vacuous-Ok bug still exists in the legacy check

        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, Array.Empty<string>());

        Assert.True(decision.NeedsPlatformApps);
        Assert.False(decision.PlatformComplete);
        Assert.True(decision.ShouldDownloadPlatform);
        // ATL's own manifest transitively needs the test toolkit too (Any, …) — see
        // DetermineManifestNeeds_ApplicationTestLibraryDependency_NeedsBothPlatformAndTest.
        Assert.True(decision.NeedsTestApps);
        Assert.True(decision.ShouldDownloadTest);
    }

    [Fact]
    public void DecideManifestProvisioning_CompleteCacheAlreadyPresent_NoDownload()
    {
        // AC #4 / #5: a warm/complete cache — whether it's the runner-owned versioned
        // destination from a prior run, or a complete explicit/default --package-cache —
        // must short-circuit BEFORE any network attempt.
        var dir = Path.Combine(_dir, "pkg-warm");
        Directory.CreateDirectory(dir);
        var names = new[]
        {
            "Application", "System", "System Application", "Base Application",
            "Business Foundation", "Application Test Library",
        };
        int i = 0;
        foreach (var n in names)
            WriteR2RApp(dir, $"app{i++}.app", Guid.NewGuid().ToString(), n, "Microsoft", "28.1.0.0");

        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 0, 0, 0)),
        };
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });

        Assert.True(decision.PlatformComplete);
        Assert.False(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_LegacySymbolOnlyIssue_AlwaysDownloads()
    {
        // Backward-compat: a found-but-symbol-only R2R app is a gap even with no
        // manifest need (e.g. no app.json at all, or reading it failed) — this must not
        // regress the pre-existing #1678 behavior.
        var dir = Path.Combine(_dir, "pkg-symbol-only");
        Directory.CreateDirectory(dir);
        WriteSymbolOnlyApp(dir, "microsoft_system application_28.1.0.0.app",
            Guid.NewGuid().ToString(), "System Application", "Microsoft", "28.1.0.0");

        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", new[] { dir });
        Assert.False(legacyReport.Ok);

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            Array.Empty<DependencyRef>(), legacyReport, new[] { dir });

        Assert.False(decision.NeedsPlatformApps);
        Assert.True(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_UnrelatedMicrosoftExtension_NeverTriggersDownload()
    {
        // AC #7 at the decision level: an unrelated Microsoft app dependency (outside
        // the curated platform/test roots) must not create an unsatisfiable "need".
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Power BI Reports", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", Array.Empty<string>());
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, Array.Empty<string>());

        Assert.False(decision.ShouldDownloadPlatform);
        Assert.False(decision.ShouldDownloadTest);
    }

    // ── TryReadManifestDependencyRoots (AC #9: malformed manifest = pre-scan miss) ────

    [Fact]
    public void TryReadManifestDependencyRoots_MalformedManifest_SkippedNotThrown()
    {
        var calls = new List<string>();
        Func<string, IEnumerable<DependencyRef>> reader = path =>
        {
            calls.Add(path);
            throw new System.Text.Json.JsonException("not an object");
        };
        var errors = new List<string>();

        var result = ProvisioningCheck.TryReadManifestDependencyRoots(
            new[] { "/fake/app.json" }, reader, errors.Add);

        Assert.Empty(result);
        Assert.Single(calls);
        Assert.Contains(errors, e => e.Contains("/fake/app.json"));
    }

    [Fact]
    public void TryReadManifestDependencyRoots_MixedValidAndMalformed_ReturnsOnlyValid()
    {
        var good = new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 0, 0, 0));
        Func<string, IEnumerable<DependencyRef>> reader = path =>
        {
            if (path == "/bad/app.json") throw new System.Text.Json.JsonException("boom");
            return new[] { good };
        };

        var result = ProvisioningCheck.TryReadManifestDependencyRoots(
            new[] { "/bad/app.json", "/good/app.json" }, reader);

        Assert.Single(result);
        Assert.Equal("Application Test Library", result[0].Name);
    }
}
