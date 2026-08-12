// BcCompilerSharedReferenceMemoTests — the shared symbol-reference loader is built ONCE
// for a bundle's dependency compiles, and hiding the self-app is equivalent to deleting it.
//
// Issue #1831
// -----------
// GetSharedReferences memoises the (expensive) reference loader in a single slot keyed by
// ComputeLoaderSignature. Before this change the signature was computed from
// DeduplicateAppPackageDirs' OUTPUT *with the self-app exclusion already applied*, so every
// Tier-3 dependency compile — each of which scopes _currentAppId to its own identity and
// therefore excludes a DIFFERENT app — produced a different signature. The single slot
// missed every time and the loader was rebuilt: a filesystem scan plus
// WarmReferenceLoader, whose cost is per-loader-instance because BC's
// MemoryCachedSymbolReferenceLoader holds its module/dependency caches in instance fields.
// Measured on a cold `tests/runner-extras` leg: 8 rebuilds, ~11.5 s each, ~92 s.
//
// The fix keys the memo on the exclusion-free SUPERSET scan set and applies the per-compile
// exclusion with SelfExcludingSymbolReferenceLoader, which refuses to answer for the
// excluded AppId using BC's own SymbolReferenceSpecification.IsSatisfiedBy predicate.
//
// What these tests pin
// --------------------
//  * POSITIVE  — N dependency compiles that differ ONLY by which app excludes itself build
//                the loader exactly ONCE (assert the count, never a duration).
//  * NEGATIVE  — a genuinely different reference set (an added package dir) still rebuilds,
//                so the memo cannot degenerate into "never invalidates".
//  * EQUIVALENCE — the self-app is still invisible to the compile that excludes it, and
//                every OTHER package stays visible; and the decorator's answers match, call
//                for call, a loader built over a physically reduced scan set.
//
// The equivalence tests are the ones that guard the risk in this change: "load the superset
// once and filter afterwards" is only valid if the filtered result is what a from-scratch
// reduced load produces.

using System.Collections.Immutable;
using System.Reflection;
using Xunit;
using AlRunner;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Packaging;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;

namespace AlRunner.Tests;

