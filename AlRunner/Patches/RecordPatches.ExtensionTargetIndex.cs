// RecordPatches.ExtensionTargetIndex — how an extension row of AllObjWithCaption finds its target.
//
// "Object Subtype" of a page/table/enum/report/permission-set EXTENSION is the target object's id
// (ObjectSubtypeTextFor), found by NAME among the objects of the target kind. Each row used to
// scan every such object, so building the rows cost (extension rows x objects of the kind) — 41-56 s
// on one test that opened Production Journal (#5474).
//
// The scan has two tiers, in this order, and the first match in a tier wins:
//   1. source: the kind's parsed registry (ExtensionTargetSourceEntries);
//   2. dependency: the registered precompiled .apps (ExtensionTargetDependencyEntries), read only
//      when tier 1 misses, so an unreadable .app is refused on a miss and never on a hit.
// Inside an inventory walk each tier of a kind is turned into a name -> id map on first use, by
// walking the SAME enumerator in the SAME order and keeping the first entry per name; outside a
// walk the enumerator itself is scanned. One enumerator feeding both is what keeps the order and
// the case rule (OrdinalIgnoreCase, never the space-stripping NamesEqual) from drifting apart.
//
// The map lives for ONE walk: ListingObjectInventory drops it when the outermost walk ends, so a
// registry that grew between two walks is read again by the second.
namespace AlRunner.Patches;

public static partial class RecordPatches
{
    [ThreadStatic] private static ExtensionTargetIndex? _extensionTargetIndex;

    // Test seam (#5474): entries pulled from the enumerators below on this thread. A COUNT, so a
    // test can pin that the cost does not grow with the number of extension rows without a clock.
    [ThreadStatic] private static long _extensionTargetEntriesExamined;
    internal static long ExtensionTargetEntriesExamined => _extensionTargetEntriesExamined;

    /// <summary>First match in the source tier, then in the dependency tier; -1 when none.
    /// <paramref name="kind"/> is already normalized. The reference the walk index must equal.</summary>
    internal static int ResolveObjectIdOfKindByNameLinear(string kind, string objectName)
    {
        foreach (var (name, id) in ExtensionTargetSourceEntries(kind))
            if (string.Equals(name, objectName, StringComparison.OrdinalIgnoreCase)) return id;
        foreach (var (name, id) in ExtensionTargetDependencyEntries(kind))
            if (string.Equals(name, objectName, StringComparison.OrdinalIgnoreCase)) return id;
        return -1;
    }

    /// <summary>Tier 1: the kind's parsed registry, each entry through the executing app group's
    /// scope. Table, page, report, enum and permission set; every other kind yields nothing.</summary>
    private static IEnumerable<(string? Name, int Id)> ExtensionTargetSourceEntries(string kind)
    {
        switch (kind)
        {
            case "table":
                foreach (var t in InAppGroupScope("table", _parsedTables))
                { _extensionTargetEntriesExamined++; yield return (t.TableName, t.TableId); }
                break;
            case "page":
                foreach (var p in InAppGroupScope("page", _parsedPages))
                { _extensionTargetEntriesExamined++; yield return (p.Name, p.Id); }
                break;
            case "report":
                foreach (var r in InAppGroupScope("report", _parsedReports))
                { _extensionTargetEntriesExamined++; yield return (r.Name, r.Id); }
                break;
            case "enum":
                // AlEnumMetadataRegistry already merges this bundle's own enums with those
                // scanned out of dependency .apps, so the enum kind has no dependency tier.
                foreach (var e in AlEnumMetadataRegistry.Snapshot())
                { _extensionTargetEntriesExamined++; yield return (e.Name, e.Id); }
                break;
            case "permissionset":
                foreach (var (declKey, declValue) in _parsedObjectDecls)
                {
                    _extensionTargetEntriesExamined++;
                    var d = InAppGroupScope(declKey.Kind, declKey.Id, declValue);
                    if (NormalizeObjectTypeName(d.Kind) == "permissionset") yield return (d.Name, d.Id);
                }
                break;
        }
    }

    /// <summary>Tier 2: the registered precompiled .apps. Tables come from their typed table
    /// list, page/report/permission set from the flat object list (ids above 0 only).</summary>
    private static IEnumerable<(string? Name, int Id)> ExtensionTargetDependencyEntries(string kind)
    {
        switch (kind)
        {
            case "table":
                foreach (var t in EnumerateBcAppTableSymbols())
                { _extensionTargetEntriesExamined++; yield return (t.TableName, t.TableId); }
                break;
            case "page":
            case "report":
            case "permissionset":
                foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("extension target (AllObjWithCaption)"))
                    foreach (var o in symbols.Objects)
                    {
                        _extensionTargetEntriesExamined++;
                        if (o.Id > 0 && NormalizeObjectTypeName(o.Kind) == kind) yield return (o.Name, o.Id);
                    }
                break;
        }
    }

    /// <summary>One walk's name -> id maps, one per (tier, kind), each built on first use.</summary>
    private sealed class ExtensionTargetIndex
    {
        private readonly Dictionary<string, Dictionary<string, int>> _source = new();
        private readonly Dictionary<string, Dictionary<string, int>> _dependency = new();

        /// <summary>Same answer as <see cref="ResolveObjectIdOfKindByNameLinear"/> for the
        /// registries as they stand when each tier is first read.</summary>
        public int Resolve(string kind, string objectName)
        {
            if (TierOf(_source, kind, ExtensionTargetSourceEntries).TryGetValue(objectName, out var id)) return id;
            // Built only on a source miss, and only stored when the whole walk over the .apps
            // succeeded: a throwing read (BcAppSymbolReadException) leaves nothing behind, so the
            // next miss refuses again instead of answering from a short map (#3143).
            return TierOf(_dependency, kind, ExtensionTargetDependencyEntries).TryGetValue(objectName, out id) ? id : -1;
        }

        private static Dictionary<string, int> TierOf(
            Dictionary<string, Dictionary<string, int>> built, string kind,
            Func<string, IEnumerable<(string? Name, int Id)>> entries)
        {
            if (built.TryGetValue(kind, out var map)) return map;
            map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // TryAdd: the FIRST entry of a name wins, as the scan returns on its first match.
            foreach (var (name, id) in entries(kind))
                if (name != null) map.TryAdd(name, id);
            return built[kind] = map;
        }
    }
}
