namespace AlRunner;

/// <summary>
/// Caches rewritten Roslyn SyntaxTrees keyed by the transpiled C# code string.
/// On cache hit (C# output unchanged for a given AL object), the prior rewritten
/// tree is reused, skipping RoslynRewriter + ValueCaptureInjector for that file.
/// </summary>
public class RewriteCache
{
    private readonly Dictionary<string, CacheEntry> _entries = new();

    private sealed class CacheEntry
    {
        public required string CSharpCode { get; init; }
        public required Microsoft.CodeAnalysis.SyntaxTree RewrittenTree { get; init; }
    }

    public Microsoft.CodeAnalysis.SyntaxTree? TryGet(string objectName, string currentCSharp)
    {
        if (_entries.TryGetValue(objectName, out var entry) && entry.CSharpCode == currentCSharp)
            return entry.RewrittenTree;
        return null;
    }

    public void Store(string objectName, string csharpCode, Microsoft.CodeAnalysis.SyntaxTree rewrittenTree)
    {
        _entries[objectName] = new CacheEntry
        {
            CSharpCode = csharpCode,
            RewrittenTree = rewrittenTree
        };
    }

    public int Count => _entries.Count;
    public void Clear() => _entries.Clear();
}