/// <summary>
/// These tests mutate BcCompiler's process-wide loader statics, so they must not run
/// concurrently with anything else that compiles AL.
/// </summary>
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class BcCompilerSharedReferenceMemoTests : IDisposable
{
    private readonly string _root;

    public BcCompilerSharedReferenceMemoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-sharedref-memo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BcCompiler.ResetSharedReferencesForTests();
    }

    public void Dispose()
    {
        BcCompiler.ResetSharedReferencesForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── POSITIVE: one build across N self-excluding dependency compiles ───────────────

    /// <summary>
    /// Four dependency compiles, each scoping the "current app" to a different one of the
    /// four packages in the shared cache dir — exactly the shape of DependencyLoader's
    /// Tier-3 loop. The reference sets differ only by which app excludes itself, so the
    /// expensive loader must be built ONCE, not once per dependency.
    /// </summary>
    [Fact]
    public void FourSelfExcludingDepCompiles_BuildTheLoaderExactlyOnce()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 4);

        foreach (var (appId, _, _) in apps)
            using (BcCompiler.ScopeCurrentAppIdentity(appId, "Microsoft", new Version(28, 2, 0, 0)))
                InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>
    /// The main-bundle compile that follows the dependency compiles (its own AppId is not in
    /// the package cache at all) must also reuse the same loader — before the fix it was a
    /// 5th rebuild, because the previous dep's staged, exclusion-specific scan dir gave a
    /// different signature.
    /// </summary>
    [Fact]
    public void MainBundleCompileAfterDepCompiles_ReusesTheSameLoader()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);

        foreach (var (appId, _, _) in apps)
            using (BcCompiler.ScopeCurrentAppIdentity(appId, "Microsoft", new Version(28, 2, 0, 0)))
                InvokeGetSharedReferences(new[] { dir });

        // The bundle's own identity — no .app of it anywhere in the scan set.
        using (BcCompiler.ScopeCurrentAppIdentity(Guid.NewGuid(), "Test", new Version(1, 0, 0, 0)))
            InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
    }

    // ── NEGATIVE: a genuinely different reference set still rebuilds ──────────────────

    /// <summary>
    /// The memo must still invalidate on a real change. A second package dir appearing in
    /// the scan set is a different reference set — the compiler would otherwise be handed a
    /// loader that cannot see the new packages at all. Without this assertion, a memo that
    /// never invalidates would pass the positive test above and be a correctness bug.
    /// </summary>
    [Fact]
    public void AddingAPackageDir_ForcesARebuild()
    {
        var dirA = MakeDir("pkgA");
        WriteApps(dirA, 2);
        var dirB = MakeDir("pkgB");
        WriteApps(dirB, 1, namePrefix: "Extra");

        InvokeGetSharedReferences(new[] { dirA });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);

        InvokeGetSharedReferences(new[] { dirA, dirB });
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);

        // …and the enlarged set is itself memoised: repeating it does not rebuild again.
        InvokeGetSharedReferences(new[] { dirA, dirB });
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>
    /// A package added to an ALREADY-SCANNED dir also changes the reference set (same dir
    /// list, different contents) and must rebuild — the signature is computed from the
    /// scanned .app set, not merely from the dir names.
    /// </summary>
    [Fact]
    public void AddingAPackageToAScannedDir_ForcesARebuild()
    {
        var dir = MakeDir("pkg");
        WriteApps(dir, 2);

        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);

        // A second dir is needed for the scan-set change to reach the signature: with a
        // single dir and nothing to dedup/exclude, DeduplicateAppPackageDirs returns the dir
        // list unchanged by design (the documented zero-cost fast path). Two dirs with a
        // duplicate AppId stage a content-addressed dir, which is what carries the set.
        var dup = MakeDir("dup");
        var apps = ReadAppIds(dir);
        CopyApp(dir, dup, apps[0]);

        InvokeGetSharedReferences(new[] { dir, dup });
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    // ── EQUIVALENCE: hiding == deleting ───────────────────────────────────────────────

    /// <summary>
    /// The whole risk of the change in one assertion: for the compile that excludes app #0,
    /// the loader must answer for apps #1..#3 exactly as before and must NOT answer for #0 —
    /// and the answers must match, spec for spec, a loader built over a scan set from which
    /// #0's .app was physically deleted.
    /// </summary>
    [Fact]
    public void HidingTheSelfApp_AnswersIdenticallyToAPhysicallyReducedLoader()
    {
        var full = MakeDir("full");
        var apps = WriteApps(full, 4);
        var selfApp = apps[0];

        // Reference implementation: the pre-#1831 behaviour — a scan dir that simply does
        // not contain the self-app's .app.
        var reduced = MakeDir("reduced");
        foreach (var a in apps.Skip(1)) CopyApp(full, reduced, a.AppId);
        var reducedLoader = ReferenceLoaderFactory.CreateReferenceLoader(new[] { reduced });

        ISymbolReferenceLoader underTest;
        using (BcCompiler.ScopeCurrentAppIdentity(selfApp.AppId, selfApp.Publisher, selfApp.Version))
            underTest = InvokeGetSharedReferences(new[] { full }).Loader!;

        Assert.NotNull(underTest);

        foreach (var (appId, publisher, version) in apps)
        {
            var name = NameFor(appId);
            var spec = new SymbolReferenceSpecification(
                publisher, name, version, exact: false, appId, isPropagated: false,
                alternateIds: ImmutableArray<Guid>.Empty);

            var expected = reducedLoader.LoadModule(spec, new List<Diagnostic>());
            var actual = underTest.LoadModule(spec, new List<Diagnostic>());

            if (appId == selfApp.AppId)
            {
                Assert.Null(expected);           // control: deleting really does hide it
                Assert.Null(actual);             // …and so does the decorator
            }
            else
            {
                Assert.NotNull(expected);        // control: the survivor is reachable
                Assert.NotNull(actual);
                Assert.Equal(ModuleName(expected!), ModuleName(actual!));
            }
        }
    }

    /// <summary>
    /// The exclusion is scoped: the compile that follows, with no self-app scope, sees the
    /// excluded app again through the very same cached loader. A decorator that leaked into
    /// the cached loader would make every later compile blind to that package.
    /// </summary>
    [Fact]
    public void AfterTheSelfExcludingCompile_TheAppIsVisibleAgain()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);
        var selfApp = apps[0];
        var spec = new SymbolReferenceSpecification(
            selfApp.Publisher, NameFor(selfApp.AppId), selfApp.Version, exact: false,
            selfApp.AppId, isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);

        using (BcCompiler.ScopeCurrentAppIdentity(selfApp.AppId, selfApp.Publisher, selfApp.Version))
        {
            var hiding = InvokeGetSharedReferences(new[] { dir }).Loader!;
            Assert.Null(hiding.LoadModule(spec, new List<Diagnostic>()));
        }

        var plain = InvokeGetSharedReferences(new[] { dir }).Loader!;
        Assert.NotNull(plain.LoadModule(spec, new List<Diagnostic>()));
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>
    /// The self-app must also be absent from the requested SPEC list of its own compile —
    /// the pre-existing guard the loader-level exclusion complements. Asserted here so the
    /// restructure cannot quietly drop it.
    /// </summary>
    [Fact]
    public void SelfAppIsAbsentFromTheRequestedSpecs()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);
        var selfApp = apps[0];
        BcCompiler.SetResolvedDeps(
            apps.Select(a => (
                    new AppManifest(a.Publisher, NameFor(a.AppId), a.Version, a.AppId, new List<DependencyRef>()),
                    Path.Combine(dir, FileNameFor(a.AppId))))
                .ToList(),
            new[] { dir });

        using (BcCompiler.ScopeCurrentAppIdentity(selfApp.AppId, selfApp.Publisher, selfApp.Version))
        {
            var specs = InvokeGetSharedReferences(new[] { dir }).Specs;
            Assert.DoesNotContain(specs, s => s.AppId == selfApp.AppId);
            Assert.Equal(apps.Count - 1, specs.Count(s => apps.Any(a => a.AppId == s.AppId)));
        }
    }

    // ── The hide-vs-rescan decision function ──────────────────────────────────────────

    [Fact]
    public void CanHide_WhenTheExcludedAppsNameIsUniqueInTheScanSet()
    {
        var self = Guid.NewGuid();
        var inventory = new List<(Guid, string)>
        {
            (self, "System Application Test Library"),
            (Guid.NewGuid(), "Library Assert"),
            (Guid.NewGuid(), "Any"),
        };

        Assert.True(SelfExcludingSymbolReferenceLoader.CanHideInsteadOfRescan(inventory, self));
    }

    [Fact]
    public void CanHide_WhenTheSameAppIdIsPresentInSeveralVersions()
    {
        // All versions of the excluded AppId are hidden together, exactly as
        // DeduplicateAppPackageDirs' exclusion drops all of them — the repeated Name here
        // belongs to the SAME app and must not be mistaken for a collision.
        var self = Guid.NewGuid();
        var inventory = new List<(Guid, string)>
        {
            (self, "Any"),
            (self, "Any"),
            (Guid.NewGuid(), "Library Assert"),
        };

        Assert.True(SelfExcludingSymbolReferenceLoader.CanHideInsteadOfRescan(inventory, self));
    }

    [Fact]
    public void CannotHide_WhenAnotherAppIdDeclaresTheSameName()
    {
        // Deleting the excluded .app would promote the same-named survivor to winner for a
        // name-matched spec; hiding would answer "not found". Not equivalent → rescan.
        var self = Guid.NewGuid();
        var inventory = new List<(Guid, string)>
        {
            (self, "Any"),
            (Guid.NewGuid(), "any"),   // case-insensitive, as BC's own comparison is
        };

        Assert.False(SelfExcludingSymbolReferenceLoader.CanHideInsteadOfRescan(inventory, self));
    }

    [Fact]
    public void CannotHide_WhenAnEmptyAppIdIsInTheScanSet()
    {
        var self = Guid.NewGuid();
        var inventory = new List<(Guid, string)>
        {
            (self, "Any"),
            (Guid.Empty, "Something Else"),
        };

        Assert.False(SelfExcludingSymbolReferenceLoader.CanHideInsteadOfRescan(inventory, self));
    }

    [Fact]
    public void NameCollisionFallback_RebuildsAPhysicallyReducedLoader_AndStillHidesTheSelfApp()
    {
        // Two distinct AppIds sharing one Name: the decorator is not provably equivalent, so
        // the runner must fall back to the pre-#1831 physically-reduced loader (one extra
        // build) — and that loader must still hide the self-app while serving the other.
        var dir = MakeDir("pkg");
        var selfId = Guid.NewGuid();
        var twinId = Guid.NewGuid();
        WriteApp(dir, "Self.app", selfId, "Twin Name", "Microsoft", "28.2.0.0");
        WriteApp(dir, "Twin.app", twinId, "Twin Name", "Contoso", "28.2.0.0");

        using (BcCompiler.ScopeCurrentAppIdentity(selfId, "Microsoft", new Version(28, 2, 0, 0)))
        {
            var loader = InvokeGetSharedReferences(new[] { dir }).Loader!;

            var selfSpec = new SymbolReferenceSpecification(
                "Microsoft", "Twin Name", new Version(28, 2, 0, 0), exact: false, selfId,
                isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);
            var twinSpec = new SymbolReferenceSpecification(
                "Contoso", "Twin Name", new Version(28, 2, 0, 0), exact: false, twinId,
                isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);

            Assert.Null(loader.LoadModule(selfSpec, new List<Diagnostic>()));
            Assert.NotNull(loader.LoadModule(twinSpec, new List<Diagnostic>()));
        }

        // superset loader + the physically-reduced fallback.
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    // ── The .app metadata cache ───────────────────────────────────────────────────────

    /// <summary>
    /// The per-file (manifest, has-SymbolReference) cache must invalidate when the package
    /// is rewritten in place — a synthetic .app re-packaged mid-run, or a --watch rebuild.
    /// Asserting on the manifest CONTENT (the new AppId), not on a timing.
    /// </summary>
    [Fact]
    public void AppMetadataCache_InvalidatesWhenThePackageIsRewritten()
    {
        var dir = MakeDir("pkg");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var other = Guid.NewGuid();
        WriteApp(dir, "Mutating.app", firstId, "Mutating App", "Microsoft", "28.2.0.0");
        WriteApp(dir, "Stable.app", other, "Stable App", "Microsoft", "28.2.0.0");

        var before = InvokeDedupInventory(new List<string> { dir });
        Assert.Contains(before, e => e.AppId == firstId);

        // Rewrite in place with a different identity, forcing a new length + write time.
        File.Delete(Path.Combine(dir, "Mutating.app"));
        WriteApp(dir, "Mutating.app", secondId, "Mutating App Renamed Longer", "Microsoft", "28.2.0.0");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "Mutating.app"), DateTime.UtcNow.AddSeconds(5));

        var after = InvokeDedupInventory(new List<string> { dir });
        Assert.Contains(after, e => e.AppId == secondId);
        Assert.DoesNotContain(after, e => e.AppId == firstId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static (ISymbolReferenceLoader? Loader, SymbolReferenceSpecification[] Specs)
        InvokeGetSharedReferences(IEnumerable<string> bundleAlpackagesDirs)
    {
        var method = typeof(BcCompiler).GetMethod(
            "GetSharedReferences", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BcCompiler.GetSharedReferences not found by reflection.");
        var result = method.Invoke(null, new object?[] { bundleAlpackagesDirs })!;
        var t = result.GetType(); // ValueTuple: fields, not properties
        return ((ISymbolReferenceLoader?)t.GetField("Item1")!.GetValue(result),
                (SymbolReferenceSpecification[])t.GetField("Item2")!.GetValue(result)!);
    }

    private static List<(Guid AppId, string Name)> InvokeDedupInventory(List<string> dirs)
    {
        var method = typeof(BcCompiler).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 3);
        var args = new object?[] { dirs, null, null };
        method.Invoke(null, args);
        var inventory = (System.Collections.IEnumerable)args[2]!;
        var result = new List<(Guid, string)>();
        foreach (var e in inventory)
        {
            var t = e!.GetType();
            result.Add(((Guid)t.GetProperty("AppId")!.GetValue(e)!, (string)t.GetProperty("Name")!.GetValue(e)!));
        }
        return result;
    }

    private string MakeDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string ModuleName(ModuleDefinition m)
        => (string)(m.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?.GetValue(m) ?? "<no name>");

    private List<(Guid AppId, string Publisher, Version Version)> WriteApps(
        string dir, int count, string namePrefix = "Dep")
    {
        var apps = new List<(Guid, string, Version)>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            WriteApp(dir, $"{namePrefix}{i}.app", id, $"{namePrefix} App {i}", "Microsoft", "28.2.0.0");
            apps.Add((id, "Microsoft", new Version(28, 2, 0, 0)));
            _names[id] = $"{namePrefix} App {i}";
            _files[id] = $"{namePrefix}{i}.app";
        }
        return apps;
    }

    private readonly Dictionary<Guid, string> _names = new();
    private readonly Dictionary<Guid, string> _files = new();
    private string NameFor(Guid id) => _names[id];
    private string FileNameFor(Guid id) => _files[id];

    private void CopyApp(string fromDir, string toDir, Guid appId)
        => File.Copy(Path.Combine(fromDir, _files[appId]), Path.Combine(toDir, _files[appId]), overwrite: true);

    private static List<Guid> ReadAppIds(string dir)
        => Directory.EnumerateFiles(dir, "*.app")
            .Select(AppLoader.ReadManifest)
            .Where(m => m != null)
            .Select(m => m!.AppId)
            .ToList();

    /// <summary>
    /// Writes a REAL .app package — NAVX v2 header plus an OPC content part — using BC's own
    /// <see cref="NavAppPackageWriter"/>.
    ///
    /// The hand-rolled "8-byte header + plain ZIP" fixture used elsewhere in this test project
    /// is enough for <see cref="AppLoader.ReadManifest"/> (which just seeks the zip magic), but
    /// BC's LocalCacheSymbolReferenceLoader goes through NavAppPackageReader →
    /// NavAppPackage.Open → System.IO.Packaging, which needs the v2 metadata header and a
    /// [Content_Types].xml part. Since these tests assert that the decorator answers
    /// IDENTICALLY to BC's real loader, the fixture has to be readable by that real loader —
    /// a fake package would only prove the two loaders agree on "not found".
    /// </summary>
    private static void WriteApp(string dir, string fileName,
        Guid appId, string name, string publisher, string version)
    {
        var path = Path.Combine(dir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var writer = NavAppPackageWriter.Create(fs);
        writer.WriteString(ManifestXml(appId, name, publisher, version), "/NavxManifest.xml");
        writer.WriteString(SymbolReferenceJson(appId, name, publisher, version), "/SymbolReference.json");
    }

    private static string ManifestXml(Guid appId, string name, string publisher, string version)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}" CompatibilityId="0.0.0.0" Platform="28.0.0.0" Runtime="17.0" />
              <Dependencies />
              <InternalsVisibleTo />
            </Package>
            """;

    private static string SymbolReferenceJson(Guid appId, string name, string publisher, string version)
        => $$"""
            {"RuntimeVersion":"17.0","Namespaces":[],"Codeunits":[],"Reports":[],"XmlPorts":[],
             "Queries":[],"ControlAddIns":[],"EnumTypes":[],"DotNetPackages":[],"Interfaces":[],
             "PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],
             "InternalsVisibleToModules":[],
             "AppId":"{{appId}}","Name":"{{name}}","Publisher":"{{publisher}}","Version":"{{version}}"}
            """;
}
