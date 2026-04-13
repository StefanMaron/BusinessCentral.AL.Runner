using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner;

/// <summary>
/// Caches parsed AL SyntaxTrees by file path. On cache hit (file mtime unchanged),
/// returns the cached tree without calling ParseObjectText.
/// Server-mode only — CLI mode passes null (no benefit for single-shot).
/// </summary>
public class SyntaxTreeCache
{
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheEntry
    {
        public required DateTime LastModifiedUtc { get; set; }
        public required string ContentHash { get; init; }
        public required SyntaxTree Tree { get; init; }
    }

    /// <summary>
    /// Get cached tree if file hasn't changed, or parse and cache.
    /// </summary>
    public SyntaxTree GetOrParse(string filePath, string content)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(filePath); }
        catch { return ParseAndStore(filePath, content); }

        if (_entries.TryGetValue(filePath, out var entry))
        {
            if (entry.LastModifiedUtc == mtime)
                return entry.Tree;

            var hash = ComputeHash(content);
            if (entry.ContentHash == hash)
            {
                entry.LastModifiedUtc = mtime;
                return entry.Tree;
            }
        }

        return ParseAndStore(filePath, content);
    }

    private SyntaxTree ParseAndStore(string filePath, string content)
    {
        var tree = SyntaxTree.ParseObjectText(content);
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(filePath); }
        catch { mtime = DateTime.MinValue; }

        _entries[filePath] = new CacheEntry
        {
            LastModifiedUtc = mtime,
            ContentHash = ComputeHash(content),
            Tree = tree
        };
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
