// BcCompilerWarmLoaderReuseTests — REMOVING a resolved dep must not throw away the warmed
// symbol-reference loader; adding or changing one still must.
//
// Issue #1832
// -----------
// #1831 made the loader memo survive the per-compile SELF-EXCLUSION. It did not make it
// survive a change to the resolved dep LIST, which ComputeLoaderSignature folded in as `D:`
// lines and compared for EQUALITY. That mattered because
// `BcCompiler.ScopeSymbolBearingDepsOnly()` REMOVES entries from the dep list — the
// synthetic source-only .apps, which carry no SymbolReference.json — around every compile
// that inspects declaration diagnostics. Entering that scope changed the signature, the
// single memo slot missed, and the loader was rebuilt and re-warmed from scratch.
//
// Measured on a cold `tests/runner-extras` bundle (38 app groups) at main 71816f30:
// the `sibling-symbols` stage was 35.77 s, and 14.32 s of it was one such rebuild —
// `GetSharedReferences` inside the FIRST sibling's EmitDepSymbols, triggered purely by the
// scope dropping a single synthetic .app from the dep list. The scan dirs were byte-identical
// either side of it; the sigdiff was one `D:` line.
//
// The fix compares the dep list as a SUBSET instead: the loader is reused when the current
// dep set is contained in the one it was built for. Removal is free; anything that adds or
// changes a key rebuilds exactly as before.
//
// Why "adds or changes" must keep rebuilding
// ------------------------------------------
// A source dependency recompiled from edited AL is republished under a NEW content-addressed
// path (`~/.cache/al-runner/workspace-deps/<hash>/…`). That new `AppPath@Version` key is the
// ONLY thing that tells the runner the cached loader's indexed symbols are stale — the
// scanned dirs do not change, because the synthetic .app carries no SymbolReference.json and
// is filtered out of the .app scan set entirely, reaching the compile through the JSON
// symbol loaders instead, which index at construction. Dropping the dep list from the
// invalidation rule altogether made `scripts/tests/server-mode-test.sh` assertions 2+3 fail:
// after a dep schema edit the main bundle compiled green against the OLD schema and died at
// runtime (exitCode 1) instead of failing loudly at compile time (exitCode 3).
//
// What these tests pin
// --------------------
//  * POSITIVE  — narrowing (and restoring) the dep set builds the loader exactly ONCE and
//                performs exactly ZERO additional warm work. Counts, never durations.
//  * NEGATIVE  — an ADDED dep rebuilds and re-warms; a dep REPUBLISHED AT A NEW PATH (the
//                server-mode regression above, in unit form) rebuilds; a changed scan-dir
//                set rebuilds. Without these a memo that never invalidates would pass the
//                positive tests.
//  * EQUIVALENCE — the reused loader answers, module for module, exactly what BC's own
//                ReferenceLoaderFactory produces for the narrowed configuration; and the
//                requested SPEC list still loses the dropped dep, so reusing the loader
//                cannot smuggle it back into the compile.
//  * The dep-set narrowing itself (ScopeSymbolBearingDepsOnly) keeps its meaning after being
//    routed through the cached .app metadata reader, including re-reading a package that is
//    rewritten in place.

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
/// Drives BcCompiler's process-wide loader statics; shares the serialised collection with
/// <see cref="BcCompilerSharedReferenceMemoTests"/>.
/// </summary>
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class BcCompilerWarmLoaderReuseTests : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<Guid, string> _names = new();
    private readonly Dictionary<Guid, string> _files = new();

    public BcCompilerWarmLoaderReuseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-warm-reuse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BcCompiler.ResetSharedReferencesForTests();
    }

    public void Dispose()
    {
        BcCompiler.ResetSharedReferencesForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── POSITIVE: narrowing the dep set costs nothing ─────────────────────────────────

    /// <summary>
    /// The exact shape of <c>ScopeSymbolBearingDepsOnly</c>: same package dirs, one dep
    /// dropped from the resolved list. Before the fix this was a full rebuild plus a full
    /// re-warm; now it must be neither.
    /// </summary>
    [Fact]
    public void NarrowingTheResolvedDepSet_ReusesTheLoaderAndDoesNoNewWarmWork()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);

        SetDeps(dir, apps);
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        // WarmedSpecs(n) below: one walk per resolved dep plus the single implicit platform
        // module (`Microsoft|System|28.0.0.0`) BC's GetDependencies returns for each of them
        // and the walk dedups. Asserting a concrete count, not ">0", is what makes the
        // "unchanged afterwards" assertion mean something.
        Assert.Equal(WarmedSpecs(3), BcCompiler.ReferenceLoaderWarmSpecCount);

        SetDeps(dir, apps.Take(2).ToList());
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(WarmedSpecs(3), BcCompiler.ReferenceLoaderWarmSpecCount);
    }

    /// <summary>
    /// …and the scope's Dispose, which puts the dropped dep back. Before the fix this was a
    /// SECOND rebuild: the widened signature no longer matched the narrowed one either.
    /// </summary>
    [Fact]
    public void RestoringTheFullDepSetAfterNarrowing_StillReusesTheLoader()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);

        SetDeps(dir, apps);
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, apps.Take(2).ToList());     // scope enter
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, apps);                      // scope dispose
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(WarmedSpecs(3), BcCompiler.ReferenceLoaderWarmSpecCount);
    }

    // ── NEGATIVE: adding or changing a dep must still rebuild ─────────────────────────

    /// <summary>
    /// A dep the loader was NOT built for must still force a rebuild — the reuse rule is
    /// subset, not "any dep set will do". This is the assertion that stops the fix
    /// degenerating into a memo that never invalidates.
    /// </summary>
    [Fact]
    public void AddingAResolvedDep_StillRebuildsAndRewarms()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);

        SetDeps(dir, apps.Take(2).ToList());
        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(WarmedSpecs(2), BcCompiler.ReferenceLoaderWarmSpecCount);

        SetDeps(dir, apps);
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
        // The rebuilt instance starts cold — BC's MemoryCachedSymbolReferenceLoader caches
        // in instance fields — so its whole closure is walked again on top of the first's.
        Assert.Equal(WarmedSpecs(2) + WarmedSpecs(3), BcCompiler.ReferenceLoaderWarmSpecCount);

        // …and the widened set is itself reusable: repeating it does not rebuild again.
        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>
    /// The `scripts/tests/server-mode-test.sh` regression in unit form. A source dependency
    /// recompiled from edited AL keeps its AppId, Name and Version but is republished under a
    /// NEW content-addressed path. Nothing else moves — the scanned .app dirs are unchanged,
    /// because a synthetic source-only package carries no SymbolReference.json and is
    /// filtered out of the .app scan set entirely. The changed dep KEY is therefore the only
    /// signal that the cached loader's symbols are stale, and it must rebuild: otherwise the
    /// dependent app compiles green against the OLD schema and fails at runtime instead of
    /// failing loudly at compile time.
    /// </summary>
    [Fact]
    public void ASymbolLessDepRepublishedAtANewPath_RebuildsTheLoader_EvenThoughTheScanSetIsIdentical()
    {
        // A stable .app scan set the loader is really built from…
        var pkg = MakeDir("pkg");
        var platform = WriteApps(pkg, 2);

        // …plus a synthetic source-only dep, symbol-less, in a content-addressed dir.
        var v1 = MakeDir("workspace-deps/9c31b7ece106");
        var dep = WriteSymbolLessApp(v1, "AL_Runner_Src_Dep_1_0_0_0.app", "Src Dep");

        BcCompiler.SetResolvedDeps(
            platform.Append(dep).Select(a => (ManifestFor(a), PathFor(a))).ToList(),
            new[] { pkg, v1 });
        InvokeGetSharedReferences(new[] { pkg });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);

        // Republish the same identity under a new content-addressed dir — exactly what the
        // layered pre-pass does when the dep's AL changes.
        var v2 = MakeDir("workspace-deps/d750c87b300c");
        var republished = Path.Combine(v2, "AL_Runner_Src_Dep_1_0_0_0.app");
        File.Copy(_files[dep.AppId], republished);
        _files[dep.AppId] = republished;

        BcCompiler.SetResolvedDeps(
            platform.Append(dep).Select(a => (ManifestFor(a), PathFor(a))).ToList(),
            new[] { pkg, v2 });

        // Control: the .app SCAN set really is unchanged — a symbol-less package is filtered
        // out of it, so the dedup staging key (the picked-app set) is identical either side.
        // Without the dep key in the rule, nothing here would invalidate the loader.
        Assert.Equal(
            InvokeDedupScanSet(new List<string> { pkg, v1 }),
            InvokeDedupScanSet(new List<string> { pkg, v2 }));

        InvokeGetSharedReferences(new[] { pkg });
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>
    /// A genuinely different scan set must still rebuild. Moving the dep list out of the
    /// signature must not take the package dirs with it.
    /// </summary>
    [Fact]
    public void AddingAPackageDir_StillRebuildsAndRewarms()
    {
        var dirA = MakeDir("pkgA");
        var appsA = WriteApps(dirA, 2);
        var dirB = MakeDir("pkgB");
        var appsB = WriteApps(dirB, 1, namePrefix: "Extra");

        SetDeps(dirA, appsA);
        InvokeGetSharedReferences(new[] { dirA });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(WarmedSpecs(2), BcCompiler.ReferenceLoaderWarmSpecCount);

        BcCompiler.SetResolvedDeps(
            appsA.Concat(appsB).Select(a => (ManifestFor(a), PathFor(a))).ToList(),
            new[] { dirA, dirB });
        InvokeGetSharedReferences(new[] { dirA, dirB });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
        // The rebuilt instance starts cold, so its whole dep closure is walked again on top
        // of the first loader's: WarmedSpecs(2) + WarmedSpecs(3) = 3 + 4 = 7. A fresh loader
        // silently inheriting the previous instance's warm set would be a real bug — BC's
        // MemoryCachedSymbolReferenceLoader caches per instance.
        Assert.Equal(WarmedSpecs(2) + WarmedSpecs(3), BcCompiler.ReferenceLoaderWarmSpecCount);
    }

    // ── EQUIVALENCE: the reused loader is the loader the old code would have built ─────

    /// <summary>
    /// The risk of the change in one assertion. After a dep-set narrowing the runner now
    /// hands the compile a loader it built for the WIDER dep set; the pre-#1832 code built a
    /// fresh one. Both are constructed from the same scan dirs, so BC's own
    /// <see cref="ReferenceLoaderFactory"/> over those dirs is the reference implementation —
    /// and the two must answer identically, module for module.
    /// </summary>
    [Fact]
    public void LoaderReusedAcrossADepSetNarrowing_AnswersIdenticallyToAFreshBcLoader()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 4);

        SetDeps(dir, apps);
        InvokeGetSharedReferences(new[] { dir });

        SetDeps(dir, apps.Take(2).ToList());
        var reused = InvokeGetSharedReferences(new[] { dir }).Loader!;
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);   // it really is the reused one

        // What the pre-#1832 rebuild would have produced for the narrowed configuration.
        var reference = ReferenceLoaderFactory.CreateReferenceLoader(new[] { dir });

        foreach (var app in apps)
        {
            var spec = SpecFor(app);
            var expected = reference.LoadModule(spec, new List<Diagnostic>());
            var actual = reused.LoadModule(spec, new List<Diagnostic>());

            // Every package in the scan set stays reachable — the dep list never governed
            // loader reachability, only which specs the compile requests.
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(ModuleName(expected!), ModuleName(actual!));
        }

        // …and a package in neither is not answered for by either.
        var absent = new SymbolReferenceSpecification(
            "Microsoft", "Never Written", new Version(28, 2, 0, 0), exact: false,
            Guid.NewGuid(), isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);
        Assert.Null(reference.LoadModule(absent, new List<Diagnostic>()));
        Assert.Null(reused.LoadModule(absent, new List<Diagnostic>()));
    }

    /// <summary>
    /// Reusing the loader must not smuggle the dropped dep back into the COMPILE. The
    /// requested spec list is the compile's actual reference set, and it is recomputed from
    /// the (narrowed) dep list every call — assert that it really shrinks.
    /// </summary>
    [Fact]
    public void NarrowingTheResolvedDepSet_RemovesTheDroppedDepFromTheRequestedSpecs()
    {
        var dir = MakeDir("pkg");
        var apps = WriteApps(dir, 3);
        var dropped = apps[2];

        SetDeps(dir, apps);
        var before = InvokeGetSharedReferences(new[] { dir }).Specs;
        Assert.Equal(3, apps.Count(a => before.Any(s => s.AppId == a.AppId)));

        SetDeps(dir, apps.Take(2).ToList());
        var after = InvokeGetSharedReferences(new[] { dir }).Specs;

        Assert.DoesNotContain(after, s => s.AppId == dropped.AppId);
        Assert.Equal(2, apps.Count(a => after.Any(s => s.AppId == a.AppId)));
    }

    // ── ScopeSymbolBearingDepsOnly through the cached .app metadata reader ────────────

    /// <summary>
    /// The scope's actual contract: a resolved dep whose .app carries no
    /// SymbolReference.json leaves the requested spec list for the scope's duration, and
    /// every symbol-bearing dep stays. Positive and negative in one test, either side of
    /// Dispose.
    /// </summary>
    [Fact]
    public void ScopeSymbolBearingDepsOnly_DropsOnlyTheSymbolLessPackage_AndRestoresItOnDispose()
    {
        var dir = MakeDir("pkg");
        var withSymbols = WriteApps(dir, 1)[0];
        var symbolLess = WriteSymbolLessApp(dir, "Synthetic.app", "Synthetic Source Dep");

        SetDeps(dir, new List<AppFixture> { withSymbols, symbolLess });

        using (BcCompiler.ScopeSymbolBearingDepsOnly())
        {
            var scoped = InvokeGetSharedReferences(new[] { dir }).Specs;
            Assert.Contains(scoped, s => s.AppId == withSymbols.AppId);
            Assert.DoesNotContain(scoped, s => s.AppId == symbolLess.AppId);
        }

        var restored = InvokeGetSharedReferences(new[] { dir }).Specs;
        Assert.Contains(restored, s => s.AppId == withSymbols.AppId);
        Assert.Contains(restored, s => s.AppId == symbolLess.AppId);
    }

    /// <summary>
    /// The scope now answers from the per-file (path + length + last-write-ticks) metadata
    /// cache instead of unzipping every resolved dep's whole package on each entry. The cache
    /// must not go stale: a package re-packaged in place — InProcessAppPackager mid-run, a
    /// --watch rebuild — that GAINS a SymbolReference.json must stop being dropped.
    /// </summary>
    [Fact]
    public void ScopeSymbolBearingDepsOnly_ReReadsAPackageRewrittenInPlace()
    {
        var dir = MakeDir("pkg");
        var stable = WriteApps(dir, 1)[0];
        var mutating = WriteSymbolLessApp(dir, "Mutating.app", "Mutating Dep");

        SetDeps(dir, new List<AppFixture> { stable, mutating });

        using (BcCompiler.ScopeSymbolBearingDepsOnly())
            Assert.DoesNotContain(
                InvokeGetSharedReferences(new[] { dir }).Specs, s => s.AppId == mutating.AppId);

        // Same path, same AppId, now WITH a SymbolReference.json.
        var path = Path.Combine(dir, "Mutating.app");
        File.Delete(path);
        WriteApp(dir, "Mutating.app", mutating.AppId, mutating.Name, mutating.Publisher, "28.2.0.0");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        using (BcCompiler.ScopeSymbolBearingDepsOnly())
            Assert.Contains(
                InvokeGetSharedReferences(new[] { dir }).Specs, s => s.AppId == mutating.AppId);
    }


    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private sealed record AppFixture(Guid AppId, string Name, string Publisher, Version Version);

    /// <summary>
    /// Specs WarmReferenceLoader walks for <paramref name="depCount"/> fixture deps: one per
    /// dep, plus the one implicit platform module (`Microsoft|System|28.0.0.0`, from the
    /// fixtures' <c>Platform="28.0.0.0"</c>) that BC returns as a dependency of every one of
    /// them and the walk's dedup collapses to a single entry.
    /// </summary>
    private static int WarmedSpecs(int depCount) => depCount + 1;

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

    /// <summary>
    /// The .app scan-dir list DeduplicateAppPackageDirs produces — what the loader is
    /// actually constructed from, and what ComputeLoaderSignature keys on.
    /// </summary>
    private static string InvokeDedupScanSet(List<string> dirs)
    {
        var method = typeof(BcCompiler).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 3);
        var args = new object?[] { dirs, null, null };
        var result = (List<string>)method.Invoke(null, args)!;
        return string.Join("\n", result);
    }

    private void SetDeps(string dir, IReadOnlyList<AppFixture> apps)
        => BcCompiler.SetResolvedDeps(
            apps.Select(a => (ManifestFor(a), PathFor(a))).ToList(), new[] { dir });

    private AppManifest ManifestFor(AppFixture a)
        => new(a.Publisher, a.Name, a.Version, a.AppId, new List<DependencyRef>());

    private string PathFor(AppFixture a) => _files[a.AppId];

    private static SymbolReferenceSpecification SpecFor(AppFixture a)
        => new(a.Publisher, a.Name, a.Version, exact: false, a.AppId,
               isPropagated: false, alternateIds: ImmutableArray<Guid>.Empty);

    private static string ModuleName(ModuleDefinition m)
        => (string)(m.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     ?.GetValue(m) ?? "<no name>");

    private string MakeDir(string name)
    {
        var d = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(d);
        return d;
    }

    private List<AppFixture> WriteApps(string dir, int count, string namePrefix = "Dep")
    {
        var apps = new List<AppFixture>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            var name = $"{namePrefix} App {i}";
            var file = $"{namePrefix}{i}_{id:N}.app";
            WriteApp(dir, file, id, name, "Microsoft", "28.2.0.0");
            _names[id] = name;
            _files[id] = Path.Combine(dir, file);
            apps.Add(new AppFixture(id, name, "Microsoft", new Version(28, 2, 0, 0)));
        }
        return apps;
    }

    /// <summary>
    /// A synthetic source-only package: a real NAVX with a manifest but NO
    /// SymbolReference.json — exactly what InProcessAppPackager writes and what
    /// ScopeSymbolBearingDepsOnly exists to drop.
    /// </summary>
    private AppFixture WriteSymbolLessApp(string dir, string fileName, string name)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(dir, fileName);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        using (var writer = NavAppPackageWriter.Create(fs))
            writer.WriteString(ManifestXml(id, name, "Microsoft", "28.2.0.0"), "/NavxManifest.xml");
        _names[id] = name;
        _files[id] = path;
        return new AppFixture(id, name, "Microsoft", new Version(28, 2, 0, 0));
    }

    /// <summary>
    /// A REAL .app package (NAVX v2 header + OPC content part) written with BC's own
    /// <see cref="NavAppPackageWriter"/> — the equivalence test compares against BC's real
    /// loader, so the fixture has to be readable by it or the comparison would only prove
    /// that both loaders agree on "not found". Same rationale as
    /// BcCompilerSharedReferenceMemoTests.WriteApp.
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
