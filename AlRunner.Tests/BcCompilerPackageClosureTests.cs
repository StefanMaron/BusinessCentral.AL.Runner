// BcCompilerPackageClosureTests - #5472.
//
// BC's AbstractSymbolReferenceAnalyzer reads every *.app manifest of the scan set on each
// reference lookup through a 100-entry LRU. Past 100 packages the walk evicts each entry before
// it comes round again, so every lookup re-reads (and, for a Ready-to-Run package, re-copies)
// every package. The runner used to hand BC the whole artifact folder. Past the bound it now
// hands BC only the dependency closure of the run, as a real .alpackages would hold.
//
// What these pin: which packages the loader is built over (never wall-clock), in both
// directions of the bound, and that the choice among versions BC makes is not changed by it.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Packaging;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class BcCompilerPackageClosureTests : IDisposable
{
    // BC's manifestCache capacity (CodeAnalysis 28.1.49838.53910, AbstractSymbolReferenceAnalyzer..ctor: ldc.i4.s 100).
    private const int BcManifestCacheCapacity = 100;

    private readonly string _root;
    private readonly Dictionary<string, Pkg> _byName = new();

    private sealed record Pkg(Guid AppId, string Name, string Publisher, string Version, string Path);

    public BcCompilerPackageClosureTests()
    {
        _root = TestScratch.Dir("al-runner-package-closure");
        Directory.CreateDirectory(_root);
        BcCompiler.ResetSharedReferencesForTests();
    }

    public void Dispose()
    {
        BcCompiler.ResetSharedReferencesForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── POSITIVE: above the bound the loader sees the closure and nothing else ─────────

    [Fact]
    public void AboveTheManifestCacheBound_TheLoaderIsBuiltOverTheClosureOnly()
    {
        var dir = MakeDir("cache");
        var closure = WriteClosure(dir);
        var noise = WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        var staged = Assert.Single(BcCompiler.LoaderScanDirs);
        Assert.NotEqual(dir, staged);
        var names = StagedNames(staged);
        // Declared dependency, its transitive dependency, BOTH versions of it (BC picks the
        // highest satisfying one, so dropping the other could change the answer), and the two
        // floors by name, with what the Application floor itself depends on.
        Assert.Equal(closure.OrderBy(x => x, StringComparer.Ordinal), names.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(names.Count <= BcManifestCacheCapacity);
        // Negative, per package: not one unrelated package got through.
        Assert.DoesNotContain(names, n => n.Contains("_Noise ", StringComparison.Ordinal));
        Assert.Equal(noise, BcCompiler.ScannedPackagesForTdd().Count(e => e.Name.StartsWith("Noise", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheTddInventory_StillListsEveryPackage()
    {
        var dir = MakeDir("cache");
        var closure = WriteClosure(dir);
        var noise = WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        // --tdd asks about packages the compile does not reference (#5446), so it must not
        // be narrowed to the ones the loader was built over.
        Assert.Equal(closure.Count + noise, BcCompiler.ScannedPackagesForTdd().Count);
    }

    // ── NEGATIVE: at or below the bound nothing changes ────────────────────────────────

    [Fact]
    public void AtTheManifestCacheBound_TheScanSetIsExactlyWhatItWasBefore()
    {
        var dir = MakeDir("cache");
        var closure = WriteClosure(dir);
        WriteNoise(dir, BcManifestCacheCapacity - closure.Count);   // exactly 100 packages

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(BcManifestCacheCapacity, Directory.GetFiles(dir, "*.app").Length);
        Assert.Equal(new[] { dir }, BcCompiler.LoaderScanDirs);
    }

    [Fact]
    public void OnePastTheManifestCacheBound_TheClosureIsStaged()
    {
        var dir = MakeDir("cache");
        var closure = WriteClosure(dir);
        WriteNoise(dir, BcManifestCacheCapacity + 1 - closure.Count);   // exactly 101 packages

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(BcManifestCacheCapacity + 1, Directory.GetFiles(dir, "*.app").Length);
        var staged = Assert.Single(BcCompiler.LoaderScanDirs);
        Assert.Equal(closure.Count, StagedNames(staged).Count);
    }

    // ── The closure only grows: a later bundle never shrinks the loader's world ────────

    [Fact]
    public void AClosureOfAnEarlierBundle_StaysStagedForALaterOne()
    {
        var dir = MakeDir("cache");
        var closure = WriteClosure(dir);
        var other = WriteApps(dir, "Other", 1);
        WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });
        var first = Assert.Single(BcCompiler.LoaderScanDirs);
        Assert.Equal(closure.Count, StagedNames(first).Count);

        // A second bundle with a different root: the closure grows, so the scan set does too.
        SetDeps(dir, "Other 0");
        InvokeGetSharedReferences(new[] { dir });
        var grown = Assert.Single(BcCompiler.LoaderScanDirs);
        Assert.NotEqual(first, grown);
        Assert.Equal(closure.Count + other.Count, StagedNames(grown).Count);

        // Back to the first bundle's roots: the scan set stays the grown one, not the smaller
        // closure of the bundle in hand, so a bundle whose closure is inside an earlier one's is
        // served by the same scan set.
        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(grown, Assert.Single(BcCompiler.LoaderScanDirs));
    }

    // ── EQUIVALENCE: BC's choice among versions is the one it made over the full folder ─

    [Fact]
    public void TheLoaderOverTheClosure_AnswersWhatBcAnswersOverTheWholeFolder()
    {
        var dir = MakeDir("cache");
        WriteClosure(dir);
        WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        var ours = InvokeGetSharedReferences(new[] { dir }).Loader!;
        Assert.NotEqual(dir, Assert.Single(BcCompiler.LoaderScanDirs));
        var reference = ReferenceLoaderFactory.CreateReferenceLoader(new[] { dir });

        foreach (var name in new[] { "Root", "Mid", "Leaf", "Application", "System", "Base Application" })
        {
            var spec = SpecFor(_byName[name]);
            var expected = reference.LoadModule(spec, new List<Diagnostic>());
            var actual = ours.LoadModule(spec, new List<Diagnostic>());
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(VersionOf(expected!), VersionOf(actual!));
        }
        // Mid ships in two versions; the highest satisfying one wins either way.
        Assert.Equal("2.0.0.0", VersionOf(ours.LoadModule(SpecFor(_byName["Mid"]), new List<Diagnostic>())!));
    }

    // ── The self-exclusion rescan (an app compiled from its own source) narrows the same way ──

    [Fact]
    public void ThePhysicallyExcludedScan_IsNarrowedToTheClosureMinusTheExcludedApp()
    {
        var dir = MakeDir("cache");
        WriteClosure(dir);
        WriteNoise(dir, BcManifestCacheCapacity + 30);
        var seeds = new[] { new BcCompiler.ClosureSeed(_byName["Root"].AppId, "Root") };

        var method = typeof(BcCompiler).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 5);
        var args = new object?[] { new List<string> { dir }, (Guid?)_byName["Leaf"].AppId, seeds, null, null };
        var staged = (List<string>)method.Invoke(null, args)!;

        var names = StagedNames(Assert.Single(staged));
        Assert.DoesNotContain(names, n => n.Contains("_Leaf_", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("_Mid_2.0.0.0", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("_Noise ", StringComparison.Ordinal));
        Assert.Equal(6, names.Count);
    }

    [Fact]
    public void TheRescanForADependencyCompiledFromSource_ServesTheClosureAndNothingElse()
    {
        var dir = MakeDir("cache");
        WriteClosure(dir);
        WriteNoise(dir, BcManifestCacheCapacity + 30);
        var mid = _byName["Mid"];
        // Another publisher's "Mid": a survivor sharing the excluded app's name, which is what
        // makes hiding the excluded app unsafe and sends the compile to a rescan of its own.
        WritePkg(dir, "Mid", "Fabrikam", "1.0.0.0");
        _byName["Mid"] = mid;

        SetDeps(dir, "Root");   // Leaf is reachable only through the excluded Mid: its manifest must still route the closure
        using var _ = BcCompiler.ScopeCurrentAppIdentity(mid.AppId, mid.Publisher, System.Version.Parse(mid.Version));
        var loader = InvokeGetSharedReferences(new[] { dir }).Loader!;

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);   // the shared loader, then the rescan
        Assert.NotNull(loader.LoadModule(SpecFor(_byName["Leaf"]), new List<Diagnostic>()));
        Assert.Null(loader.LoadModule(SpecFor(_byName["Noise 7"]), new List<Diagnostic>()));
    }

    // ── Each arm of the closure walk, pinned on its own ────────────────────────────────

    /// <summary>The identity BC can ask for under an old name: the dependency entry carries the
    /// AppId and a Name the package no longer has. BC's IsSatisfiedBy accepts it on the AppId, and
    /// FindMatchingPackageFile takes the highest satisfying version, so that package must be staged.</summary>
    [Fact]
    public void ADependencyNamedUnderAnOldName_StagesThePackageByItsAppId()
    {
        var dir = MakeDir("cache");
        var id = Guid.NewGuid();
        WritePkg(dir, "Renamed Now", "Contoso", "2.0.0.0", appId: id);
        WritePkg(dir, "Root", "Contoso", "1.0.0.0",
            deps: new[] { new Pkg(id, "Called This Before", "Contoso", "1.0.0.0", "") });
        WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        var names = StagedNames(Assert.Single(BcCompiler.LoaderScanDirs));
        Assert.Equal(new[] { "Contoso_Renamed Now_2.0.0.0.app", "Contoso_Root_1.0.0.0.app" }, names.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>A dependency entry without an AppId is matched on its name, and BC compares names
    /// ignoring case (IsSatisfiedBy: StringComparison.OrdinalIgnoreCase).</summary>
    [Fact]
    public void ADependencyWithoutAnAppId_IsMatchedOnItsNameIgnoringCase()
    {
        var dir = MakeDir("cache");
        WritePkg(dir, "Leaf", "Contoso", "1.0.0.0");
        WritePkg(dir, "Root", "Contoso", "1.0.0.0",
            deps: new[] { new Pkg(Guid.Empty, "lEAF", "Contoso", "1.0.0.0", "") });
        WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        var names = StagedNames(Assert.Single(BcCompiler.LoaderScanDirs));
        Assert.Equal(new[] { "Contoso_Leaf_1.0.0.0.app", "Contoso_Root_1.0.0.0.app" }, names.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>The Application / Platform floors of a package reached through a dependency, not
    /// only of a seed: nothing else in this scan set names Application or System.</summary>
    [Fact]
    public void TheFloorsOfAPackageReachedThroughADependency_AreFollowed()
    {
        var dir = MakeDir("cache");
        WritePkg(dir, "Base Application", "Microsoft", "28.0.0.0");
        WritePkg(dir, "Application", "Microsoft", "28.0.0.0", deps: new[] { _byName["Base Application"] });
        WritePkg(dir, "System", "Microsoft", "28.0.0.0");
        var mid = WritePkg(dir, "Mid", "Contoso", "1.0.0.0", floors: true);
        WritePkg(dir, "Root", "Contoso", "1.0.0.0", deps: new[] { mid });   // Root itself declares no floor
        WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });

        var names = StagedNames(Assert.Single(BcCompiler.LoaderScanDirs));
        Assert.Equal(5, names.Count);
        Assert.Contains("Microsoft_Application_28.0.0.0.app", names);
        Assert.Contains("Microsoft_System_28.0.0.0.app", names);
    }

    /// <summary>Seeds remembered from an earlier bundle must not narrow a call that resolved no
    /// dependencies: that path (the --precompile fallback) needs every package in the scan set.</summary>
    [Fact]
    public void ACallWithNoResolvedDependencies_IsNotNarrowedBySeedsOfAnEarlierOne()
    {
        var dir = MakeDir("cache");
        WriteClosure(dir);
        WriteNoise(dir, BcManifestCacheCapacity + 30);

        SetDeps(dir, "Root");
        InvokeGetSharedReferences(new[] { dir });
        Assert.NotEqual(dir, Assert.Single(BcCompiler.LoaderScanDirs));

        BcCompiler.SetResolvedDeps(new List<(AppManifest, string)>(), new[] { dir });
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(new[] { dir }, BcCompiler.LoaderScanDirs);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>Root -> Mid (two versions) -> Leaf by declared dependency, plus Application (which
    /// depends on Base Application) and System as the floors of Root. Returns the file names.</summary>
    private List<string> WriteClosure(string dir)
    {
        var leaf = WritePkg(dir, "Leaf", "Contoso", "1.0.0.0");
        var mid1 = WritePkg(dir, "Mid", "Contoso", "1.0.0.0", deps: new[] { leaf });
        var mid2 = WritePkg(dir, "Mid", "Contoso", "2.0.0.0", deps: new[] { leaf });
        var baseApp = WritePkg(dir, "Base Application", "Microsoft", "28.0.0.0");
        WritePkg(dir, "Application", "Microsoft", "28.0.0.0", deps: new[] { baseApp });
        WritePkg(dir, "System", "Microsoft", "28.0.0.0");
        WritePkg(dir, "Root", "Contoso", "1.0.0.0", deps: new[] { mid1 }, floors: true);
        return Directory.GetFiles(dir, "*.app").Select(Path.GetFileName).Select(f => f!).ToList();
    }

    private int WriteNoise(string dir, int count) => WriteApps(dir, "Noise", count).Count;

    private List<string> WriteApps(string dir, string prefix, int count)
    {
        var files = new List<string>();
        for (var i = 0; i < count; i++)
            files.Add(Path.GetFileName(WritePkg(dir, $"{prefix} {i}", "Contoso", "1.0.0.0").Path));
        return files;
    }

    private Pkg WritePkg(string dir, string name, string publisher, string version,
        Pkg[]? deps = null, bool floors = false, Guid? appId = null)
    {
        // A second version of the same app keeps its AppId, as a real package cache does.
        var id = appId ?? (_byName.TryGetValue(name, out var prev) && prev.Publisher == publisher ? prev.AppId : Guid.NewGuid());
        var path = Path.Combine(dir, $"{publisher}_{name}_{version}.app");
        var depXml = string.Join("", (deps ?? Array.Empty<Pkg>()).Select(d =>
            $"""<Dependency Id="{d.AppId}" Name="{d.Name}" Publisher="{d.Publisher}" MinVersion="{d.Version}" />"""));
        var floorAttrs = floors ? """ Application="28.0.0.0" Platform="28.0.0.0" """ : "";
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{id}" Name="{name}" Publisher="{publisher}" Version="{version}" CompatibilityId="0.0.0.0"{floorAttrs} Runtime="17.0" />
              <Dependencies>{depXml}</Dependencies>
              <InternalsVisibleTo />
            </Package>
            """;
        var symbols = $$"""
            {"RuntimeVersion":"17.0","Namespaces":[],"Codeunits":[],"Reports":[],"XmlPorts":[],
             "Queries":[],"ControlAddIns":[],"EnumTypes":[],"DotNetPackages":[],"Interfaces":[],
             "PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],
             "InternalsVisibleToModules":[],
             "AppId":"{{id}}","Name":"{{name}}","Publisher":"{{publisher}}","Version":"{{version}}"}
            """;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        using (var writer = NavAppPackageWriter.Create(fs))
        {
            writer.WriteString(manifest, "/NavxManifest.xml");
            writer.WriteString(symbols, "/SymbolReference.json");
        }
        var pkg = new Pkg(id, name, publisher, version, path);
        _byName[name] = pkg;   // the later of two versions wins the lookup; tests only read Mid's version via the loader
        return pkg;
    }

    private void SetDeps(string dir, params string[] names)
    {
        var deps = names.Select(n => _byName[n])
            .Select(p => (new AppManifest(p.Publisher, p.Name, System.Version.Parse(p.Version), p.AppId,
                new List<DependencyRef>()), p.Path))
            .ToList();
        BcCompiler.SetResolvedDeps(deps, new[] { dir });
    }

    private static List<string> StagedNames(string stage)
        => Directory.GetFiles(stage, "*.app").Select(f => Path.GetFileName(f)!).ToList();

    private static SymbolReferenceSpecification SpecFor(Pkg p)
        => new(p.Publisher, p.Name, System.Version.Parse("1.0.0.0"), exact: false, p.AppId,
               isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);

    private static string VersionOf(ModuleDefinition m)
        => m.GetType().GetProperty("Version", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(m)?.ToString() ?? "<no version>";

    private static (ISymbolReferenceLoader? Loader, SymbolReferenceSpecification[] Specs)
        InvokeGetSharedReferences(IEnumerable<string> dirs)
    {
        var method = typeof(BcCompiler).GetMethod("GetSharedReferences", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BcCompiler.GetSharedReferences not found by reflection.");
        var result = method.Invoke(null, new object?[] { dirs })!;
        var t = result.GetType();
        return ((ISymbolReferenceLoader?)t.GetField("Item1")!.GetValue(result),
                (SymbolReferenceSpecification[])t.GetField("Item2")!.GetValue(result)!);
    }

    private string MakeDir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }
}
