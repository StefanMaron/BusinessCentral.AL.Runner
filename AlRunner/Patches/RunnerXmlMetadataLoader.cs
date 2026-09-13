// RunnerXmlMetadataLoader — a real INCLMetaApplicationObjectLoader /
// INCLObjectXmlMetadataLoader backed by AlReportMetadataRegistry, so
// NCLMetaReport.LoadMetadata() (via GetMetadataFromLoader() ->
// ObjectLoader.XmlMetadataLoader.GetMetaObjectXmlMetadata) can build a real
// MetaReport for reports the runner source-compiled.
//
// Why this exists (root cause, verified via ilspycmd decompile of Ncl.dll):
//   RecordPatches.NclMetaFormReportBuilder.BuildNCLMetaReport builds skeleton
//   NCLMetaReport entries via NCLMetaReport.CreateEmptyNCLMetaReport(loader,
//   id, appGroup, ...), historically passing loader=null — safe for the
//   Populate()/CompileAndLoadClrObject() no-op'd paths that entry originally
//   served (NCLMetadata.GetMetaApplicationObject succeeding is enough for
//   those). But NavGlobal.MetadataProvider.GetReportMetadata(id) (reached
//   from AL via Report.WordXmlPart / Report.DefaultLayout / any AL surface
//   that needs a report's real dataset/column shape) calls
//   NCLMetaReport.LoadMetadata() -> GetMetadataFromLoader() ->
//   ObjectLoader.XmlMetadataLoader.GetMetaObjectXmlMetadata(...) — a genuine
//   NullReferenceException when ObjectLoader (=the ctor's `loader` param) is
//   null. This is DISTINCT from the precompiled-dependency stub-metadata gap
//   (NavReportSync.cs): that one is for reports the runner NEVER compiles;
//   this one is for reports the runner DOES compile (AlReportMetadataRegistry
//   already has the real emit-captured XML for them — see BcCompiler.
//   CaptureOutputter.AddApplicationObject) but whose NCLMetaReport skeleton
//   was never given a loader that can hand that XML back on request.
//
// Faithfulness: GetMetaObjectXmlMetadata returns the SAME metadata XML BC's
// own MetaReport(XmlElement, ...) ctor parses elsewhere in this runner
// (NavReportSync.GetRealMetaReport) — this is not a different/looser shape,
// it is literally the emit-captured metadata for the SAME report id. Objects
// the registry has no entry for (never compiled, or a non-report type) throw
// loudly — never a silent empty/default document (loud-failures rule).
//
// Scope (#3599): report, page and xmlport keep their own per-kind registries above;
// table (#3552) and every other kind AlObjectMetadataRegistry holds (#3548) are served
// by the general fallback at the bottom of GetMetaObjectXmlMetadata. A kind neither
// registry has an entry for still throws RunnerOutOfScopeException rather than
// silently returning something wrong.
//
// Implemented directly (no runtime DispatchProxy — tried first, but produced
// unexplained null returns from CreateEmptyNCLMetaReport's factory Invoke
// under this Cecil/R2R-patched runtime; direct implementation sidesteps that
// class of risk entirely and is simpler to read).
using Microsoft.Dynamics.Nav.Apps.MetadataDeltas;
using Microsoft.Dynamics.Nav.Apps.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Apps;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

/// <summary>
/// Real INCLObjectXmlMetadataLoader backed by BC's own emit-captured metadata:
/// AlReportMetadataRegistry / AlPageMetadataRegistry / AlXmlPortMetadataRegistry per kind,
/// AlObjectMetadataRegistry (#3548) for table and everything else it holds (#3599).
/// </summary>
public sealed class RunnerXmlMetadataLoader : INCLObjectXmlMetadataLoader
{
    // BC's runtime ObjectType and the AL compiler's SymbolKind (the string the registry is
    // keyed by) are two different enums; they name every captured kind identically except
    // ObjectType.CodeUnit vs SymbolKind.Codeunit. Report/Page/Table/XmlPort are absent
    // because the branches above already serve them from their own registries.
    // see docs/object-metadata-capture.md#objecttype-vs-symbolkind
    private static readonly System.Collections.Generic.IReadOnlyDictionary<ObjectType, string>
        RegistryKindByObjectType = new System.Collections.Generic.Dictionary<ObjectType, string>
        {
            [ObjectType.CodeUnit] = "Codeunit",
            [ObjectType.Query] = "Query",
            [ObjectType.Enum] = "Enum",
            [ObjectType.EnumExtension] = "EnumExtension",
            [ObjectType.PermissionSet] = "PermissionSet",
            [ObjectType.PermissionSetExtension] = "PermissionSetExtension",
            [ObjectType.TableExtension] = "TableExtension",
            [ObjectType.PageExtension] = "PageExtension",
            [ObjectType.ReportExtension] = "ReportExtension",
        };

