// #5107: which --server source paths ProgramSupport.ExpandAppContainerRoots splits into one path
// per app. TransitiveDependencyVisibilityTests drives the observable (AL0185 through a served
// container); these facts pin the shapes that must be left alone.
using Xunit;

namespace AlRunner.Tests;

public sealed class ExpandAppContainerRootsTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-expand-app-containers");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void Touch(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string App(params string[] relative)
    {
        var dir = Path.Combine(new[] { _root }.Concat(relative).ToArray());
        Touch(Path.Combine(dir, "app.json"), """{ "id": "00000000-0000-0000-0000-000000051070", "name": "A", "publisher": "P", "version": "1.0.0.0" }""");
        Touch(Path.Combine(dir, "A.Codeunit.al"), "codeunit 50100 A { }");
        return Path.GetFullPath(dir);
    }

    [Fact]
    public void ContainerOfApps_BecomesOnePathPerApp()
    {
        var a = App("ws", "base-app");
        var b = App("ws", "nested", "middle-app");
        var c = App("ws", "test-app");

        var expanded = ProgramSupport.ExpandAppContainerRoots(new[] { Path.Combine(_root, "ws") });

        Assert.Equal(new[] { a, b, c }.OrderBy(x => x, StringComparer.Ordinal),
            expanded.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ContainerOfOneApp_BecomesThatApp()
    {
        var a = App("one", "only-app");

        Assert.Equal(new[] { a }, ProgramSupport.ExpandAppContainerRoots(new[] { Path.Combine(_root, "one") }));
    }

    [Fact]
    public void AnAppItself_IsLeftAsGiven()
    {
        var a = App("self");
        App("self", "vendored", "inner-app");

        Assert.Equal(new[] { a }, ProgramSupport.ExpandAppContainerRoots(new[] { a }));
    }

    [Fact]
    public void AFolderInsideAnApp_IsLeftAsGiven()
    {
        App("outer");
        App("outer", "sub", "inner-app");
        var sub = Path.Combine(_root, "outer", "sub");

        Assert.Equal(new[] { sub }, ProgramSupport.ExpandAppContainerRoots(new[] { sub }));
    }

    [Fact]
    public void ContainerWithASuiteThatHasNoAppJson_IsLeftAsGiven()
    {
        App("mixed", "with-manifest");
        Touch(Path.Combine(_root, "mixed", "split-suite", "src", "B.Codeunit.al"), "codeunit 50101 B { }");
        var mixed = Path.Combine(_root, "mixed");

        Assert.Equal(new[] { mixed }, ProgramSupport.ExpandAppContainerRoots(new[] { mixed }));
    }

    /// <summary>The CLI's BuildAppGroups reads a suite whose app.json yields no identity as an
    /// orphan and compiles it in the fallback module; splitting it into its own server bundle made
    /// its dependency read throw, and the request ran nothing (AlOutputCacheDoNotCacheTests).</summary>
    [Fact]
    public void ContainerWithAnUnreadableAppJson_IsLeftAsGiven()
    {
        App("broken", "good-app");
        Touch(Path.Combine(_root, "broken", "bad-app", "app.json"), """{ "id": "not valid json """);
        Touch(Path.Combine(_root, "broken", "bad-app", "B.Codeunit.al"), "codeunit 50103 B { }");
        var broken = Path.Combine(_root, "broken");

        Assert.Equal(new[] { broken }, ProgramSupport.ExpandAppContainerRoots(new[] { broken }));
    }

    [Fact]
    public void FlatFolderOfAlFiles_IsLeftAsGiven()
    {
        var flat = Path.Combine(_root, "flat");
        Touch(Path.Combine(flat, "C.Codeunit.al"), "codeunit 50102 C { }");

        Assert.Equal(new[] { flat }, ProgramSupport.ExpandAppContainerRoots(new[] { flat }));
    }

    [Fact]
    public void SeveralPaths_KeepTheirOrder_AndExpandInPlace()
    {
        var first = App("first");
        var a = App("ws2", "a-app");
        var b = App("ws2", "b-app");
        var last = App("last");

        var expanded = ProgramSupport.ExpandAppContainerRoots(new[] { first, Path.Combine(_root, "ws2"), last });

        Assert.Equal(first, expanded[0]);
        Assert.Equal(new[] { a, b }.OrderBy(x => x, StringComparer.Ordinal),
            expanded[1..3].OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(last, expanded[3]);
        Assert.Equal(4, expanded.Length);
    }
}
