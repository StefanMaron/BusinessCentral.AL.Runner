using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

// =========================================================================
// RewriteCacheTests — unit tests for the Roslyn rewrite cache
// =========================================================================

[Collection("Pipeline")]
public class RewriteCacheTests
{
    [Fact]
    public void TryGet_CacheHit_ReturnsSameTree()
    {
        var cache = new RewriteCache();
        var tree = CSharpSyntaxTree.ParseText("class C {}");

        cache.Store("Object1", "class C {}", tree, iterationTracking: false);

        var result = cache.TryGet("Object1", "class C {}", iterationTracking: false);
        Assert.Same(tree, result);
    }

    [Fact]
    public void TryGet_CacheMiss_DifferentCode_ReturnsNull()
    {
        var cache = new RewriteCache();
        var tree = CSharpSyntaxTree.ParseText("class C {}");

        cache.Store("Object1", "class C {}", tree, iterationTracking: false);

        var result = cache.TryGet("Object1", "class D {}", iterationTracking: false);
        Assert.Null(result);
    }

    [Fact]
    public void TryGet_CacheMiss_IterationTrackingChanged_ReturnsNull()
    {
        var cache = new RewriteCache();
        var tree = CSharpSyntaxTree.ParseText("class C {}");

        cache.Store("Object1", "class C {}", tree, iterationTracking: false);

        var result = cache.TryGet("Object1", "class C {}", iterationTracking: true);
        Assert.Null(result);
    }

    [Fact]
    public void Store_Overwrite_ReturnsNewTree()
    {
        var cache = new RewriteCache();
        var tree1 = CSharpSyntaxTree.ParseText("class C {}");
        var tree2 = CSharpSyntaxTree.ParseText("class D {}");

        cache.Store("Object1", "class C {}", tree1, iterationTracking: false);
        cache.Store("Object1", "class D {}", tree2, iterationTracking: false);

        // Old code no longer matches
        var miss = cache.TryGet("Object1", "class C {}", iterationTracking: false);
        Assert.Null(miss);

        // New code returns the new tree
        var hit = cache.TryGet("Object1", "class D {}", iterationTracking: false);
        Assert.Same(tree2, hit);
    }

    [Fact]
    public void Count_ReflectsStoredEntries()
    {
        var cache = new RewriteCache();
        Assert.Equal(0, cache.Count);

        cache.Store("A", "code-a", CSharpSyntaxTree.ParseText("class A {}"), false);
        cache.Store("B", "code-b", CSharpSyntaxTree.ParseText("class B {}"), false);
        Assert.Equal(2, cache.Count);

        // Overwrite does not increase count
        cache.Store("A", "code-a2", CSharpSyntaxTree.ParseText("class A2 {}"), false);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new RewriteCache();
        cache.Store("A", "code-a", CSharpSyntaxTree.ParseText("class A {}"), false);
        cache.Store("B", "code-b", CSharpSyntaxTree.ParseText("class B {}"), false);
        Assert.Equal(2, cache.Count);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryGet("A", "code-a", false));
    }
}

// =========================================================================
// SyntaxTreeCacheTests — unit tests for the AL parse cache
// =========================================================================

[Collection("Pipeline")]
public class SyntaxTreeCacheTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort cleanup */ }
        }
    }

    private string CreateTempAlFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void GetOrParse_SameContent_ReturnsSameTree()
    {
        var cache = new SyntaxTreeCache();
        var alCode = "codeunit 50100 TestCU { trigger OnRun() begin end; }";
        var path = CreateTempAlFile(alCode);

        var tree1 = cache.GetOrParse(path, alCode);
        var tree2 = cache.GetOrParse(path, alCode);

        Assert.Same(tree1, tree2);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void GetOrParse_DifferentContent_ReturnsDifferentTree()
    {
        var cache = new SyntaxTreeCache();
        var path = CreateTempAlFile("codeunit 50100 TestCU { trigger OnRun() begin end; }");

        var tree1 = cache.GetOrParse(path, "codeunit 50100 TestCU { trigger OnRun() begin end; }");
        var tree2 = cache.GetOrParse(path, "codeunit 50101 TestCU2 { trigger OnRun() begin end; }");

        Assert.NotSame(tree1, tree2);
        Assert.Equal(1, cache.Count); // same path, overwritten entry
    }

    [Fact]
    public void GetOrParse_ThreadSafety_NoExceptions()
    {
        var cache = new SyntaxTreeCache();
        var paths = Enumerable.Range(0, 20).Select(i =>
        {
            var code = $"codeunit {50100 + i} CU{i} {{ trigger OnRun() begin end; }}";
            return (Path: CreateTempAlFile(code), Code: code);
        }).ToList();

        Parallel.ForEach(paths, item =>
        {
            var tree = cache.GetOrParse(item.Path, item.Code);
            Assert.NotNull(tree);
        });

        Assert.Equal(20, cache.Count);
    }
}

// =========================================================================
// Pipeline rewrite cache integration test
// =========================================================================

[Collection("Pipeline")]
public class PipelineRewriteCacheTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestPath(string testCase, string sub) =>
        Path.Combine(CliRunner.FindTestCase(testCase), sub);

    [Fact]
    public void Pipeline_SecondRun_ShowsRewriteCacheHits()
    {
        var pipeline = new AlRunnerPipeline();
        pipeline.RewriteCache = new RewriteCache();

        var options = new PipelineOptions
        {
            TestIsolation = AlRunner.TestIsolation.Method,
            InputPaths = { TestPath("01-pure-function", "src"), TestPath("01-pure-function", "test") },
            Verbose = true
        };

        // First run — cold cache
        var r1 = pipeline.Run(options);
        Assert.Equal(0, r1.ExitCode);
        Assert.DoesNotContain("Rewrite cache:", r1.StdErr);

        // Second run — warm cache, should log rewrite cache hits
        var r2 = pipeline.Run(options);
        Assert.Equal(0, r2.ExitCode);
        Assert.Contains("Rewrite cache:", r2.StdErr);

        // Results should be identical
        Assert.Equal(r1.Passed, r2.Passed);
        Assert.Equal(r1.Failed, r2.Failed);
    }
}
