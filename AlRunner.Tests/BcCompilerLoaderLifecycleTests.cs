// BcCompilerLoaderLifecycleTests - #5473.
//
// A bundle that only ADDS a dependency over the same scanned packages used to rebuild the package
// loader, and every rebuild parses Base Application's symbols again. These tests pin, by counting
// loader builds and warmed specs (never wall-clock), that
//   - such a bundle reuses the loader and warms only the specs that are new on it,
//   - what makes a reused loader wrong still rebuilds it (a package that moved, changed, appeared
//     or was rewritten since the build; another scan set), and
//   - a reused loader answers, version for version, what a loader built for each bundle alone
//     (its own closure, or the whole folder) answers.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Packaging;
using Microsoft.Dynamics.Nav.CodeAnalysis.SymbolReference;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class BcCompilerLoaderLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, Pkg> _byName = new();

    private sealed record Pkg(Guid AppId, string Name, string Publisher, string Version, string Path);

    public BcCompilerLoaderLifecycleTests()
    {
        _root = TestScratch.Dir("al-runner-loader-lifecycle");
        Directory.CreateDirectory(_root);
        BcCompiler.ResetSharedReferencesForTests();
    }

    public void Dispose()
    {
        BcCompiler.ResetSharedReferencesForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── POSITIVE: a bundle that only adds dependencies keeps the loader ────────────────

    [Fact]
    public void AnAddedDependencyOverTheSameScan_ReusesTheLoaderAndWarmsOnlyTheAddedSpecs()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        var warmedForTheFirst = BcCompiler.ReferenceLoaderWarmSpecCount;
        Assert.Equal(2, warmedForTheFirst);   // Greeter, and the System floor its manifest declares

        // The next bundle adds Assert (which depends on Mid -> Leaf): over the same packages.
        SetDeps(dir, "Greeter", "Assert");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        // Assert, Mid, Leaf are new on this loader; Greeter and the System floor are not walked again.
        Assert.Equal(warmedForTheFirst + 3, BcCompiler.ReferenceLoaderWarmSpecCount);
    }

    [Fact]
    public void ReturningToAnEarlierBundlesDependencies_NeitherRebuildsNorWarmsAgain()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, "Greeter", "Assert");
        InvokeGetSharedReferences(new[] { dir });
        var warmed = BcCompiler.ReferenceLoaderWarmSpecCount;

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(warmed, BcCompiler.ReferenceLoaderWarmSpecCount);
    }

    /// <summary>Bundles whose dependencies do not overlap: going back to an earlier one is a reuse too,
    /// because what the loader is warm for only ever grows until it is rebuilt.</summary>
    [Fact]
    public void ReturningToDisjointEarlierDependencies_ReusesTheLoaderAndDoesNotWarmAgain()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        var warmed = BcCompiler.ReferenceLoaderWarmSpecCount;
        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(warmed, BcCompiler.ReferenceLoaderWarmSpecCount);
    }

    // ── EQUIVALENCE: the reused loader answers what a loader built for the bundle answers ─

    [Fact]
    public void AReusedLoader_AnswersWhatALoaderBuiltForEachBundlesOwnPackagesAnswers()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        WriteNoise(dir, 5);

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, "Greeter", "Assert");
        var reused = InvokeGetSharedReferences(new[] { dir }).Loader!;
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);   // it really is the reused one

        var wholeFolder = ReferenceLoaderFactory.CreateReferenceLoader(new[] { dir });
        // What a loader built for just the second bundle (Greeter + Assert + their closure) holds.
        var ownClosure = CopyOnly(dir, "second-bundle",
            "Greeter", "Assert", "Mid", "Mid", "Leaf", "System");
        var forTheBundle = ReferenceLoaderFactory.CreateReferenceLoader(new[] { ownClosure });

        foreach (var name in new[] { "Greeter", "Assert", "Mid", "Leaf", "System" })
        {
            var spec = SpecFor(_byName[name]);
            var expected = VersionOf(wholeFolder.LoadModule(spec, new List<Diagnostic>())!);
            Assert.Equal(expected, VersionOf(reused.LoadModule(spec, new List<Diagnostic>())!));
            Assert.Equal(expected, VersionOf(forTheBundle.LoadModule(spec, new List<Diagnostic>())!));
        }
        // Mid ships in two versions; the highest satisfying one wins on every loader.
        Assert.Equal("2.0.0.0", VersionOf(reused.LoadModule(SpecFor(_byName["Mid"]), new List<Diagnostic>())!));
        // A package nobody asked for is not invented.
        Assert.Null(reused.LoadModule(SpecFor(new Pkg(Guid.NewGuid(), "Never Written", "Contoso", "1.0.0.0", "")),
            new List<Diagnostic>()));
    }

    // ── NEGATIVE: what makes a reused loader wrong must still rebuild it ───────────────

    [Fact]
    public void AnotherScanSet_StillRebuilds()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        var other = MakeDir("other");
        WritePkg(other, "Extra", "1.0.0.0");

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        BcCompiler.SetResolvedDeps(Resolved("Greeter", "Extra"), new[] { dir, other });
        InvokeGetSharedReferences(new[] { dir, other });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    [Fact]
    public void APackageThatAppearsAfterTheBuild_RebuildsWhenItBecomesADependency()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        WritePkg(dir, "Late Arrival", "1.0.0.0");   // not in the scan the loader was built over

        SetDeps(dir, "Greeter", "Late Arrival");
        var loader = InvokeGetSharedReferences(new[] { dir }).Loader!;

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
        Assert.NotNull(loader.LoadModule(SpecFor(_byName["Late Arrival"]), new List<Diagnostic>()));
    }

    [Fact]
    public void AScannedFileRewrittenAfterTheBuild_RebuildsWhenItBecomesADependency()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        // Assert was in the scan at build time and was not a dependency then; now its bytes differ.
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" }, marker: "rewritten after the build");

        SetDeps(dir, "Greeter", "Assert");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    [Fact]
    public void ADependencyRewrittenInPlace_Rebuilds()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Greeter", "Assert");
        InvokeGetSharedReferences(new[] { dir });
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" }, marker: "changed bytes");
        SetDeps(dir, "Greeter", "Assert");   // same path, same version, different bytes
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    [Fact]
    public void ADependencyAtANewVersion_Rebuilds()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);

        SetDeps(dir, "Greeter", "Assert");
        InvokeGetSharedReferences(new[] { dir });
        var assert = _byName["Assert"];
        BcCompiler.SetResolvedDeps(new List<(AppManifest, string)>
        {
            (ManifestOf(_byName["Greeter"]), _byName["Greeter"].Path),
            (ManifestOf(assert with { Version = "1.1.0.0" }), assert.Path),
        }, new[] { dir });
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>A scanned package that was never a dependency, rewritten to the same length with a new
    /// timestamp, is a different package: a check on length alone would reuse a loader that has
    /// memoized the old one.</summary>
    [Fact]
    public void ANeverDependencyRewrittenToTheSameLengthWithANewTimestamp_RebuildsWhenItBecomesADependency()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" }, marker: "abcdefgh");   // as long as the rewrite below
        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        var stamp = File.GetLastWriteTimeUtc(_byName["Assert"].Path);

        RewriteAssertToTheSameLength(dir);
        File.SetLastWriteTimeUtc(_byName["Assert"].Path, stamp.AddSeconds(5));
        SetDeps(dir, "Greeter", "Assert");
        var loader = InvokeGetSharedReferences(new[] { dir }).Loader!;

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal("Leaf@1.0.0.0,System@28.0.0.0", DependenciesOf(loader, "Assert"));
    }

    /// <summary>The same rewrite with the mtime put back EXACTLY (<c>cp -p</c>, <c>rsync -t</c>): same
    /// length, same nanosecond mtime, only ctime moved. BC's manifest memo is keyed by path alone, so a
    /// reused loader would answer the old dependency list; the check has to use what the writer cannot
    /// set back (ctime, #5409). Main rebuilds here, because the key of a package that becomes a dependency
    /// is new. .NET's <c>SetLastWriteTimeUtc</c> keeps 100 ns ticks only, so the OS tool puts it back. The
    /// in-process test pins the loader; the runner's own manifest memo (AppLoader.ReadManifest, #5499) is
    /// a separate one.</summary>
    [SkippableFact]
    public void ANeverDependencyRewrittenToTheSameLengthWithTheTimestampPutBack_RebuildsWhenItBecomesADependency()
    {
        // Linux only, as FileIdentityTests is: there a missing key or a missing coreutils fails below
        // instead of skipping, so this cannot skip on a leg where that class runs.
        Skip.IfNot(OperatingSystem.IsLinux(), "ctime is only available through statx on Linux (FileIdentity header)");
        var dir = MakeDir("cache");
        WriteGraph(dir);
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" }, marker: "abcdefgh");   // as long as the rewrite below
        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        var path = _byName["Assert"].Path;
        Assert.NotNull(AlRunner.Infrastructure.FileIdentity.Probe(Path.GetFullPath(path)).Key);
        var reference = path + ".mtime";
        File.WriteAllText(reference, "");
        Assert.NotNull(Os("touch", "-r", path, reference));
        var length = new FileInfo(path).Length;
        var mtime = Os("stat", "-c", "%.9Y", path);
        var ctime = Os("stat", "-c", "%.9Z", path);
        Assert.NotNull(mtime);

        RewriteAssertToTheSameLength(dir);
        Assert.NotNull(Os("touch", "-m", "-r", reference, path));

        // The premise: only ctime moved. Were mtime to differ, the test could pass on that alone.
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(mtime, Os("stat", "-c", "%.9Y", path));
        Assert.NotEqual(ctime, Os("stat", "-c", "%.9Z", path));
        SetDeps(dir, "Greeter", "Assert");
        var loader = InvokeGetSharedReferences(new[] { dir }).Loader!;

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal("Leaf@1.0.0.0,System@28.0.0.0", DependenciesOf(loader, "Assert"));
    }

    private static string? Os(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode == 0 ? stdout.Trim() : null;
    }

    /// <summary>A dependency added by reuse is remembered with its path, so a later rewrite of it is a
    /// changed key at a known path and not a new package.</summary>
    [Fact]
    public void ADependencyAddedByReuse_IsRememberedByPath()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, "Greeter", "Assert");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        var remembered = BcCompiler.LoaderDepPathsForTests();
        Assert.Contains(Path.GetFullPath(_byName["Assert"].Path), remembered, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(_byName["Greeter"].Path), remembered, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>After a rebuild the loader is warm for the new keys only: a dependency put back to its
    /// earlier bytes is not a key it was warmed for, and its path was seen with other bytes.</summary>
    [Fact]
    public void ADependencyPutBackToItsEarlierBytes_AfterARebuild_RebuildsAgain()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        var path = _byName["Assert"].Path;
        var original = path + ".original";
        File.Copy(path, original);

        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Leaf" }, marker: "second bytes");
        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);

        File.Copy(original, path, overwrite: true);
        SetDeps(dir, "Assert");
        var loader = InvokeGetSharedReferences(new[] { dir }).Loader!;

        Assert.Equal(3, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal("Mid@2.0.0.0,System@28.0.0.0", DependenciesOf(loader, "Assert"));
    }

    /// <summary>The key of a dependency the loader has seen must outlive another bundle's
    /// dependencies: a package rewritten to the same length and timestamp is still a different
    /// package, and only the key from the earlier bundle can say so.</summary>
    [Fact]
    public void ADependencyRewrittenWithTheSameSizeAndTimestamp_AfterAnotherBundle_Rebuilds()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" }, marker: "abcdefgh");

        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });
        SetDeps(dir, "Greeter");   // a bundle that does not depend on Assert
        InvokeGetSharedReferences(new[] { dir });
        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);

        var path = _byName["Assert"].Path;
        var length = new FileInfo(path).Length;
        var stamp = File.GetLastWriteTimeUtc(path);
        // Different bytes, same size: the compressed size depends on the content, so look for a marker
        // that lands on it.
        const string original = "abcdefgh";
        for (var shift = 1; shift < original.Length; shift++)
        {
            WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" },
                marker: original[shift..] + original[..shift]);
            if (new FileInfo(path).Length == length) break;
        }
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(length, new FileInfo(path).Length);   // the premise of this test

        SetDeps(dir, "Assert");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
    }

    /// <summary>The third state: a dependency whose bytes could not be named is never trusted, even
    /// when its path is in the scan the loader was built over and unchanged.</summary>
    [Fact]
    public void ADependencyWhoseBytesCouldNotBeNamed_IsNeverTrusted()
    {
        var dir = MakeDir("cache");
        var greeter = WritePkg(dir, "Greeter", "1.0.0.0");
        var build = BcCompiler.LoaderBuild.For(Array.Empty<BcCompiler.ServedDep>(), new[]
        {
            new BcCompiler.PackageScanEntry(greeter.Path, greeter.AppId, "Contoso", "Greeter", new Version(1, 0, 0, 0)),
        });
        var named = new BcCompiler.ServedDep(greeter.Path, greeter.Path + "@1.0.0.0@hash", Identified: true);

        Assert.Equal(BcCompiler.LoaderReuse.ReuseAndWarm,
            BcCompiler.JudgeLoaderReuse(new[] { named }, new HashSet<string>(), build));
        Assert.Equal(BcCompiler.LoaderReuse.Rebuild,
            BcCompiler.JudgeLoaderReuse(new[] { named with { Identified = false } }, new HashSet<string>(), build));
        Assert.Equal(BcCompiler.LoaderReuse.Reuse,
            BcCompiler.JudgeLoaderReuse(new[] { named }, new HashSet<string> { named.Key }, build));
    }

    /// <summary>…and the flag itself: a package that is in the scan but cannot be read is reported as
    /// one whose bytes were not named, a readable one as named.</summary>
    [Fact]
    public void ServedDeps_FlagAnUnreadablePackageAsUnidentified_AndAReadableOneAsIdentified()
    {
        var dir = MakeDir("cache");
        var readable = WritePkg(dir, "Greeter", "1.0.0.0");
        var gone = Path.Combine(dir, "gone.app");
        var inventory = new List<BcCompiler.PackageScanEntry>
        {
            new(Path.GetFullPath(readable.Path), readable.AppId, "Contoso", "Greeter", new Version(1, 0, 0, 0)),
            new(Path.GetFullPath(gone), Guid.NewGuid(), "Contoso", "Gone", new Version(1, 0, 0, 0)),
        };
        var deps = new List<(AppManifest, string)>
        {
            (ManifestOf(readable), readable.Path),
            (new AppManifest("Contoso", "Gone", new Version(1, 0, 0, 0), Guid.NewGuid(), new List<DependencyRef>()), gone),
        };

        var served = BcCompiler.PackageServedDeps(deps, inventory);

        Assert.Equal(2, served.Count);
        Assert.True(served.Single(d => d.Path == Path.GetFullPath(readable.Path)).Identified);
        Assert.False(served.Single(d => d.Path == Path.GetFullPath(gone)).Identified);
    }

    // ── Seeding every bundle's closure first: one stage, so one loader, for the run ─────

    [Fact]
    public void WhenEveryBundlesRootsAreSeededFirst_AClosureNarrowedRunBuildsOneLoader()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        WriteNoise(dir, BcCompilerPackageClosureBound() + 30);

        BcCompiler.SeedClosureRoots(new[] { SeedOf("Greeter"), SeedOf("Assert") });
        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        var stage = Assert.Single(BcCompiler.LoaderScanDirs);
        Assert.NotEqual(dir, stage);   // the premise: this run is narrowed
        SetDeps(dir, "Greeter", "Assert");
        var reused = InvokeGetSharedReferences(new[] { dir }).Loader!;
        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });

        Assert.Equal(1, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal(stage, Assert.Single(BcCompiler.LoaderScanDirs));

        // And the loader of the narrowed stage answers what the whole folder answers.
        var wholeFolder = ReferenceLoaderFactory.CreateReferenceLoader(new[] { dir });
        foreach (var name in new[] { "Greeter", "Assert", "Mid", "Leaf", "System" })
        {
            var spec = SpecFor(_byName[name]);
            Assert.Equal(VersionOf(wholeFolder.LoadModule(spec, new List<Diagnostic>())!),
                VersionOf(reused.LoadModule(spec, new List<Diagnostic>())!));
        }
        Assert.Null(reused.LoadModule(SpecFor(_byName["Noise 7"]), new List<Diagnostic>()));
    }

    /// <summary>The control: without the seeding the second bundle's closure is a new stage, and
    /// the loader is rebuilt, as before. Slower, never wrong.</summary>
    [Fact]
    public void WithoutSeeding_AGrowingClosureStillGetsItsOwnLoader_AndItAnswersCorrectly()
    {
        var dir = MakeDir("cache");
        WriteGraph(dir);
        WriteNoise(dir, BcCompilerPackageClosureBound() + 30);

        SetDeps(dir, "Greeter");
        InvokeGetSharedReferences(new[] { dir });
        var first = Assert.Single(BcCompiler.LoaderScanDirs);
        SetDeps(dir, "Greeter", "Assert");
        var second = InvokeGetSharedReferences(new[] { dir }).Loader!;

        Assert.NotEqual(first, Assert.Single(BcCompiler.LoaderScanDirs));
        Assert.Equal(2, BcCompiler.ReferenceLoaderBuildCount);
        Assert.Equal("2.0.0.0", VersionOf(second.LoadModule(SpecFor(_byName["Mid"]), new List<Diagnostic>())!));
    }

    // ── The seeding itself: what a bundle's app.json contributes ───────────────────────

    [Fact]
    public void SeedingFromBundles_TakesEveryBundlesDeclaredDependencies_AndTheTestToolOnlyWhenAsked()
    {
        var leaf = Guid.NewGuid();
        var tests = Guid.NewGuid();
        var a = MakeDir("bundle-a");
        var b = MakeDir("bundle-b");
        File.WriteAllText(Path.Combine(a, "app.json"),
            $$"""{"id":"{{Guid.NewGuid()}}","name":"A","publisher":"P","version":"1.0.0.0","dependencies":[{"id":"{{leaf}}","name":"Declared By A","publisher":"P","version":"1.0.0.0"}]}""");
        File.WriteAllText(Path.Combine(b, "app.json"),
            $$"""{"id":"{{Guid.NewGuid()}}","name":"B","publisher":"P","version":"1.0.0.0","dependencies":[{"id":"{{tests}}","name":"Declared By B","publisher":"P","version":"1.0.0.0"}]}""");

        ProgramSupport.SeedClosureForBundles(new[] { a, b }, includeInstalledTestTool: false);
        var seeds = BcCompiler.ClosureSeedsForTests();
        Assert.Contains(new BcCompiler.ClosureSeed(leaf, "Declared By A"), seeds);
        Assert.Contains(new BcCompiler.ClosureSeed(tests, "Declared By B"), seeds);
        Assert.DoesNotContain(seeds, s => s.Name == "Test Runner");

        ProgramSupport.SeedClosureForBundles(new[] { a }, includeInstalledTestTool: true);
        Assert.Contains(BcCompiler.ClosureSeedsForTests(), s => s.Name == "Test Runner");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private string DependenciesOf(ISymbolReferenceLoader loader, string name)
        => string.Join(",", loader.GetDependencies(SpecFor(_byName[name]), new List<Diagnostic>())
            .Select(d => d.Name + "@" + d.Version).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Rewrites Assert so that it depends on Leaf instead of Mid and its file is exactly as long
    /// as before; the compressed size depends on the content, so look for a marker that lands on it.</summary>
    private void RewriteAssertToTheSameLength(string dir)
    {
        var path = _byName["Assert"].Path;
        var length = new FileInfo(path).Length;
        var rnd = new Random(1);
        for (var i = 0; i < 20000; i++)
        {
            var marker = new string(Enumerable.Range(0, 8 + rnd.Next(0, 4)).Select(_ => (char)('a' + rnd.Next(26))).ToArray());
            WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Leaf" }, marker: marker);
            if (new FileInfo(path).Length == length) return;
        }
        throw new InvalidOperationException("no marker gave the same file length; the premise of the test is not met");
    }

    private static int BcCompilerPackageClosureBound() => BcCompiler.BcManifestCacheCapacity;

    /// <summary>Greeter (depends on nothing) and Assert -> Mid (two versions) -> Leaf, with System
    /// as the Platform floor of every package.</summary>
    private void WriteGraph(string dir)
    {
        WritePkg(dir, "System", "28.0.0.0", floors: false);
        WritePkg(dir, "Leaf", "1.0.0.0");
        WritePkg(dir, "Mid", "1.0.0.0", deps: new[] { "Leaf" });
        WritePkg(dir, "Mid", "2.0.0.0", deps: new[] { "Leaf" });
        WritePkg(dir, "Assert", "1.0.0.0", deps: new[] { "Mid" });
        WritePkg(dir, "Greeter", "1.0.0.0");
    }

    private void WriteNoise(string dir, int count)
    {
        for (var i = 0; i < count; i++) WritePkg(dir, $"Noise {i}", "1.0.0.0");
    }

    private Pkg WritePkg(string dir, string name, string version, string[]? deps = null,
        bool floors = true, string marker = "x")
    {
        var id = _byName.TryGetValue(name, out var prev) ? prev.AppId : Guid.NewGuid();
        var path = Path.Combine(dir, $"Contoso_{name}_{version}.app");
        var depXml = string.Join("", (deps ?? Array.Empty<string>()).Select(d =>
            $"""<Dependency Id="{_byName[d].AppId}" Name="{d}" Publisher="Contoso" MinVersion="{_byName[d].Version}" />"""));
        var floorAttrs = floors ? """ Platform="28.0.0.0" """ : "";
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{id}" Name="{name}" Publisher="Contoso" Version="{version}" CompatibilityId="0.0.0.0"{floorAttrs} Runtime="17.0" />
              <Dependencies>{depXml}</Dependencies>
              <InternalsVisibleTo />
            </Package>
            """;
        var symbols = $$"""
            {"RuntimeVersion":"17.0","Namespaces":[],"Codeunits":[],"Reports":[],"XmlPorts":[],
             "Queries":[],"ControlAddIns":[],"EnumTypes":[],"DotNetPackages":[],"Interfaces":[],
             "PermissionSets":[],"PermissionSetExtensions":[],"ReportExtensions":[],
             "InternalsVisibleToModules":[],"Marker":"{{marker}}",
             "AppId":"{{id}}","Name":"{{name}}","Publisher":"Contoso","Version":"{{version}}"}
            """;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        using (var writer = NavAppPackageWriter.Create(fs))
        {
            writer.WriteString(manifest, "/NavxManifest.xml");
            writer.WriteString(symbols, "/SymbolReference.json");
        }
        var pkg = new Pkg(id, name, "Contoso", version, path);
        // The later of two versions of one name wins the lookup; only Mid has two.
        if (!_byName.TryGetValue(name, out var existing) || string.CompareOrdinal(existing.Version, version) <= 0)
            _byName[name] = pkg;
        return pkg;
    }

    /// <summary>A directory holding copies of just the named packages (every version of a name).</summary>
    private string CopyOnly(string from, string into, params string[] names)
    {
        var d = MakeDir(into);
        foreach (var f in Directory.GetFiles(from, "*.app"))
            if (names.Distinct().Any(n => Path.GetFileName(f).StartsWith($"Contoso_{n}_", StringComparison.Ordinal)))
                File.Copy(f, Path.Combine(d, Path.GetFileName(f)));
        return d;
    }

    private static AppManifest ManifestOf(Pkg p)
        => new(p.Publisher, p.Name, System.Version.Parse(p.Version), p.AppId, new List<DependencyRef>());

    private List<(AppManifest, string)> Resolved(params string[] names)
        => names.Select(n => (ManifestOf(_byName[n]), _byName[n].Path)).ToList();

    private void SetDeps(string dir, params string[] names)
        => BcCompiler.SetResolvedDeps(Resolved(names), new[] { dir });

    private BcCompiler.ClosureSeed SeedOf(string name) => new(_byName[name].AppId, name);

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
