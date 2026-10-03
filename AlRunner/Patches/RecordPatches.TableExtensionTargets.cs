// RecordPatches.TableExtensionTargets: which table a SOURCE tableextension extends, resolved by the
// namespace its `extends` clause writes (#5289, the table twin of #5085's pageextension predicates).
// docs/tableextension-binding.md has the rule and what it leaves open.
using System.Collections.Concurrent;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// How a source tableextension's <c>extends</c> clause names its table, and the file scope the
    /// clause was written in. <paramref name="BaseNamespace"/> is the namespace written in front of
    /// the name, null for a bare name; <paramref name="Namespace"/> and <paramref name="Usings"/> are
    /// the declaring file's own.
    /// </summary>
    internal sealed record TableExtensionTarget(
        string BaseName, string? BaseNamespace, string? Namespace, IReadOnlyList<string> Usings);

    // Written only by MergeExtensionFields, for extensions parsed from AL source. A precompiled
    // dependency's tableextension has no entry: its symbol states a name only, so it keeps matching
    // every table of that name (the page side's #5288 item 3, unchanged here).
    private static readonly Dictionary<int, TableExtensionTarget> _tableExtensionTargets = new();

    // One extension's own fields and keys, so a table of a shared name can take only the ones its
    // extensions declared. The name-keyed _parsedExtensionFields/_parsedExtensionKeys pool every
    // extension of a name and de-duplicate by field id, which drops a second extension's field that
    // reuses an id the first declared on a DIFFERENT table.
    private static readonly Dictionary<int, (List<ParsedField> Fields, List<ParsedExtensionKey> Keys)>
        _extensionContributions = new();

    // Source table ids each scoped extension resolves to, memoised because the question is asked per
    // record creation. Cleared whenever the answer can move: a table parsed, an extension merged, a
    // reload (InvalidateExtensionTargetCache).
    private static readonly ConcurrentDictionary<int, IReadOnlyList<int>> _extensionSourceTargetCache = new();

    private static void InvalidateExtensionTargetCache() => _extensionSourceTargetCache.Clear();

    private static bool NamespaceEquals(string? left, string? right)
        => string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ids of the source-parsed tables extension <paramref name="extensionId"/> extends. A written
    /// namespace picks the table of that name in that namespace. A bare name picks the table of that
    /// name in the extension's own namespace, which the compiler resolves first, and otherwise the
    /// one in the global namespace or a namespace the file imports with <c>using</c> (the order
    /// <see cref="ResolveInFileScope"/> applies to a table's own names, #4133). Empty means the
    /// clause names a dependency's table.
    /// </summary>
    private static IReadOnlyList<int> SourceTableIdsTargetedBy(int extensionId, TableExtensionTarget ext)
        => _extensionSourceTargetCache.GetOrAdd(extensionId, _ =>
        {
            var named = InAppGroupScope("table", _parsedTables)
                .Where(t => t.Usings != null
                    && string.Equals(t.TableName, ext.BaseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            IEnumerable<ParsedTable> hit;
            if (ext.BaseNamespace != null)
                hit = named.Where(t => NamespaceEquals(t.Namespace, ext.BaseNamespace));
            else
            {
                var own = named.Where(t => NamespaceEquals(t.Namespace, ext.Namespace)).ToList();
                hit = own.Count > 0
                    ? own
                    : named.Where(t => t.Namespace == null
                        || ext.Usings.Contains(t.Namespace, StringComparer.OrdinalIgnoreCase));
            }
            return hit.Select(t => t.TableId).Distinct().ToList();
        });

    /// <summary>
    /// Whether tableextension <paramref name="extensionId"/> extends the table of that name and id.
    /// <paramref name="sourceParsed"/> is true for a table parsed from AL source, whose namespace is
    /// known, and false for one read from a dependency's symbols, which carries none: a dependency
    /// table is extended only when the clause resolves to no source table, so the namespace written
    /// in front of a dependency's name is not compared and two dependencies sharing a table name are
    /// not told apart (#5288 item 2 is the page-side statement of that limit).
    /// An extension with no recorded target is matched by name, as before.
    /// </summary>
    internal static bool ExtensionAttachesToTable(int extensionId, string tableName, int tableId, bool sourceParsed)
    {
        if (!_tableExtensionTargets.TryGetValue(extensionId, out var ext)) return true;
        if (!string.Equals(ext.BaseName, tableName, StringComparison.OrdinalIgnoreCase)) return false;
        var sources = SourceTableIdsTargetedBy(extensionId, ext);
        return sourceParsed ? sources.Contains(tableId) : sources.Count == 0;
    }

    internal static bool ExtensionAttachesToTable(int extensionId, ParsedTable table)
        => ExtensionAttachesToTable(extensionId, table.TableName, table.TableId, table.Usings != null);

    /// <summary>
    /// The tableextension ids registered for <paramref name="table"/>'s name that extend this table,
    /// in merge order. <paramref name="narrowed"/> is true when a registered extension of the name was
    /// left out, which is the only case the name-pooled field and key lists cannot answer.
    /// </summary>
    internal static IReadOnlyList<int> ExtensionIdsForTable(ParsedTable table, out bool narrowed)
    {
        narrowed = false;
        if (!_extensionIdsByBaseTable.TryGetValue(table.TableName.ToLowerInvariant(), out var all))
            return Array.Empty<int>();
        if (_tableExtensionTargets.Count == 0) return all;
        var attached = new List<int>(all.Count);
        foreach (var id in all)
            if (ExtensionAttachesToTable(id, table)) attached.Add(id);
        narrowed = attached.Count != all.Count;
        return narrowed ? attached : all;
    }

    internal static IReadOnlyList<int> ExtensionIdsForTable(ParsedTable table)
        => ExtensionIdsForTable(table, out _);

    /// <summary>
    /// The fields the tableextensions of <paramref name="table"/> add. The name-pooled list unless an
    /// extension of the name targets another table, then only this table's extensions' own.
    /// </summary>
    internal static IReadOnlyList<ParsedField> ExtensionFieldsFor(ParsedTable table)
    {
        var ids = ExtensionIdsForTable(table, out var narrowed);
        if (!narrowed)
            return _parsedExtensionFields.TryGetValue(table.TableName.ToLowerInvariant(), out var pooled)
                ? pooled : Array.Empty<ParsedField>();
        var result = new List<ParsedField>();
        var seen = new HashSet<int>();
        foreach (var id in ids)
            if (_extensionContributions.TryGetValue(id, out var own))
                foreach (var f in own.Fields)
                    if (seen.Add(f.FieldId)) result.Add(f);
        return result;
    }

    /// <summary>The keys the tableextensions of <paramref name="table"/> declare; as
    /// <see cref="ExtensionFieldsFor"/>.</summary>
    internal static IReadOnlyList<ParsedExtensionKey> ExtensionKeysFor(ParsedTable table)
    {
        var ids = ExtensionIdsForTable(table, out var narrowed);
        if (!narrowed)
            return _parsedExtensionKeys.TryGetValue(table.TableName.ToLowerInvariant(), out var pooled)
                ? pooled : Array.Empty<ParsedExtensionKey>();
        var result = new List<ParsedExtensionKey>();
        foreach (var id in ids)
            if (_extensionContributions.TryGetValue(id, out var own))
                foreach (var k in own.Keys) AddExtensionKeyDeduped(result, k);
        return result;
    }

    private static void AddExtensionKeyDeduped(List<ParsedExtensionKey> into, ParsedExtensionKey key)
    {
        if (key.FieldNames.Count == 0) return;
        // The composition is part of the identity: two DIFFERENT extensions may each declare a key
        // called "Key1", so the name alone would drop the second (#3216).
        if (into.Any(e => string.Equals(e.Name, key.Name, StringComparison.OrdinalIgnoreCase)
                && e.FieldNames.Count == key.FieldNames.Count
                && e.FieldNames.Zip(key.FieldNames, (a, b) =>
                       string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(x => x)))
            return;
        into.Add(key);
    }

    /// <summary>
    /// The (app, modify) record of every tableextension that extends <paramref name="table"/>, for
    /// <see cref="ShouldBuildTableFromBcDocument"/>: another table's extension of the same name must
    /// not decide which route this table is built on.
    /// </summary>
    internal static IEnumerable<(Guid? OwningAppId, bool HasModify)> ExtensionSourceInfoFor(ParsedTable table)
    {
        if (!_extensionSourceInfo.TryGetValue(table.TableName.ToLowerInvariant(), out var all))
            yield break;
        foreach (var (owningAppId, hasModify, extensionId) in all)
            if (ExtensionAttachesToTable(extensionId, table))
                yield return (owningAppId, hasModify);
    }
}
