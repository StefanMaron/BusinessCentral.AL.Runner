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
        // The exact manual command, with version — issue #2085: this must be the
        // tool-install-valid `provision --service-tier` subcommand, never
        // `dotnet run --project tools/DownloadArtifacts`, which requires a source checkout
        // a `dotnet tool install` user never has.
        Assert.Contains("al-runner provision --service-tier --bc-version 28.2.50931.52786", msg);
        Assert.DoesNotContain("dotnet run --project", msg);
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
    public void DetermineManifestNeeds_ImplicitApplicationAndSystemRootsAlone_NeedNoTestApps()
    {
        // Mirrors ReadDependencies' synthesis of implicit `application`/`platform` roots
        // (Guid.Empty, "Application"/"System", "Microsoft", Optional: true) — present on
        // essentially every AL Runner bundle.
        //
        // This used to assert NeedsPlatformApps == false as well, on the premise that
        // System/Base Application and Business Foundation have a service-tier DLL dispatch
        // fallback, so their absence is never a gap. Issue #2205 measured that premise
        // false on a cold cache: the fallback serves runtime dispatch, not compile-time
        // symbols, so an ordinary app with exactly these roots and nothing on disk does not
        // compile at all. The platform half of the claim now lives in
        // DetermineManifestNeeds_ImplicitMicrosoftRoots_RequireTheAppsTheyName.
        //
        // What stays true here, and is the half worth keeping: these roots say nothing
        // about the MS test toolkit, which is a separate download.
        var roots = new[]
        {
            new DependencyRef(Guid.Empty, "Application", "Microsoft", new Version(28, 1, 0, 0), Optional: true),
            new DependencyRef(Guid.Empty, "System", "Microsoft", new Version(28, 1, 0, 0), Optional: true),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
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

    [Fact]
    public void DetermineManifestNeeds_TestsTestLibrariesDependency_NeedsPlatformOnceItsRealEdgeIsKnown()
    {
        // Issue #2073: a bundle depending on "Tests-TestLibraries" (already recognized as a
        // test-framework root, hence NeedsTestApps) never names "Application Test Library"
        // directly — but on BC 28.x Tests-TestLibraries' OWN manifest declares it
        // (<Dependency Id="d852d5d2-a39d-4179-baeb-f99a19e32510" Name="Application Test
        // Library" Publisher="Microsoft" .../> — the exact AppId the issue's "Missing:"
        // error names). Before that fix this root produced NeedsPlatformApps == false, so
        // `provision` reported "already present" and downloaded nothing.
        //
        // Issue #2103: that edge is no longer transcribed by hand — it is read from the
        // package on disk (ProvisioningCheck.ScanDependencyEdges), because the edge is
        // VERSION-SPECIFIC: on BC 27.x the same app declares Library Variable Storage +
        // Business Foundation Test Libraries and no Application Test Library at all. So this
        // test states the walk in both directions: known edge → need; nothing known yet →
        // no invented need, but still the test-apps download that reveals it. The real
        // 27-vs-28 shapes are pinned in ManifestDependencyEdgeScanTests.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Tests-TestLibraries", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var bc28Edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tests-TestLibraries"] = new[]
            {
                "System Application Test Library", "Permissions Mock", "Application Test Library",
            },
        };

        var withEdge = ProvisioningCheck.DetermineManifestNeeds(roots, bc28Edges);
        Assert.True(withEdge.NeedsPlatformApps);
        Assert.True(withEdge.NeedsTestApps);

        var nothingKnownYet = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.False(nothingKnownYet.NeedsPlatformApps);
        Assert.True(nothingKnownYet.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_TestsTestLibrariesDependency_EmptyCacheStillDownloadsTheSetThatRevealsTheNeed()
    {
        // The end-to-end shape of #2073's repro: a bundle naming only "Tests-TestLibraries",
        // with an empty package cache (nothing provisioned yet).
        //
        // Issue #2103 changed WHICH download that produces. With nothing on disk there is no
        // manifest to read, and the runner no longer guesses from a version-blind table — it
        // asks for the test-apps set, which is the set carrying Tests-TestLibraries' own
        // manifest. The platform need is then derived from that real manifest on the next
        // pass (DecideManifestProvisioning_ColdCache_LearnsThePlatformNeedOnceTheTestSetLands
        // in ManifestDependencyEdgeScanTests proves the second round). One extra round, and
        // a right answer on every BC version, instead of one round and a wrong answer on 27.x.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Tests-TestLibraries", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", Array.Empty<string>());
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, Array.Empty<string>());
        Assert.True(decision.NeedsTestApps);
        Assert.True(decision.ShouldDownloadTest);
        Assert.False(decision.NeedsPlatformApps);
        Assert.False(decision.ShouldDownloadPlatform);
    }

    // ── Issue #2087: transitive need must be DERIVED (a closure walk over recorded
    // dependency edges), not a hand-maintained list of "apps known to reach the no-fallback
    // set today". Before this fix, DetermineManifestNeeds could only recognize the ONE
    // literal name #2086 hardcoded (Tests-TestLibraries); a different Microsoft app with
    // the identical shape (declares a dependency that itself, or transitively, reaches
    // "Application Test Library") was invisible to it. These tests prove the WALK, not just
    // the two apps that happen to already be known.

    [Fact]
    public void DetermineManifestNeeds_TransitiveClosure_CatchesAnyChainNotJustTheKnownOne()
    {
        // Synthetic dependency graph standing in for "the next Microsoft app with the same
        // shape" (issue #2087's whole point): a two-hop chain nobody has hand-listed
        // anywhere, ending at "Application Test Library" (a real KnownNoFallbackPlatformApps
        // member). Neither name here is "Tests-TestLibraries" or in KnownTestFrameworkAppNames
        // — a bespoke one-entry list keyed on THAT name could never catch this. The walk must.
        var syntheticEdges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contoso-Style Future App"] = new[] { "Some Intermediate Microsoft App" },
            ["Some Intermediate Microsoft App"] = new[] { "Application Test Library" },
        };
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Contoso-Style Future App", "Microsoft", new Version(29, 0, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots, syntheticEdges);
        Assert.True(needs.NeedsPlatformApps);
        Assert.True(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_ClosureWalk_DoesNotOverfireOnUnrelatedChain()
    {
        // Negative direction (issue #2087 acceptance): a synthetic app whose OWN declared
        // dependency chain never reaches a KnownNoFallbackPlatformApps member must NOT be
        // flagged. Proves the walk terminates on a real (non-trivial, multi-edge) graph
        // without false-positiving — the mistake "widen to every known test-framework app"
        // would have made.
        var syntheticEdges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contoso-Style Future App"] = new[] { "Some Intermediate Microsoft App" },
            ["Some Intermediate Microsoft App"] = new[] { "Some Unrelated Microsoft App" },
        };
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Contoso-Style Future App", "Microsoft", new Version(29, 0, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots, syntheticEdges);
        Assert.False(needs.NeedsPlatformApps);
    }

    [Fact]
    public void DetermineManifestNeeds_SystemApplicationTestLibraryDependency_NeedsTestNotPlatform()
    {
        // Real Microsoft app (confirmed via its own NavxManifest.xml on BC 27.0, 27.5, 28.0,
        // 28.1 and 28.3 — the same two edges on every one): "System Application Test Library"
        // depends on "System Application" and "Any", and NEITHER reaches "Application Test
        // Library". Proves the closure walk doesn't over-fire just because an app HAS edges;
        // it must actually reach the target.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "System Application Test Library", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.True(needs.NeedsTestApps);
        Assert.False(needs.NeedsPlatformApps);
    }

    [Fact]
    public void ReachesAnyOf_DirectMember_ReturnsTrue()
    {
        var edges = new Dictionary<string, IReadOnlyList<string>>();
        Assert.True(ProvisioningCheck.ReachesAnyOf("Application Test Library", edges, new[] { "Application Test Library" }));
    }

    [Fact]
    public void ReachesAnyOf_MultiHopChain_ReturnsTrue()
    {
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new[] { "B" },
            ["B"] = new[] { "C" },
            ["C"] = new[] { "Target" },
        };
        Assert.True(ProvisioningCheck.ReachesAnyOf("A", edges, new[] { "Target" }));
    }

    [Fact]
    public void ReachesAnyOf_NoPathToTarget_ReturnsFalse()
    {
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new[] { "B" },
            ["B"] = new[] { "C" },
        };
        Assert.False(ProvisioningCheck.ReachesAnyOf("A", edges, new[] { "Target" }));
    }

    [Fact]
    public void ReachesAnyOf_CyclicEdges_TerminatesWithoutHanging()
    {
        // A malformed/future edge table with a cycle must not hang the walk — cycle safety
        // is the mechanism's own correctness property, independent of any specific app name.
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new[] { "B" },
            ["B"] = new[] { "A" },
        };
        Assert.False(ProvisioningCheck.ReachesAnyOf("A", edges, new[] { "Target" }));
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

    // ── Issue #2003: manifest-driven version floors ───────────────────────────
    // FindWarmProvisionedVersion used to decide "reuse this warm set" on presence alone,
    // ignoring the version floor the bundle's app.json manifests declare. A warm set at the
    // same major.minor but an OLDER patch than the manifest requires was reused
    // unconditionally, and the run failed later on a compile diagnostic pointing at the test
    // code rather than a message naming the stale provisioning. These tests drive the shared
    // primitives (DetermineVersionFloors / FindVersionFloorViolations / the floor-aware
    // NoFallbackPlatformAppsPresent+TestToolkitPresent overloads / DecideManifestProvisioning)
    // that both the initial gate and FindWarmProvisionedVersion's warm-reuse scan consult.

    [Fact]
    public void DetermineVersionFloors_TwoRootsSameApp_KeepsHigherVersion()
    {
        // A looser dependency declared elsewhere must never relax the strictest floor.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 0, 0, 0)),
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 1, 5, 0)),
        };
        var floors = ProvisioningCheck.DetermineVersionFloors(roots);
        Assert.Equal(new Version(28, 1, 5, 0), floors["Application Test Library"]);
    }

    [Fact]
    public void DetermineVersionFloors_NonMicrosoftPublisher_Ignored()
    {
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Contoso ISV", new Version(9, 9, 9, 9)),
        };
        var floors = ProvisioningCheck.DetermineVersionFloors(roots);
        Assert.False(floors.ContainsKey("Application Test Library"));
    }

    [Fact]
    public void DetermineVersionFloors_NoMicrosoftRoots_ReturnsEmptyMap()
    {
        // AC #4 basis: a bundle whose manifests declare no floor gets an empty map, which
        // every floor-aware lookup below then treats identically to "no floor given".
        var floors = ProvisioningCheck.DetermineVersionFloors(Array.Empty<DependencyRef>());
        Assert.Empty(floors);
    }

    [Fact]
    public void FindVersionFloorViolations_AppBelowFloor_ReportsNameFoundAndRequired()
    {
        var dir = Path.Combine(_dir, "warm-stale");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.0.0.0");

        var floors = new Dictionary<string, Version> { ["Application Test Library"] = new Version(28, 1, 0, 0) };
        var violations = ProvisioningCheck.FindVersionFloorViolations(new[] { dir }, floors);

        var v = Assert.Single(violations);
        Assert.Equal("Application Test Library", v.AppName);
        Assert.Equal(new Version(28, 0, 0, 0), v.FoundVersion);
        Assert.Equal(new Version(28, 1, 0, 0), v.RequiredVersion);
    }

    [Fact]
    public void FindVersionFloorViolations_AppAtOrAboveFloor_ReportsNothing()
    {
        var dir = Path.Combine(_dir, "warm-fresh");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.1.0.0");

        var floors = new Dictionary<string, Version> { ["Application Test Library"] = new Version(28, 1, 0, 0) };
        var violations = ProvisioningCheck.FindVersionFloorViolations(new[] { dir }, floors);

        Assert.Empty(violations);
    }

    [Fact]
    public void FindVersionFloorViolations_AppAbsent_ReportsNothing()
    {
        // Plain absence is a presence gap, not a version-floor violation — the two are
        // reported through different mechanisms (CheckPlatformApps/DecideManifestProvisioning
        // for absence, this for "found but stale").
        var floors = new Dictionary<string, Version> { ["Application Test Library"] = new Version(28, 1, 0, 0) };
        var violations = ProvisioningCheck.FindVersionFloorViolations(new[] { _dir }, floors);

        Assert.Empty(violations);
    }

    [Fact]
    public void NoFallbackPlatformAppsPresent_BelowFloor_ReturnsFalse()
    {
        // AC #2: a warm-but-stale Application Test Library does not count as present when
        // the manifest declares a higher floor.
        var dir = Path.Combine(_dir, "atl-stale");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.0.0.0");

        var floors = new Dictionary<string, Version> { ["Application Test Library"] = new Version(28, 1, 0, 0) };
        Assert.False(ProvisioningCheck.NoFallbackPlatformAppsPresent(new[] { dir }, floors));
    }

    [Fact]
    public void NoFallbackPlatformAppsPresent_AtOrAboveFloor_ReturnsTrue()
    {
        // AC #1: a warm set that DOES meet the floor is still reused — the common path.
        var dir = Path.Combine(_dir, "atl-fresh");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.1.5.0");

        var floors = new Dictionary<string, Version> { ["Application Test Library"] = new Version(28, 1, 0, 0) };
        Assert.True(ProvisioningCheck.NoFallbackPlatformAppsPresent(new[] { dir }, floors));
    }

    [Fact]
    public void NoFallbackPlatformAppsPresent_NoFloorsGiven_MatchesOldPresenceOnlyBehavior()
    {
        // AC #4: omitting versionFloors (or passing null, the default) must reproduce the
        // pre-#2003 presence-only behavior exactly — an old app still counts as present.
        var dir = Path.Combine(_dir, "atl-no-floor");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "1.0.0.0");

        Assert.True(ProvisioningCheck.NoFallbackPlatformAppsPresent(new[] { dir }));
    }

    [Fact]
    public void TestToolkitPresent_BelowFloor_ReturnsFalse()
    {
        var dir = Path.Combine(_dir, "toolkit-stale");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "bftl.app", Guid.NewGuid().ToString(),
            ProvisioningCheck.TestToolkitSentinelApp, "Microsoft", "28.0.0.0");

        var floors = new Dictionary<string, Version> { [ProvisioningCheck.TestToolkitSentinelApp] = new Version(28, 1, 0, 0) };
        Assert.False(ProvisioningCheck.TestToolkitPresent(new[] { dir }, floors));
    }

    [Fact]
    public void TestToolkitPresent_AtOrAboveFloor_ReturnsTrue()
    {
        var dir = Path.Combine(_dir, "toolkit-fresh");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "bftl.app", Guid.NewGuid().ToString(),
            ProvisioningCheck.TestToolkitSentinelApp, "Microsoft", "28.1.0.0");

        var floors = new Dictionary<string, Version> { [ProvisioningCheck.TestToolkitSentinelApp] = new Version(28, 1, 0, 0) };
        Assert.True(ProvisioningCheck.TestToolkitPresent(new[] { dir }, floors));
    }

    [Fact]
    public void DecideManifestProvisioning_WarmSetBelowDeclaredFloor_NotReused_DownloadsInstead()
    {
        // AC #2, wired through the SAME decision the initial gate (and the warm-reuse re-
        // check after a download) both consult — not just a standalone helper.
        var dir = Path.Combine(_dir, "decide-stale");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.0.0.0");

        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });

        Assert.False(decision.PlatformComplete);
        Assert.True(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_WarmSetMeetsDeclaredFloor_ReusedNoDownload()
    {
        // AC #1: the common case — a warm set that DOES meet the floor is still reused
        // with no download. A regression here means every run starts downloading.
        var dir = Path.Combine(_dir, "decide-fresh");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "28.1.5.0");

        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Application Test Library", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });

        Assert.True(decision.PlatformComplete);
        Assert.False(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_NoDeclaredFloor_KeepsPresenceOnlyBehavior()
    {
        // AC #4: a bundle whose manifests declare NO version for the dependency at all (the
        // implicit `application`/`platform` synthesis passes Optional roots without pinning
        // a real floor beyond whatever ships) must not newly reject a warm set it would have
        // accepted before #2003. Simulate "no floor" the same way DetermineVersionFloors
        // would see it for an app that's warm-present but was never named in any manifest
        // root — DecideManifestProvisioning is called with roots that don't mention
        // Application Test Library at all, only the legacy symbol-only signal drives it.
        var dir = Path.Combine(_dir, "decide-no-floor");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "atl.app", Guid.NewGuid().ToString(), "Application Test Library", "Microsoft", "1.0.0.0");

        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.50794", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(Array.Empty<DependencyRef>(), legacyReport, new[] { dir });

        Assert.True(decision.PlatformComplete);
        Assert.False(decision.ShouldDownloadPlatform);
    }

    // ── ResolveProvisionMajorMinor / BuildProvisionVersionSkewNote (issue #2077) ──────────
    // `--bc-version 28.4` was observed provisioning 28.1 platform apps because the
    // provisioning minor used to be DERIVED from whatever was already in the package cache
    // (a project's committed `.alpackages`, or a stale symbol-only app) instead of the BC
    // version the run had already selected. These prove the decision in isolation, and the
    // loud note the fix emits when the cache disagrees with the selection.

    [Fact]
    public void ResolveProvisionMajorMinor_AlwaysUsesSelectedVersion_IgnoresCache()
    {
        // The exact repro shape: engine/selection is 28.4, regardless of anything found on
        // disk elsewhere — this function takes no cache input at all, by design.
        var mm = ProvisioningCheck.ResolveProvisionMajorMinor("28.4.53241.53989");
        Assert.Equal("28.4", mm);
    }

    [Fact]
    public void ResolveProvisionMajorMinor_ShortVersion_ReturnedAsIs()
    {
        var mm = ProvisioningCheck.ResolveProvisionMajorMinor("28");
        Assert.Equal("28", mm);
    }

    [Fact]
    public void BuildProvisionVersionSkewNote_CacheAgrees_ReturnsNull()
    {
        var note = ProvisioningCheck.BuildProvisionVersionSkewNote("28.4", "28.4", "platform apps in cache");
        Assert.Null(note);
    }

    [Fact]
    public void BuildProvisionVersionSkewNote_CacheDisagrees_NamesBothVersionsLoudly()
    {
        // The Pageworks.Bench repro: selected 28.4, but the bundle's committed
        // `.alpackages` vendors a 28.1 symbol closure. The note must name BOTH versions —
        // a vague "version mismatch" would not tell a reader which one actually got used.
        var note = ProvisioningCheck.BuildProvisionVersionSkewNote(
            "28.4", "28.1", "platform apps already in the package cache");
        Assert.NotNull(note);
        Assert.Contains("28.1", note);
        Assert.Contains("28.4", note);
        Assert.Contains("SELECTED", note);
    }

    [Fact]
    public void BuildProvisionVersionSkewNote_CaseInsensitiveAgreement_ReturnsNull()
    {
        var note = ProvisioningCheck.BuildProvisionVersionSkewNote("28.4", "28.4", "x");
        Assert.Null(note);
    }

    // ── Issue #2205: cold cache + an ordinary AL app ──────────────────────────
    // Every real AL extension declares its Microsoft roots through app.json's
    // `application`/`platform` fields, not the `dependencies` array — ReadDependencies
    // synthesises them as Optional Microsoft/Application + Microsoft/System roots. The
    // need-detection above used to ignore those roots entirely, on the premise that
    // System/Base Application and Business Foundation have a service-tier DLL dispatch
    // fallback so their ABSENCE is never a gap (only PRESENT-but-symbol-only is).
    //
    // That premise is false on a cold cache: the DLL fallback serves RUNTIME DISPATCH, it
    // does not supply COMPILE-TIME SYMBOLS. With engine-only artifacts on disk the app
    // never compiles, so there is no runtime for the fallback to serve — the run died with
    // EMIT-EXCLUDED and two unframed `[deps] dependency not found in cache, skipping`
    // lines, and `provision` reported "nothing to provision" and exited 0.
    //
    // The distinction these pin is ABSENT vs PRESENT-BUT-SYMBOL-ONLY, asked of whatever
    // the manifest actually names — not membership of a hardcoded exemption list. The
    // warm arms are the constraint that matters most: a warm cache must keep deciding
    // "no download", or every bundle in the corpus starts claiming it needs one.

    /// <summary>The implicit roots ReadDependencies synthesises for an ordinary AL app
    /// whose app.json carries `"application"` + `"platform"` and `"dependencies": []`.</summary>
    private static DependencyRef[] ImplicitMicrosoftRoots(string version = "27.0.0.0") => new[]
    {
        new DependencyRef(Guid.Empty, "Application", "Microsoft", Version.Parse(version), Optional: true),
        new DependencyRef(Guid.Empty, "System", "Microsoft", Version.Parse(version), Optional: true),
    };

    [Fact]
    public void DetermineManifestNeeds_ImplicitMicrosoftRoots_RequireTheAppsTheyName()
    {
        var needs = ProvisioningCheck.DetermineManifestNeeds(ImplicitMicrosoftRoots());

        Assert.True(needs.NeedsPlatformApps);
        // Exactly the two apps the manifest named — NOT the whole downloadable set. A
        // bundle that never mentions Business Foundation must not be told it needs it.
        Assert.Equal(new[] { "Application", "System" }, needs.RequiredPlatformApps.OrderBy(n => n).ToArray());
        // The test-apps set is a SEPARATE 20 MB download and these roots say nothing about
        // it. Broadening the platform need must not drag the toolkit along.
        Assert.False(needs.NeedsTestApps);
    }

    // ── Issue #2229 (regression, not a fix here) ──────────────────────────────
    // The FIRST attempt at #2229 filtered the implicit Application/System roots out of
    // the platform-app requirement whenever their floor's major was below 10 (a
    // "no real BC build has ever shipped below 10.0" placeholder heuristic). That is
    // wrong: a `1.0.0.0` floor and genuine Base/System Application usage are NOT
    // mutually exclusive. Measured directly — an ordinary app, `"dependencies": []`,
    // `application`/`platform` of `1.0.0.0`, one test using `Codeunit "Environment
    // Information"` (System Application) — regressed from `2P/0F/0E` on main to
    // `EMIT-ZERO`/AL0185 "Codeunit 'Environment Information' is missing" on that attempt,
    // because the placeholder-floor heuristic can't see what the AL source references —
    // only the manifest, which is IDENTICAL for "never touches Microsoft" and "touches
    // Microsoft AND set no real floor". #2232 already reaches this for the mirror shape:
    // separating "declared" from "actually used" needs a compile attempt, not a version
    // sentinel. These two pin the correct, unconditional behavior permanently.
    [Fact]
    public void DetermineManifestNeeds_ImplicitMicrosoftRootsAtPlaceholderFloor_StillRequireTheAppsTheyName()
    {
        var needs = ProvisioningCheck.DetermineManifestNeeds(ImplicitMicrosoftRoots("1.0.0.0"));

        // The floor value must never launder away a real requirement — only the AL
        // source (unavailable here) could ever tell "unused" apart from "used", and the
        // manifest alone can't see it. So absent that information, over-including (this)
        // is the only safe default; under-including regressed a real, minimal repro.
        Assert.True(needs.NeedsPlatformApps);
        Assert.Equal(new[] { "Application", "System" }, needs.RequiredPlatformApps.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void DecideManifestProvisioning_ColdCache_PlaceholderFloorApp_StillNeedsDownload()
    {
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.53910", Array.Empty<string>());

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            ImplicitMicrosoftRoots("1.0.0.0"), legacyReport, Array.Empty<string>());

        Assert.True(decision.NeedsPlatformApps);
        Assert.True(decision.ShouldDownloadPlatform);
        Assert.True(decision.ShouldDownloadAny);
        Assert.Equal(new[] { "Application", "System" }, decision.MissingPlatformApps.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void DecideManifestProvisioning_ColdCache_OrdinaryAlApp_NeedsPlatformApps()
    {
        // Issue #2205's exact repro shape: engine artifacts on disk, nothing else. The
        // legacy symbol-only check reports Ok vacuously (nothing found = nothing broken),
        // so the decision must come from the manifest.
        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.53910", Array.Empty<string>());
        Assert.True(legacyReport.Ok);

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            ImplicitMicrosoftRoots(), legacyReport, Array.Empty<string>());

        Assert.True(decision.NeedsPlatformApps);
        Assert.False(decision.PlatformComplete);
        Assert.True(decision.ShouldDownloadPlatform);
        Assert.Equal(new[] { "Application", "System" }, decision.MissingPlatformApps.OrderBy(n => n).ToArray());
        Assert.False(decision.ShouldDownloadTest);
    }

    [Fact]
    public void DecideManifestProvisioning_WarmCache_OrdinaryAlApp_NoDownload()
    {
        // THE no-spurious-download constraint. Same bundle, same roots, platform apps
        // already on disk: the decision must be identical to what it was before #2205 —
        // nothing to download. A regression here makes every warm corpus bundle start
        // claiming it needs a 120 MB fetch on every single run.
        var dir = Path.Combine(_dir, "warm-ordinary");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "28.1.49838.53910");
        WriteR2RApp(dir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "28.0.53872.0");

        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.53910", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(
            ImplicitMicrosoftRoots(), legacyReport, new[] { dir });

        Assert.True(decision.NeedsPlatformApps);
        Assert.True(decision.PlatformComplete);
        Assert.Empty(decision.MissingPlatformApps);
        Assert.False(decision.ShouldDownloadPlatform);
        Assert.False(decision.ShouldDownloadTest);
    }

    [Fact]
    public void DecideManifestProvisioning_OrdinaryAlApp_PresentButSymbolOnly_StillADownload()
    {
        // The other side of the absent/symbol-only distinction, unchanged by #2205: an app
        // that IS on disk but only as a symbol package cannot execute, so it stays a gap.
        // Proving both states here is what makes "absent" a real classification rather
        // than a synonym for "not R2R".
        var dir = Path.Combine(_dir, "symbolonly-ordinary");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "28.1.49838.53910");
        WriteR2RApp(dir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "28.0.53872.0");
        WriteSymbolOnlyApp(dir, "sysapp.app", Guid.NewGuid().ToString(), "System Application", "Microsoft", "28.1.49838.53910");

        var legacyReport = ProvisioningCheck.CheckPlatformApps("28.1.49838.53910", new[] { dir });
        Assert.False(legacyReport.Ok);

        var decision = ProvisioningCheck.DecideManifestProvisioning(
            ImplicitMicrosoftRoots(), legacyReport, new[] { dir });

        // The two apps the manifest named ARE present, so nothing is "missing"...
        Assert.Empty(decision.MissingPlatformApps);
        // ...yet the symbol-only System Application still forces the download.
        Assert.True(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_ExplicitMicrosoftRoots_RequireExactlyThoseApps()
    {
        // The al-language corpus shape: `application`/`platform` PLUS explicit Base/System
        // Application dependencies at a declared floor. Warm, at or above the floor, this
        // must still decide "no download".
        var dir = Path.Combine(_dir, "warm-corpus");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "27.5.46862.53931");
        WriteR2RApp(dir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "27.5.46862.0");
        WriteR2RApp(dir, "sysapp.app", Guid.NewGuid().ToString(), "System Application", "Microsoft", "27.5.46862.53931");
        WriteR2RApp(dir, "baseapp.app", Guid.NewGuid().ToString(), "Base Application", "Microsoft", "27.5.46862.53931");

        var roots = ImplicitMicrosoftRoots().Concat(new[]
        {
            new DependencyRef(Guid.NewGuid(), "System Application", "Microsoft", new Version(27, 5, 0, 0)),
            new DependencyRef(Guid.NewGuid(), "Base Application", "Microsoft", new Version(27, 5, 0, 0)),
        }).ToArray();

        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);
        Assert.Equal(
            new[] { "Application", "Base Application", "System", "System Application" },
            needs.RequiredPlatformApps.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // Business Foundation and Application Test Library are in the downloadable set but
        // this manifest names neither — they must not be demanded, and their absence from
        // the warm dir above must not make it look incomplete.
        Assert.DoesNotContain("Business Foundation", needs.RequiredPlatformApps);
        Assert.DoesNotContain("Application Test Library", needs.RequiredPlatformApps);

        var legacyReport = ProvisioningCheck.CheckPlatformApps("27.5.46862.53931", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });
        Assert.True(decision.PlatformComplete);
        Assert.False(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_ExplicitMicrosoftRootBelowFloor_IsNotPresent()
    {
        // Negative arm of the floor rule at the broadened set: a Base Application found
        // BELOW the floor the manifest declares reads as missing, not present.
        var dir = Path.Combine(_dir, "stale-baseapp");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "27.5.46862.53931");
        WriteR2RApp(dir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "27.5.46862.0");
        WriteR2RApp(dir, "baseapp.app", Guid.NewGuid().ToString(), "Base Application", "Microsoft", "27.0.0.0");

        var roots = ImplicitMicrosoftRoots().Concat(new[]
        {
            new DependencyRef(Guid.NewGuid(), "Base Application", "Microsoft", new Version(27, 5, 0, 0)),
        }).ToArray();

        var legacyReport = ProvisioningCheck.CheckPlatformApps("27.5.46862.53931", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });

        Assert.Equal(new[] { "Base Application" }, decision.MissingPlatformApps.ToArray());
        Assert.True(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_FloorAboveTheSelectedBcVersion_IsNotADownloadDemand()
    {
        // The al-language corpus on the BC 27.0 leg, exactly. Its app.json declares
        // System Application / Base Application >= 27.5.0.0 while the leg provisions 27.0 —
        // and it passes there, because the runner deliberately tolerates a bundle whose
        // declared floor sits above the selected BC version (BcFloorGate owns the case
        // where that is genuinely incompatible).
        //
        // #2003's floor rule was measured on a STALE PATCH — 28.0 present, 28.1 wanted,
        // a newer one obtainable. A floor above the version being provisioned is a
        // different thing: no download can ever clear it, so treating it as "absent" turns
        // every 27.0/27.3 leg into "platform apps still missing after download", exit 2.
        // A floor may only make an app absent when satisfying it is possible.
        var dir = Path.Combine(_dir, "bc27-leg");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "27.0.38460.53260");
        WriteR2RApp(dir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "27.0.38460.0");
        WriteR2RApp(dir, "sysapp.app", Guid.NewGuid().ToString(), "System Application", "Microsoft", "27.0.38460.53260");
        WriteR2RApp(dir, "baseapp.app", Guid.NewGuid().ToString(), "Base Application", "Microsoft", "27.0.38460.53260");

        var roots = ImplicitMicrosoftRoots().Concat(new[]
        {
            new DependencyRef(Guid.NewGuid(), "System Application", "Microsoft", new Version(27, 5, 0, 0)),
            new DependencyRef(Guid.NewGuid(), "Base Application", "Microsoft", new Version(27, 5, 0, 0)),
        }).ToArray();

        var legacyReport = ProvisioningCheck.CheckPlatformApps("27.0.38460.53260", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });

        Assert.Empty(decision.MissingPlatformApps);
        Assert.True(decision.PlatformComplete);
        Assert.False(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DecideManifestProvisioning_FloorWithinTheSelectedBcVersion_StillADownloadDemand()
    {
        // The negative arm: a floor the selected version CAN satisfy keeps #2003's
        // behavior — a stale patch is still a gap, and dropping unsatisfiable floors must
        // not become a licence to ignore satisfiable ones.
        var dir = Path.Combine(_dir, "bc275-stale");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "27.5.46862.53931");
        WriteR2RApp(dir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "27.5.46862.0");
        WriteR2RApp(dir, "baseapp.app", Guid.NewGuid().ToString(), "Base Application", "Microsoft", "27.5.1.0");

        var roots = ImplicitMicrosoftRoots().Concat(new[]
        {
            new DependencyRef(Guid.NewGuid(), "Base Application", "Microsoft", new Version(27, 5, 46862, 53931)),
        }).ToArray();

        var legacyReport = ProvisioningCheck.CheckPlatformApps("27.5.46862.53931", new[] { dir });
        var decision = ProvisioningCheck.DecideManifestProvisioning(roots, legacyReport, new[] { dir });

        Assert.Equal(new[] { "Base Application" }, decision.MissingPlatformApps.ToArray());
        Assert.True(decision.ShouldDownloadPlatform);
    }

    [Fact]
    public void DropUnsatisfiableFloors_KeepsWhatTheVersionCanSupply_DropsWhatItCannot()
    {
        var floors = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["Base Application"] = new Version(27, 5, 0, 0),
            ["Application Test Library"] = new Version(27, 0, 0, 0),
        };

        var on27_0 = ProvisioningCheck.DropUnsatisfiableFloors(floors, "27.0.38460.53260");
        Assert.False(on27_0.ContainsKey("Base Application"));
        Assert.Equal(new Version(27, 0, 0, 0), on27_0["Application Test Library"]);

        var on28_1 = ProvisioningCheck.DropUnsatisfiableFloors(floors, "28.1.49838.53910");
        Assert.Equal(new Version(27, 5, 0, 0), on28_1["Base Application"]);
        Assert.Equal(new Version(27, 0, 0, 0), on28_1["Application Test Library"]);

        // An unparseable/absent version cannot rule anything out — keep every floor rather
        // than silently relax them all.
        var unknown = ProvisioningCheck.DropUnsatisfiableFloors(floors, "not-a-version");
        Assert.Equal(2, unknown.Count);
    }

    [Fact]
    public void DetermineManifestNeeds_NoMicrosoftRootsAtAll_RequiresNothing()
    {
        // A bundle with no `application`/`platform` fields and no Microsoft dependency (the
        // al-language-internals-fixture shape) must still require nothing — the broadened
        // rule keys off what the manifest NAMES, so naming nothing demands nothing.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "AL Internals Test Fixture", "AL Language", new Version(1, 0, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);

        Assert.False(needs.NeedsPlatformApps);
        Assert.Empty(needs.RequiredPlatformApps);
        Assert.False(needs.NeedsTestApps);
    }

    [Fact]
    public void DetermineManifestNeeds_UnrelatedMicrosoftExtension_StillRequiresNothing()
    {
        // Unchanged by #2205: a Microsoft app the platform-apps set cannot supply must not
        // become an unsatisfiable requirement.
        var roots = new[]
        {
            new DependencyRef(Guid.NewGuid(), "Power BI Reports", "Microsoft", new Version(28, 1, 0, 0)),
        };
        var needs = ProvisioningCheck.DetermineManifestNeeds(roots);

        Assert.False(needs.NeedsPlatformApps);
        Assert.Empty(needs.RequiredPlatformApps);
    }

    // ── Issue #2205, second half: `provision` must not claim a need does not exist ─────

    [Fact]
    public void BuildPlatformProvisionSkippedMessage_NeedVerifiedPresent_SaysWhatItFound()
    {
        var msg = ProvisioningCheck.BuildPlatformProvisionSkippedMessage(
            new[] { "Application", "System" }, new[] { "/cache/platform-apps" });

        Assert.Contains("Application", msg);
        Assert.Contains("System", msg);
        Assert.Contains("/cache/platform-apps", msg);
        // #2073's intent preserved: presence is claimed only because it was verified.
        Assert.Contains("already present", msg);
        Assert.DoesNotContain("do not need", msg);
    }

    [Fact]
    public void BuildPlatformProvisionSkippedMessage_NoNeedDeclared_StatesWhatWasChecked()
    {
        // The wrong answer #2205 reports: "target bundle(s) do not need the platform R2R
        // apps set" for a bundle that demonstrably needs it. The honest form states what
        // was examined and what was found there, and never asserts a need does not exist.
        var msg = ProvisioningCheck.BuildPlatformProvisionSkippedMessage(
            Array.Empty<string>(), new[] { "/cache/platform-apps" });

        Assert.DoesNotContain("do not need", msg);
        Assert.Contains("app.json", msg);
        Assert.Contains("Microsoft", msg);
    }

    [Fact]
    public void BuildManifestNeedsMissingMessage_NamesTheMissingAppsNotJustTheSet()
    {
        // The run-path message. Naming "the Microsoft platform-app set" alone told the
        // reader nothing about which app was actually absent.
        var msg = ProvisioningCheck.BuildManifestNeedsMissingMessage(
            needsPlatform: true, needsTest: false,
            searchedDirs: new[] { "/cache/a" },
            missingPlatformApps: new[] { "Application", "System" });

        Assert.Contains("Application", msg);
        Assert.Contains("System", msg);
        Assert.Contains("/cache/a", msg);
    }

    [Fact]
    public void BuildManifestNeedsMissingMessage_NoSearchedDirs_SaysSoRatherThanTrailingOff()
    {
        // On a genuinely cold cache there is no package cache directory at all, and the
        // line used to render as a bare "  Searched: " — which reads as a value the runner
        // failed to fill in, not as the fact that nothing exists to search yet.
        var msg = ProvisioningCheck.BuildManifestNeedsMissingMessage(
            needsPlatform: true, needsTest: false,
            searchedDirs: Array.Empty<string>(),
            missingPlatformApps: new[] { "Application" });

        Assert.DoesNotContain("Searched: \n", msg);
        Assert.Contains("no package cache directory exists yet", msg);
    }

    // ── FindMissingPlatformApps: absent vs present, in isolation ──────────────

    [Fact]
    public void FindMissingPlatformApps_NamesOnlyTheAbsentOnes()
    {
        var dir = Path.Combine(_dir, "partial-set");
        Directory.CreateDirectory(dir);
        WriteR2RApp(dir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "28.1.0.0");

        var missing = ProvisioningCheck.FindMissingPlatformApps(
            new[] { "Application", "System", "Base Application" }, new[] { dir });

        Assert.Equal(new[] { "Base Application", "System" }, missing.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void FindMissingPlatformApps_EmptyRequirement_FindsNothingMissing()
    {
        var missing = ProvisioningCheck.FindMissingPlatformApps(
            Array.Empty<string>(), new[] { Path.Combine(_dir, "does-not-exist") });

        Assert.Empty(missing);
    }

    // ── Issue #2234: need detection must consult the run path's FULL search set ──
    // #2226 (separate, still open) can leave `provision`'s platform-app and test-app
    // sub-steps under DIFFERENT patch directories of the same major.minor — the engine's
    // own exact build for one sub-step, the CDN's "latest" for the major.minor for the
    // other. Need detection used to build its search set from the SELECTED engine
    // version's own exact patch directory alone, so an app provisioned under a sibling
    // patch directory read as absent even though it was present and usable — the run
    // path found it only as an incidental side effect of the auto-provision reuse scan
    // (FindWarmProvisionedVersion), so `--no-auto-provision` reported the identical
    // "missing" diagnostic on every subsequent run, even immediately after `provision`
    // had just completed successfully (an unbreakable loop: the tool's own advice was to
    // run the command that had just run).

    [Fact]
    public void CollectRunnerOwnedProvisionDirs_FindsDirsAcrossDifferentPatchVersions()
    {
        var root = Path.Combine(_dir, "artifacts-root-2234-collect");
        var enginePatchTestApps = Path.Combine(root, "28.1.49838.53910", "test-apps");
        var laterPatchPlatformApps = Path.Combine(root, "28.1.49838.54044", "platform-apps");
        Directory.CreateDirectory(enginePatchTestApps);
        Directory.CreateDirectory(laterPatchPlatformApps);
        // A DIFFERENT major.minor must never be picked up, even though "28.2" sorts
        // higher than "28.1" — only patches sharing the requested major.minor qualify.
        Directory.CreateDirectory(Path.Combine(root, "28.2.50931.53737", "platform-apps"));

        var dirs = ProvisioningCheck.CollectRunnerOwnedProvisionDirs(root, "28.1");

        Assert.Contains(enginePatchTestApps, dirs);
        Assert.Contains(laterPatchPlatformApps, dirs);
        Assert.DoesNotContain(dirs, d => d.Contains("28.2.50931.53737"));
    }

    [Fact]
    public void CollectRunnerOwnedProvisionDirs_MissingRoot_ReturnsEmpty()
    {
        var dirs = ProvisioningCheck.CollectRunnerOwnedProvisionDirs(
            Path.Combine(_dir, "does-not-exist-2234"), "28.1");

        Assert.Empty(dirs);
    }

    [Fact]
    public void NeedDetection_PlatformAppsUnderADifferentPatchThanSelectedEngine_AreNotReportedMissing()
    {
        // The exact #2234 repro shape: the platform apps live under a DIFFERENT patch
        // directory than the engine's own selected build, and NOTHING at all exists under
        // the engine's own patch directory except the (unrelated) test toolkit. This test
        // would still pass if #2226 were fixed by making both directories always land on
        // the SAME version — the point is it must ALSO pass when they genuinely differ,
        // which is exactly the case a fix to #2226 would stop exercising, leaving this
        // disagreement live and undetected the next time the two steps diverge for any
        // other reason.
        var root = Path.Combine(_dir, "artifacts-root-2234-present-elsewhere");
        const string selectedEngineVersion = "28.1.49838.53910";
        const string differentPatchVersion = "28.1.49838.54044";
        var platformAppsDir = Path.Combine(root, differentPatchVersion, "platform-apps");
        Directory.CreateDirectory(platformAppsDir);
        WriteR2RApp(platformAppsDir, "application.app", Guid.NewGuid().ToString(), "Application", "Microsoft", "28.1.0.0");
        WriteR2RApp(platformAppsDir, "system.app", Guid.NewGuid().ToString(), "System", "Microsoft", "28.1.0.0");
        // Nothing under the engine's OWN patch directory but the unrelated test toolkit —
        // matches the repro's "provision downloaded the test toolkit to the engine's own
        // version, the platform apps to a different one" split exactly.
        Directory.CreateDirectory(Path.Combine(root, selectedEngineVersion, "test-apps"));

        var searchDirs = ProvisioningCheck.CollectRunnerOwnedProvisionDirs(
            root, ProvisioningCheck.ResolveProvisionMajorMinor(selectedEngineVersion));
        var missing = ProvisioningCheck.FindMissingPlatformApps(
            new[] { "Application", "System" }, searchDirs);

        Assert.Empty(missing);
    }

    [Fact]
    public void NeedDetection_PlatformAppsGenuinelyAbsentEverywhere_AreStillReportedMissing()
    {
        // The negative direction: the widened search must not turn "genuinely absent"
        // into a false "present" — regression guard for #2205/#2220's good diagnostic.
        var root = Path.Combine(_dir, "artifacts-root-2234-genuinely-absent");
        Directory.CreateDirectory(Path.Combine(root, "28.1.49838.53910", "test-apps"));
        Directory.CreateDirectory(Path.Combine(root, "28.1.49838.54044")); // no platform-apps subdir at all

        var searchDirs = ProvisioningCheck.CollectRunnerOwnedProvisionDirs(root, "28.1");
        var missing = ProvisioningCheck.FindMissingPlatformApps(
            new[] { "Application", "System" }, searchDirs);

        Assert.Equal(
            new[] { "Application", "System" },
            missing.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