    public NCLObjectXmlMetadata GetMetaObjectXmlMetadata(ApplicationObjectId objectId, NavAppGroup appGroup)
    {
        // MetadataHash is a cache-invalidation key only (the real
        // NCLObjectXmlMetadataLoader hashes the app's own object summary); a stable
        // per-object string is faithful enough since the runner has no
        // republish/versioning concept to invalidate against.
        if (objectId.ObjectType == ObjectType.Report
            && AlReportMetadataRegistry.TryGet(objectId.ObjectNumber, out var reportXml))
            return Wrap(reportXml, $"runner-report-{objectId.ObjectNumber}");

        // Pages: the same emit-captured metadata XML, feeding NCLMetaForm.LoadMetadata()
        // so the page gets its real control tree instead of an empty skeleton. That tree
        // is what NavForm registers its source expressions from, and therefore the only
        // route by which a TestPage control bound to a page variable (rather than to a
        // Rec field) can resolve to anything at all.
        if (objectId.ObjectType == ObjectType.Page
            && AlPageMetadataRegistry.TryGet(objectId.ObjectNumber, out var pageXml))
            return Wrap(pageXml, $"runner-page-{objectId.ObjectNumber}");

        // Pages living in a PRECOMPILED dependency .app: never source-compiled, so the
        // emit registry above never holds them. Reconstruct a minimal, honest metadata
        // document (PageType + SourceObject only) from the .app's own SymbolReference.json
        // rather than refuse — see DependencyPageMetadataXml.cs for exactly what is read
        // and what is deliberately left unstated.
        if (objectId.ObjectType == ObjectType.Page
            && RecordPatches.TryBuildDependencyPageMetadata(objectId.ObjectNumber) is { } depPageXml)
            return Wrap(depPageXml, $"runner-dep-page-{objectId.ObjectNumber}");

        // XmlPorts: same emit-captured metadata XML, feeding NCLMetaXmlPort.LoadMetadata()
        // so BC's own XmlPort engine imports/exports against the port's real node schema
        // instead of NREing on an empty skeleton.
        if (objectId.ObjectType == ObjectType.XmlPort
            && AlXmlPortMetadataRegistry.TryGet(objectId.ObjectNumber, out var xmlPortXml))
            return Wrap(xmlPortXml, $"runner-xmlport-{objectId.ObjectNumber}");

        // Tables: BC's own emitted metadata document, kept per (kind, id) by #3548. This is
        // the entry point for NCLMetaTable.LoadMetadata() — MetaObjectCache.GetMetaTable
        // hands what we return here to MetaTable.CreateMetaTableFromXml, so BC constructs
        // the table's fields, keys, relations and captions from its own bytes instead of
        // the runner deriving them (#3552).
        if (objectId.ObjectType == ObjectType.Table
            && AlObjectMetadataRegistry.TryGet(
                RecordPatches.BcTableMetadataKind, objectId.ObjectNumber, out var tableXml))
            return Wrap(tableXml, $"runner-table-{objectId.ObjectNumber}");

        // Reports living in a PRECOMPILED dependency .app: never source-compiled, so the
        // emit registry above will never hold them. Their shape is still fully stated by
        // the .app itself (SymbolReference.json + the embedded AL source), so reconstruct
        // the metadata document from those rather than refuse — see
        // DependencyReportMetadata.cs for exactly what is read and what is left unstated.
        if (objectId.ObjectType == ObjectType.Report
            && RecordPatches.TryBuildDependencyReportMetadata(objectId.ObjectNumber) is { } depXml)
            return Wrap(depXml, $"runner-dep-report-{objectId.ObjectNumber}");

        // Every other kind AlObjectMetadataRegistry holds (#3548, #3599): codeunit, query,
        // enum(extension), permission set(extension), table extension, page extension, report
        // extension. Same registry #3552 reads for tables, same shape — BC's own emitted
        // document, keyed by (kind, id), served back to BC's own MetaObjectCache /
        // NCLObjectMetadataLoaderExtensions so it builds the real MetaCodeunit / MetaQuery /
        // MetaEnum / … instead of a runner-derived approximation.
        if (RegistryKindByObjectType.TryGetValue(objectId.ObjectType, out var kind)
            && AlObjectMetadataRegistry.TryGet(kind, objectId.ObjectNumber, out var kindXml))
            return Wrap(kindXml, $"runner-{kind.ToLowerInvariant()}-{objectId.ObjectNumber}");

        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata({objectId.ObjectType} {objectId.ObjectNumber})",
            "not-yet-implemented — no metadata XML for this object: it was not source-compiled " +
            "by the runner, and no loaded dependency .app declares it");
    }

    private static NCLObjectXmlMetadata Wrap(string xml, string metadataHash)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        return new NCLObjectXmlMetadata(doc, NavText.Create(metadataHash));
    }

    public NCLObjectXmlMetadata GetSystemTableMetaObjectXmlMetadataFromApplicationDatabase(ApplicationObjectId objectId) =>
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "INCLObjectXmlMetadataLoader.GetSystemTableMetaObjectXmlMetadataFromApplicationDatabase",
            "not-yet-implemented — system-table (2000000071) metadata-from-application-database lookup is not wired");

    public NavAppObjectMetadataRuntimeDeltas GetExtensionDeltasForAppObject(ApplicationObjectId objectId, NavAppRuntimeMetadata runtimeAppMetadata) =>
        // No extension-runtime-delta tracking in the runner (no published-app
        // extension pipeline) — null is BC's own "no deltas" value too (see
        // the real NCLObjectXmlMetadataLoader: it only calls the retriever
        // when a matching Extension-format summary exists; absent that, it
        // also returns null).
        null!;
}

