// RecordPatches.XmlPortMetadataVirtualTable — the "XmlPort Metadata" system virtual table
// (2000000280), served by BC's OWN XmlPortDataProvider (#4461).
//
// Same shape as Query Metadata (RecordPatches.QueryMetadataVirtualTable.cs): the dispatch hands
// the table to BC's factory, and the provider's key-field-1 walk reads the object snapshot,
// which NCLMetadata_GetSnapshotOfAllObjects now fills for ObjectType.XmlPort too. Every column
// is BC's own, read off the NCLMetaXmlPort the metadata cache resolves; the one thing the
// runner supplies is that object's owning app, for the reason SetOwningApp documents.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int XmlPortMetadataVirtualTableId = 2000000280;

    private static bool IsXmlPortMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == XmlPortMetadataVirtualTableId;

    /// <summary>ObjectType.XmlPort — 6, the same constant the metadata-cache populator uses.</summary>
    private const int ObjectTypeXmlPort = 6;

    /// <summary>
    /// True for an xmlport the source-owner map does not know, but which a SOURCE-compiled app
    /// outside <paramref name="visibleApps"/> compiled — the second owner evidence the app-group
    /// filter needs here and no other inventory table does. KnownXmlPortIdSet also reads
    /// compiled <c>XmlPort{id}</c> types, and a --watch cycle clears the source owners while
    /// the previous cycle's assemblies stay loaded, so a later bundle's xmlport reached the
    /// earlier bundle's run unowned and was listed (WatchDependentBundleInventoryTests, cycle 2).
    /// The xmlports of a registered .app package are not hidden (pinned by runner-extras
    /// xmlport-metadata-floor-app); a precompiled DLL loaded with no registered .app symbols is
    /// not in that set, so its xmlports CAN be hidden from a group whose closure omits it.
    /// Pinned for ambiguous ids by xmlport-metadata-shared-id-{x,y}.
    /// </summary>
    private static bool IsCompiledXmlPortOfUnreachableSourceApp(int id, HashSet<Guid>? visibleApps,
        ref Dictionary<int, Guid>? compiledSourceOwners)
    {
        // An id two source groups both declare is ambiguous and never hidden, exactly as in
        // IsHiddenFromAppGroup — the other group's assembly is not this group's owner.
        if (visibleApps == null || _sourceObjectOwners.ContainsKey(("xmlport", id))
            || _ambiguousSourceObjects.Contains(("xmlport", id))) return false;
        if (compiledSourceOwners == null)
        {
            compiledSourceOwners = new Dictionary<int, Guid>();
            var packagedApps = new HashSet<Guid>();
            foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("xmlport app-group scope"))
                if (Guid.TryParse(symbols.AppId, out var g)) packagedApps.Add(g);
            foreach (var (asm, appId) in AlRunner.BcRuntime.RegisteredModuleAssemblies())
            {
                if (visibleApps.Contains(appId) || packagedApps.Contains(appId)) continue;
                var index = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm);
                var names = index.IsMetadataBacked
                    ? index.TypeNamesWithPrefix("XmlPort")
                    : index.EnumerateWithPrefix("XmlPort").Select(t => t.Name);
                foreach (var xmlPortId in ExtractXmlPortIdsFromNames(names))
                    compiledSourceOwners[xmlPortId] = appId;
            }
        }
        return compiledSourceOwners.ContainsKey(id);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _xmlPortOwnerAttempted = new();

    /// <summary>
    /// Whether xmlport <paramref name="id"/> resolves through the metadata cache, and, when it
    /// does, give the resolved object its owning app if it has none yet.
    ///
    /// <para>Resolved through the SKELETON cache (<c>_metaXmlPortCache</c>), never through
    /// <c>GetMetaApplicationObject</c>: the snapshot is rebuilt on every call by every snapshot
    /// consumer, and the latter loads each dependency xmlport's real metadata — measured at
    /// ~5 s added to the first Query Metadata read with Base Application loaded. It is the same
    /// instance the metadata cache later loads real metadata into, and the same null for an
    /// id that cannot be built, so the answer is the one XmlPortDataProvider's own resolution
    /// gives; the provider then pays the real load only for the rows it actually emits.</para>
    /// </summary>
    private static bool XmlPortMetadataResolves(int id, ref Dictionary<(string Kind, int Id), Guid>? ownerIndex)
    {
        var meta = _metaXmlPortCache.GetOrAdd(id, BuildNCLMetaXmlPort);
        if (meta == null) return false;
        // Once per metadata instance: an xmlport no index can place would otherwise rebuild the
        // owner index on every snapshot call.
        if (!_xmlPortOwnerAttempted.TryGetValue(meta, out _))
        {
            EnsureXmlPortOwningApp(meta, id, ref ownerIndex);
            _xmlPortOwnerAttempted.AddOrUpdate(meta, meta);
        }
        return true;
    }

    /// <summary>
    /// CLAIM — observably equivalent. XmlPortDataProvider's "App ID" column is
    /// <c>MetadataDataProvider.GetAppId(meta)</c>, i.e. <c>OwningApp.AppId</c>, which BC
    /// resolves as the app declaring the object. <see cref="BuildObjectOwnerIndex"/> answers
    /// exactly that — the SymbolReference.json of a precompiled .app, the emitted assembly of a
    /// source-compiled one — so this writes BC's answer, not a stand-in. An xmlport the index
    /// cannot place keeps the null owner, so BC's own empty-GUID answer stands.
    /// Corpus: codeunit 67450 Record_XmlPortMetadata_AppId_IsThisExtensionsOwnAppId.
    ///
    /// <para>TRAP — the name matters beyond this table: <see cref="GetOrCreateAppOwner"/>
    /// caches one owner per app id for every consumer, so an owner is written only when the
    /// app's real name is known.</para>
    /// </summary>
    private static void EnsureXmlPortOwningApp(object meta, int id,
        ref Dictionary<(string Kind, int Id), Guid>? ownerIndex)
    {
        if (ReadOwningApp(meta) != null) return;

        ownerIndex ??= BuildObjectOwnerIndex();
        if (!ownerIndex.TryGetValue(("xmlport", id), out var appId) || appId == Guid.Empty) return;
        if (KnownAppName(appId) is not { } name) return;
        SetOwningApp(meta, appId, name);
    }

    private static object? ReadOwningApp(object meta)
    {
        _fNCLMetaAppObjOwningApp ??= FindDeclaredFieldUpHierarchy(meta.GetType(), "owningApp")
            ?? throw new BcShapeGapException("xmlport-metadata-virtual-table", "NCLMetaApplicationObject",
                "owningApp field not found — the XmlPort Metadata App ID column has no source (#4461)");
        return _fNCLMetaAppObjOwningApp.GetValue(meta);
    }

    /// <summary>The name of a loaded app: its registered module assembly, else its .app.</summary>
    private static string? KnownAppName(Guid appId)
    {
        foreach (var (asm, id) in AlRunner.BcRuntime.RegisteredModuleAssemblies())
            if (id == appId)
                return AlRunner.BcRuntime.GetModuleAppInfoFor(asm).Name;
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("xmlport owning app name"))
            if (Guid.TryParse(symbols.AppId, out var g) && g == appId && !string.IsNullOrEmpty(symbols.AppName))
                return symbols.AppName;
        return null;
    }
}
