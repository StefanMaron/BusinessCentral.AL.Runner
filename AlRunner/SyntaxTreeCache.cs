using System.Collections.Concurrent;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

/// <summary>
/// Caches parsed AL SyntaxTrees by file path, keyed on content hash.
/// Thread-safe for use from Parallel.For via ConcurrentDictionary.
/// Server-mode only — CLI mode passes null (no benefit for single-shot).
/// </summary>
public class SyntaxTreeCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheEntry
    {
        public required string ContentHash { get; init; }
        public required SyntaxTree Tree { get; init; }
    }

    /// <summary>
    /// Get cached tree if content hasn't changed, or parse and cache.
    /// </summary>
    public SyntaxTree GetOrParse(string filePath, string content)
    {
        var hash = ComputeHash(content);
        if (_entries.TryGetValue(filePath, out var entry) && entry.ContentHash == hash)
            return entry.Tree;

        var tree = SyntaxTree.ParseObjectText(content);
        _entries[filePath] = new CacheEntry { ContentHash = hash, Tree = tree };
        return tree;
    }

    public int Count => _entries.Count;
    public void Clear() => _entries.Clear();

    private static string ComputeHash(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}
