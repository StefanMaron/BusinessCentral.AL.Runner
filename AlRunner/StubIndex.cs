using System.Text.RegularExpressions;

namespace AlRunner;

/// <summary>
/// Lightweight index of stub AL files grouped by (objectType, objectName)
/// so the runner can point at a namespace mismatch when a referenced stub
/// is declared under a different namespace than the consumer expects.
///
/// Only covers the diagnostic-hint path — the real compile still happens
/// through BC's normal symbol loader.
/// </summary>
public static class StubIndex
{
    // Capture: <namespace>;  (first match wins per file)
    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Capture: <type> <id> "<name>" | <type> <id> <name>
    // Supports codeunit / table / enum / interface / page / report / xmlport.
    private static readonly Regex ObjectDeclRegex = new(
        @"^\s*(codeunit|table|enum|interface|page|report|xmlport|query|pageextension|tableextension)\s+\d+\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public record StubEntry(string ObjectType, string ObjectName, string? Namespace, string SourcePath);

    private static readonly List<StubEntry> _entries = new();

    /// <summary>Clear the in-memory index (used by server mode between runs).</summary>
    public static void Clear() => _entries.Clear();

    /// <summary>Parse a stub file's header and register its declared objects.</summary>
    public static void Record(string sourcePath, string text)
    {
        var ns = NamespaceRegex.Match(text);
        string? nsName = ns.Success ? ns.Groups[1].Value : null;

        foreach (Match m in ObjectDeclRegex.Matches(text))
        {
            var type = m.Groups[1].Value.ToLowerInvariant();
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            _entries.Add(new StubEntry(type, name, nsName, sourcePath));
        }
    }

    /// <summary>
    /// Look up any stub entries whose type+name match the given key.
    /// Used to enrich missing-dependency diagnostics.
    /// </summary>
    public static IReadOnlyList<StubEntry> Find(string objectType, string objectName)
    {
        var type = objectType.ToLowerInvariant();
        return _entries
            .Where(e => e.ObjectType == type &&
                        string.Equals(e.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static bool HasAny => _entries.Count > 0;
}
