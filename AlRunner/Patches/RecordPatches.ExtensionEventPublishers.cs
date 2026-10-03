// RecordPatches.ExtensionEventPublishers — which base object an extension's events are published
// under (#5004). docs/server-mode.md#affectedonly-and-event-subscribers has the selection side.
namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The ids of the <paramref name="baseKind"/> object (<c>Table</c>, <c>Page</c>, <c>Report</c>)
    /// that extension <paramref name="extensionId"/> of <paramref name="extensionKind"/>
    /// (<c>TableExtension</c>, <c>PageExtension</c>, <c>ReportExtension</c>) extends, from the
    /// source parse. Empty when the parse does not know the extension or its base; several when
    /// several app groups declare the base name.
    /// </summary>
    internal static IReadOnlyList<int> ExtensionBaseObjectIds(string extensionKind, int extensionId)
    {
        switch (extensionKind)
        {
            case "TableExtension":
            {
                var ids = new List<int>();
                foreach (var (baseName, extIds) in _extensionIdsByBaseTable)
                {
                    if (!extIds.Contains(extensionId)) continue;
                    foreach (var t in _parsedTables.Values)
                        if (string.Equals(t.TableName, baseName, StringComparison.OrdinalIgnoreCase) && !ids.Contains(t.TableId)
                            && ExtensionAttachesToTable(extensionId, t))
                            ids.Add(t.TableId);
                }
                return ids;
            }
            case "PageExtension":
                return _parsedPageExtensions.TryGetValue(extensionId, out var pageExt) && pageExt.BaseName.Length > 0
                    ? _parsedPages.Values.Where(p => ExtensionTargetsSourcePage(pageExt, p)).Select(p => p.Id).Distinct().ToList()
                    : Array.Empty<int>();
            case "ReportExtension":
                return _parsedReportExtensions.TryGetValue(extensionId, out var reportExt) && reportExt.BaseObjectName is { Length: > 0 } reportName
                    ? _parsedReports.Values.Where(r => NamesEqual(r.Name, reportName)).Select(r => r.Id).Distinct().ToList()
                    : Array.Empty<int>();
            default:
                return Array.Empty<int>();
        }
    }

    /// <summary>
    /// #5025: every pageextension id this run knows (source-parsed, then precompiled in a dependency
    /// .app, where a same-numbered source-parsed one wins) to the ids its base page name resolves to
    /// among the source-parsed and the dependency pages; empty when it resolves to none. Null when a
    /// dependency .app's symbols cannot be read, which is no answer rather than "no extensions".
    /// </summary>
    internal static Dictionary<int, List<int>>? PageExtensionBasePageIds()
    {
        try
        {
            var pageIdsByName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var sourcePagesByName = new Dictionary<string, List<ParsedPage>>(StringComparer.OrdinalIgnoreCase);
            var dependencyPagesByName = new Dictionary<string, List<BcAppSymbolCache.PageSymbol>>(StringComparer.OrdinalIgnoreCase);
            void Index(string name, int id)
            {
                var key = NameKey(name);
                if (!pageIdsByName.TryGetValue(key, out var ids)) pageIdsByName[key] = ids = new List<int>();
                if (!ids.Contains(id)) ids.Add(id);
            }
            foreach (var p in _parsedPages.Values)
            {
                Index(p.Name, p.Id);
                var key = NameKey(p.Name);
                if (!sourcePagesByName.TryGetValue(key, out var list)) sourcePagesByName[key] = list = new List<ParsedPage>();
                list.Add(p);
            }
            foreach (var p in DependencyPageSymbolsById().Values)
            {
                Index(p.Name, p.Id);
                var key = NameKey(p.Name);
                if (!dependencyPagesByName.TryGetValue(key, out var list)) dependencyPagesByName[key] = list = new List<BcAppSymbolCache.PageSymbol>();
                list.Add(p);
            }

            var result = new Dictionary<int, List<int>>();
            void Add(int extId, string baseName)
                => result[extId] = baseName.Length > 0 && pageIdsByName.TryGetValue(NameKey(baseName), out var ids)
                    ? ids.ToList() : new List<int>();
            // A source extension's written namespace decides between same-named pages (#5085).
            void AddSource(ParsedPage ext)
            {
                var key = NameKey(ext.BaseName);
                var ids = new List<int>();
                if (ext.BaseName.Length > 0)
                {
                    if (sourcePagesByName.TryGetValue(key, out var sources))
                        ids.AddRange(sources.Where(p => ExtensionTargetsSourcePage(ext, p)).Select(p => p.Id));
                    if (dependencyPagesByName.TryGetValue(key, out var dependencies))
                        ids.AddRange(dependencies.Where(p => ExtensionTargetsDependencyPage(ext, p.Name)).Select(p => p.Id));
                }
                result[ext.Id] = ids.Distinct().ToList();
            }
            foreach (var ext in _parsedPageExtensions.Values) AddSource(ext);
            foreach (var symbols in DependencyAppSymbols())
                foreach (var ext in symbols.PageExtensions ?? (IReadOnlyList<BcAppSymbolCache.PageExtensionSymbol>)Array.Empty<BcAppSymbolCache.PageExtensionSymbol>())
                    if (!result.ContainsKey(ext.Id)) Add(ext.Id, ext.TargetObjectName);
            return result;
        }
        catch (AlRunner.Infrastructure.BcAppSymbolReadException)
        {
            return null;
        }

        // The NamesEqual rule (case- and space-insensitive) as a dictionary key.
        static string NameKey(string name) => name.Replace(" ", "");
    }

    /// <summary>
    /// The extension ids of base object <paramref name="baseId"/> of <paramref name="baseKind"/>
    /// (<c>Table</c>, <c>Page</c>, <c>Report</c>), source-parsed and precompiled — the registries
    /// the extension instances themselves are created from.
    /// </summary>
    internal static IReadOnlyList<int> ExtensionIdsOfBaseObject(string baseKind, int baseId)
    {
        switch (baseKind)
        {
            case "Table":
                lock (_bcTableIndexLock)
                {
                    // Precompiled tableextensions reach _extensionIdsByBaseTable only through this index.
                    EnsureBcSymbolExtensionIndex();
                    if (TryGetInAppGroupScope("table", _parsedTables, baseId, out var parsed))
                        return ExtensionIdsForTable(parsed).ToList();
                    // Not parsed yet: a dependency's table, which carries no namespace (#5289).
                    var tableName = DependencyTableName(baseId);
                    return tableName != null
                           && _extensionIdsByBaseTable.TryGetValue(tableName.ToLowerInvariant(), out var extIds)
                        ? extIds.Where(e => ExtensionAttachesToTable(e, tableName, baseId, sourceParsed: false)).ToList()
                        : Array.Empty<int>();
                }
            case "Page":
                return GetPageExtensionIdsForPage(baseId);
            case "Report":
                return ReportExtensionIdsFor(baseId);
            default:
                return Array.Empty<int>();
        }
    }

    // A table that ships in a dependency .app and has not been faulted into _parsedTables yet.
    // Caller holds _bcTableIndexLock.
    private static string? DependencyTableName(int tableId)
    {
        EnsureBcSymbolTableIndex();
        return _bcSymbolTableIndex != null && _bcSymbolTableIndex.TryGetValue(tableId, out var entry)
            ? entry.Table.TableName
            : null;
    }
}