/// <summary>
/// Minimal INCLMetaApplicationObjectLoader. Only XmlMetadataLoader is
/// meaningfully implementable from what the runner tracks; every other
/// member throws loudly if ever touched (none of the code paths that need
/// this loader — NCLMetaReport.LoadMetadata/GetMetadataFromLoader — read
/// them today).
/// </summary>
public sealed class RunnerMetaApplicationObjectLoader : INCLMetaApplicationObjectLoader
{
    public INCLObjectXmlMetadataLoader XmlMetadataLoader { get; } = new RunnerXmlMetadataLoader();

    public INCLCodeLoader CodeLoader =>
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "INCLMetaApplicationObjectLoader.CodeLoader",
            "not-yet-implemented — runner metadata loader only implements XmlMetadataLoader");

    public NCLMetadata MetadataCache =>
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "INCLMetaApplicationObjectLoader.MetadataCache",
            "not-yet-implemented — runner metadata loader only implements XmlMetadataLoader");

    // BC's OWN MetaObjectCache, constructed over our XML loader. NCLMetaForm's page path
    // does NOT go through XmlMetadataLoader the way the report path does — it calls
    // ObjectLoader.MetaObjectCache.GetMetaPage(id, appGroup), which is what turns the
    // metadata XML into a real MetaPageDefinition. MetaObjectCache's only constructor
    // dependency is an INCLObjectXmlMetadataLoader, which is exactly what we already have,
    // so the parsing is BC's rather than a reimplementation of it.
    //
    // Resolved by reflection because MetaObjectCache is internal to Ncl. Built lazily and
    // once: it is a cache, so a fresh instance per call would defeat its purpose.
    private IMetaObjectCache? _metaObjectCache;
    private readonly object _metaObjectCacheLock = new();

    public IMetaObjectCache MetaObjectCache
    {
        get
        {
            if (_metaObjectCache != null) return _metaObjectCache;
            lock (_metaObjectCacheLock)
            {
                if (_metaObjectCache != null) return _metaObjectCache;
                var type = typeof(NCLMetadata).Assembly
                    .GetType("Microsoft.Dynamics.Nav.Runtime.MetaObjectCache")
                    ?? throw new InvalidOperationException(
                        "MetaObjectCache type not found in Ncl — BC metadata shape changed");
                var ctor = type.GetConstructor(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance,
                    binder: null, types: new[] { typeof(INCLObjectXmlMetadataLoader), typeof(bool) },
                    modifiers: null)
                    ?? throw new InvalidOperationException(
                        "MetaObjectCache(INCLObjectXmlMetadataLoader, bool) ctor not found — BC metadata shape changed");
                _metaObjectCache = (IMetaObjectCache)ctor.Invoke(new object?[] { XmlMetadataLoader, false });
                return _metaObjectCache;
            }
        }
    }

    /// <summary>
    /// Drop the MetaObjectCache on a bundle reload (#4100). It keys on the object owner's
    /// runtime package id, which the runner's app group never supplies, so every app's page N
    /// shares the key (Guid.Empty, N) and the first bundle's parsed definition answered every
    /// later bundle. The next read rebuilds from the registries, which the reload repopulated.
    /// </summary>
    internal void ResetMetaObjectCache()
    {
        lock (_metaObjectCacheLock) _metaObjectCache = null;
    }

    public INavAppClrTypeRetriever AppClrTypeRetriever =>
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "INCLMetaApplicationObjectLoader.AppClrTypeRetriever",
            "not-yet-implemented — runner metadata loader only implements XmlMetadataLoader");

    // Single shared instance. The XML loader half is stateless (every call re-resolves
    // against the registries, which are the source of truth); the MetaObjectCache half is
    // deliberately NOT — it is BC's own cache and must be shared to be one.
    public static readonly RunnerMetaApplicationObjectLoader Instance = new();
}
