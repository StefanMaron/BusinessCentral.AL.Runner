using System.Collections.Concurrent;

namespace AlRunner;

/// <summary>
/// Caches rewritten Roslyn SyntaxTrees keyed by the transpiled C# code string
/// and the IterationTracking flag. Thread-safe for use from Parallel.For via
/// ConcurrentDictionary. On cache hit (C# output and options unchanged for a
/// given AL object), the prior rewritten tree is reused, skipping
/// RoslynRewriter + ValueCaptureInjector + IterationInjector for that file.
/// </summary>
public class RewriteCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    private sealed class CacheEntry
    {
        public required string CSharpCode { get; init; }
        public required bool IterationTracking { get; init; }
        public required Microsoft.CodeAnalysis.SyntaxTree RewrittenTree { get; init; }
    }

    public Microsoft.CodeAnalysis.SyntaxTree? TryGet(string objectName, string currentCSharp, bool iterationTracking)
    {
        if (_entries.TryGetValue(objectName, out var entry)
            && entry.CSharpCode == currentCSharp
            && entry.IterationTracking == iterationTracking)
            return entry.RewrittenTree;
        return null;
    }

    public void Store(string objectName, string csharpCode, Microsoft.CodeAnalysis.SyntaxTree rewrittenTree, bool iterationTracking)
    {
        _entries[objectName] = new CacheEntry
        {
            CSharpCode = csharpCode,
            RewrittenTree = rewrittenTree,
            IterationTracking = iterationTracking
        };
    }

    public int Count => _entries.Count;
    public void Clear() => _entries.Clear();
}
