// #4071: ProgramSupport.SourceDirsWithCompileManifest pairs every registered source folder with
// the app.json its compile reads. It runs before BuildAppGroups, so it restates that grouping's
// manifest half; these facts pin the two together, per layout, against the compile's own rule
// (BcCompiler.ResolveManifestAppJson over a group's SuiteDir and Paths).
using Xunit;

namespace AlRunner.Tests;

public sealed class SuiteCompileManifestTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-suite-compile-manifest");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Touch(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Manifest(string id, string name) =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "publisher": "P", "version": "1.0.0.0", "preprocessorSymbols": ["X"] }""";

    private static void AssertMatchesBuildAppGroups(List<string> suites, string bundle)
    {
        var registered = ProgramSupport.SourceDirsWithCompileManifest(suites, bucketRoot: null, bundle, bundledMode: true);
        var groups = ProgramSupport.BuildAppGroups(suites, bucketRoot: null, bundle);

        var fromGroups = groups
            .SelectMany(g => g.Paths.Select(p => (Dir: p, Manifest: AlRunner.BcCompiler.ResolveManifestAppJson(g.SuiteDir, g.Paths))))
            .ToList();
        Assert.NotEmpty(fromGroups);
        Assert.Equal(fromGroups.OrderBy(x => x.Dir, StringComparer.Ordinal), registered.OrderBy(x => x.Dir, StringComparer.Ordinal));
    }

    [Fact]
    public void Bundled_IdentifiedSuiteWithSrc_ReadsTheSuiteManifest()
    {
        var suite = Path.Combine(_root, "app");
        Touch(Path.Combine(suite, "app.json"), Manifest("00000000-0000-0000-0000-000000040711", "A"));
        Touch(Path.Combine(suite, "src", "A.al"), "codeunit 50100 A { }");
        var suites = new List<string> { suite };

        AssertMatchesBuildAppGroups(suites, _root);
        var (dir, manifest) = Assert.Single(ProgramSupport.SourceDirsWithCompileManifest(suites, null, _root, bundledMode: true));
        Assert.Equal(Path.Combine(suite, "src"), dir);
        Assert.Equal(Path.Combine(suite, "app.json"), manifest);
    }

    [Fact]
    public void Bundled_FolderWithoutManifestUnderAParentWithOne_ReadsNone()
    {
        Touch(Path.Combine(_root, "app.json"), Manifest("00000000-0000-0000-0000-000000040712", "Parent"));
        var folder = Path.Combine(_root, "folder");
        Touch(Path.Combine(folder, "A.al"), "codeunit 50100 A { }");
        var suites = new List<string> { folder };

        AssertMatchesBuildAppGroups(suites, folder);
        Assert.All(ProgramSupport.SourceDirsWithCompileManifest(suites, null, folder, bundledMode: true),
            r => Assert.Null(r.ManifestAppJsonPath));
    }

    [Fact]
    public void Bundled_IdentifiedAndFallbackSuitesMixed_MatchBuildAppGroups()
    {
        var withId = Path.Combine(_root, "with-id");
        Touch(Path.Combine(withId, "app.json"), Manifest("00000000-0000-0000-0000-000000040713", "WithId"));
        Touch(Path.Combine(withId, "A.al"), "codeunit 50100 A { }");
        var withoutId = Path.Combine(_root, "without-id");
        Touch(Path.Combine(withoutId, "app.json"), """{ "name": "NoId", "preprocessorSymbols": ["Y"] }""");
        Touch(Path.Combine(withoutId, "B.al"), "codeunit 50101 B { }");
        var bare = Path.Combine(_root, "bare");
        Touch(Path.Combine(bare, "C.al"), "codeunit 50102 C { }");

        AssertMatchesBuildAppGroups(new List<string> { withId, withoutId, bare }, _root);
    }

    [Fact]
    public void PerSuite_EachSuiteReadsItsOwnRootOrNone()
    {
        var withId = Path.Combine(_root, "with-id");
        Touch(Path.Combine(withId, "app.json"), Manifest("00000000-0000-0000-0000-000000040714", "WithId"));
        Touch(Path.Combine(withId, "A.al"), "codeunit 50100 A { }");
        Touch(Path.Combine(_root, "app.json"), Manifest("00000000-0000-0000-0000-000000040715", "Parent"));
        var bare = Path.Combine(_root, "bare");
        Touch(Path.Combine(bare, "C.al"), "codeunit 50102 C { }");

        var registered = ProgramSupport.SourceDirsWithCompileManifest(
            new List<string> { withId, bare }, null, _root, bundledMode: false);

        Assert.Equal(Path.Combine(withId, "app.json"), registered.Single(r => r.Dir == withId).ManifestAppJsonPath);
        Assert.Null(registered.Single(r => r.Dir == bare).ManifestAppJsonPath);
    }
}
