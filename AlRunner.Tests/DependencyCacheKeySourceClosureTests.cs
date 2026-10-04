// DependencyCacheKeySourceClosureTests — issue #5306.
//
// A source dependency compiles against the packages it depends on. The runner packages a source
// bundle itself (workspace-deps) and, under --tdd, rewrites that package when it generates a member
// into it, so the compile of a library depends on bytes the library's own package does not hold. The
// compiled-deps key named only the library's own bytes, so a DLL compiled while a neighbour lacked a
// member answered for a run in which it had it, and the reverse: a warm --tdd run generated other
// members than the cold one. These pin the key's term for that closure: the source-built packages the
// compile reaches follow it, transitively, and nothing else does.
using AlRunner;
using AlRunner.Infrastructure;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class DependencyCacheKeySourceClosureTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _outside;

    public DependencyCacheKeySourceClosureTests()
    {
        _root = TestScratch.Dir("al-runner-depkey-closure");
        _workspace = Path.Combine(_root, "workspace-deps");
        _outside = Path.Combine(_root, "packaged");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        CacheRoots.SetOverride(_root);
    }

    public void Dispose()
    {
        CacheRoots.ResetForTests();
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Pkg(AppManifest Manifest, string Path);

    private int _n;

    /// <summary>A package file under <paramref name="dir"/>, its own directory like a workspace entry.</summary>
    private Pkg Package(string dir, string name, string body, params Pkg[] dependsOn)
    {
        var path = Path.Combine(dir, $"{++_n}", $"{name}.app");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
        var id = Guid.NewGuid();
        var manifest = new AppManifest("AL Runner", name, new Version(1, 0, 0, 0), id,
            dependsOn.Select(d => new DependencyRef(d.Manifest.AppId, d.Manifest.Name, d.Manifest.Publisher, d.Manifest.Version)).ToList());
        return new Pkg(manifest, path);
    }

    private static string Key(Pkg of, params Pkg[] resolved)
        => DependencyLoader.ComputeSourceDependencyCacheKey(
            of.Manifest, of.Path, resolved.Select(r => (r.Manifest, r.Path)).ToList());

    /// <summary>The same package with other bytes at the same path: what regenerating it looks like.</summary>
    private static Pkg Rewritten(Pkg p, string body)
    {
        File.WriteAllBytes(p.Path, Encoding.UTF8.GetBytes(body));
        RunnerFingerprint.ClearFileContentHashMemoForTests();
        return p;
    }

    [Fact]
    public void Key_FollowsADirectSourceBuiltDependency()
    {
        var lib1 = Package(_workspace, "lib1", "lib1 without the member");
        var lib2 = Package(_workspace, "lib2", "lib2 own bytes", lib1);

        var before = Key(lib2, lib1, lib2);
        Assert.Equal(before, Key(lib2, lib1, lib2));          // the same closure keys the same: a warm run still hits
        Rewritten(lib1, "lib1 with the generated member");
        Assert.NotEqual(before, Key(lib2, lib1, lib2));       // the neighbour changed, the library's own bytes did not
    }

    [Fact]
    public void Key_FollowsASourceBuiltDependencyOfADependency()
    {
        var lib1 = Package(_workspace, "lib1", "lib1 without the member");
        var lib2 = Package(_workspace, "lib2", "lib2 own bytes", lib1);
        var lib3 = Package(_workspace, "lib3", "lib3 own bytes", lib2);   // declares lib2 only

        var before = Key(lib3, lib1, lib2, lib3);
        Rewritten(lib1, "lib1 with the generated member");
        Assert.NotEqual(before, Key(lib3, lib1, lib2, lib3));
    }

    [Fact]
    public void Key_DoesNotFollowAPackageTheCompileDoesNotReach()
    {
        var lib1 = Package(_workspace, "lib1", "lib1 own bytes");
        var lib2 = Package(_workspace, "lib2", "lib2 own bytes", lib1);
        var dependent = Package(_workspace, "dependent", "a library that depends on lib2", lib2);
        var unrelated = Package(_workspace, "unrelated", "a library nothing here declares");

        var before = Key(lib2, lib1, lib2, dependent, unrelated);
        Rewritten(dependent, "the dependent regenerated");
        Rewritten(unrelated, "the unrelated one regenerated");
        Assert.Equal(before, Key(lib2, lib1, lib2, dependent, unrelated));
    }

    [Fact]
    public void Key_DoesNotFollowAPackagedDependencyOutsideTheWorkspace()
    {
        var packaged = Package(_outside, "packaged", "a package the runner did not build");
        var lib = Package(_workspace, "lib", "lib own bytes", packaged);

        Assert.Null(DependencyLoader.SourceClosureCacheTerm(
            lib.Manifest, new[] { (packaged.Manifest, packaged.Path), (lib.Manifest, lib.Path) },
            CacheRoots.SourceBuiltPackageDirs(), static p => RunnerFingerprint.ComputeFileContentHashMemoized(p)));
        var before = Key(lib, packaged, lib);
        Rewritten(packaged, "rebuilt elsewhere");
        Assert.Equal(before, Key(lib, packaged, lib));
    }

    [Fact]
    public void Key_OfADiamondAndOfACycleIsComputed()
    {
        var shared = Package(_workspace, "shared", "shared bytes");
        var left = Package(_workspace, "left", "left bytes", shared);
        var right = Package(_workspace, "right", "right bytes", shared);
        var top = Package(_workspace, "top", "top bytes", left, right);
        var diamond = Key(top, shared, left, right, top);
        Rewritten(shared, "shared regenerated");
        Assert.NotEqual(diamond, Key(top, shared, left, right, top));

        // Two packages that each declare the other cannot be a real closure, but a manifest can say it.
        var a = Package(_workspace, "a", "a bytes");
        var b = Package(_workspace, "b", "b bytes", a);
        var cyclicA = a with { Manifest = a.Manifest with { Dependencies = new[] { new DependencyRef(b.Manifest.AppId, "b", "AL Runner", new Version(1, 0, 0, 0)) } } };
        var done = Task.Run(() => Key(cyclicA, cyclicA, b)).Wait(TimeSpan.FromSeconds(30));
        Assert.True(done, "the closure walk did not finish on a cycle");
    }

    [Fact]
    public void Key_OfAWorkspacePackageThatCannotBeReadIsRefusedNotKeyedOnNothing()
    {
        var lib1 = Package(_workspace, "lib1", "lib1 bytes");
        var lib2 = Package(_workspace, "lib2", "lib2 own bytes", lib1);
        File.Delete(lib1.Path);
        RunnerFingerprint.ClearFileContentHashMemoForTests();

        var ex = Assert.Throws<FileNotFoundException>(() => Key(lib2, lib1, lib2));
        Assert.Contains("lib1.app", ex.Message);
    }

    [Fact]
    public void Key_OfACompileWithNoSourceBuiltDependencyCarriesNoClosureTerm()
    {
        var packaged = Package(_outside, "packaged", "a package the runner did not build");
        var lib = Package(_outside, "lib", "lib own bytes", packaged);

        Assert.Null(DependencyLoader.SourceClosureCacheTerm(
            lib.Manifest, new[] { (packaged.Manifest, packaged.Path), (lib.Manifest, lib.Path) },
            CacheRoots.SourceBuiltPackageDirs(), static p => RunnerFingerprint.ComputeFileContentHashMemoized(p)));
        Assert.Equal(
            DependencyLoader.ComputeSourceDependencyCacheKeyCore(
                lib.Manifest, lib.Path, static p => RunnerFingerprint.ComputeFileContentHashMemoized(p),
                suppliedFloorsTerm: DependencyLoader.SuppliedFloorsCacheTerm(lib.Manifest, new[] { packaged.Manifest, lib.Manifest })),
            Key(lib, packaged, lib));
    }
}
