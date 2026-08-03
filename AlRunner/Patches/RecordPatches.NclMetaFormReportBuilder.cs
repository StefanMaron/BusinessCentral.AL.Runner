// RecordPatches.NclMetaFormReportBuilder — turns ParsedPage / ParsedReport
// into skeleton NCLMetaForm / NCLMetaReport instances, suitable for inserting
// into NCLMetadata.metadataCacheEntries[Page] / [Report].
//
// Strategy: NCLMetaForm and NCLMetaReport both expose internal static factory
// methods `CreateEmptyNCLMetaForm` / `CreateEmptyNCLMetaReport` that take only
// (loader, id, NavAppGroup appGroup, depOrder, alNamespace). The result has
// `objectId` / `metadataAppGroup` populated but no MetaPageDefinition /
// MetaReportDefinition — which is fine for our needs:
//
//   • The cache slot just has to be non-null so
//     `NCLMetadata.GetMetaApplicationObjectInternal` finds an entry instead of
//     throwing `NavNCLApplicationObjectNotFoundException`.
//   • Every property getter on NCLMetaForm that touches
//     `metadataAppGroupPageDefinition.Item` is gated by Populate() / runtime
//     code paths we already JMP-hook to no-op (§O).
//   • `ApplicationObjectClrType` is JMP-hooked elsewhere and looks up
//     `Form{N}` / `Report{N}` from the loaded test assembly (extended below).
//
// The loader can be passed as null because the §O Populate / CompileAndLoadClrObject
// JMP no-ops mean the loader is never dereferenced after construction.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Parsed page/report/query/xmlport tables, mirror of _parsedTables.
    private static readonly Dictionary<int, ParsedPage> _parsedPages = new();
    private static readonly Dictionary<int, ParsedReport> _parsedReports = new();
    // Separate id namespace: reportextension N may legally reuse a `report` id.
    private static readonly Dictionary<int, ParsedReport> _parsedReportExtensions = new();
    private static readonly Dictionary<int, ParsedQuery> _parsedQueries = new();
    private static readonly Dictionary<int, ParsedXmlPort> _parsedXmlPorts = new();

    // Cache: id → NCLMeta{Form|Report|Query|XmlPort} instance.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _metaFormCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _metaReportCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _metaQueryCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _metaXmlPortCache = new();

    // Type/method handles resolved lazily.
    private static Type? _tNCLMetaForm;
    private static Type? _tNCLMetaReport;
    private static Type? _tNCLMetaQuery;
    private static Type? _tNCLMetaXmlPort;
    private static MethodInfo? _mCreateEmptyNCLMetaForm;
    private static MethodInfo? _mCreateEmptyNCLMetaReport;
    private static MethodInfo? _mCreateEmptyNCLMetaQuery;
    private static MethodInfo? _mCreateEmptyNCLMetaXmlPort;
    private static Type? _tApplicationObjectId;
    private static Type? _tObjectTypeEnum;
    private static object? _baseAppGroup;

    private static void EnsureFormReportReflection()
    {
        if (_tNCLMetaForm != null && _tNCLMetaReport != null
            && _tNCLMetaQuery != null && _tNCLMetaXmlPort != null) return;
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;

        _tNCLMetaForm = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaForm");
        _tNCLMetaReport = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaReport");
        _tNCLMetaQuery = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaQuery");
        _tNCLMetaXmlPort = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaXmlPort");

        // Factories are `internal static`.
        _mCreateEmptyNCLMetaForm = _tNCLMetaForm?.GetMethod("CreateEmptyNCLMetaForm",
            BindingFlags.NonPublic | BindingFlags.Static);
        _mCreateEmptyNCLMetaReport = _tNCLMetaReport?.GetMethod("CreateEmptyNCLMetaReport",
            BindingFlags.NonPublic | BindingFlags.Static);
        _mCreateEmptyNCLMetaXmlPort = _tNCLMetaXmlPort?.GetMethod("CreateEmptyNCLMetaXmlPort",
            BindingFlags.NonPublic | BindingFlags.Static);

        // NCLMetaQuery has two CreateEmptyNCLMetaQuery overloads — the (loader,
        // ApplicationObjectId, NavAppGroup, int, string) one is the analog of
        // Form/Report and the only one we want.
        if (_tNCLMetaQuery != null)
        {
            foreach (var m in _tNCLMetaQuery.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "CreateEmptyNCLMetaQuery") continue;
                var ps = m.GetParameters();
                if (ps.Length == 5 && ps[1].ParameterType.Name == "ApplicationObjectId")
                { _mCreateEmptyNCLMetaQuery = m; break; }
            }
        }

        // NavAppGroup.BaseGroup
        var tAppGroup = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup");
        _baseAppGroup = tAppGroup?.GetProperty("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? tAppGroup?.GetField("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        // ApplicationObjectId(ObjectType, int) — Microsoft.Dynamics.Nav.Types
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        _tApplicationObjectId = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.ApplicationObjectId");
        _tObjectTypeEnum = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.ObjectType");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? BuildNCLMetaForm(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var parsed)) return null;
        EnsureFormReportReflection();
        if (_mCreateEmptyNCLMetaForm == null) return null;

        try
        {
            // (loader, id, appGroup, depOrder=-1, alNamespace="")
            //
            // loader = RunnerMetaApplicationObjectLoader.Instance for the same reason the
            // report builder passes it: NCLMetaForm.LoadMetadata() -> GetMetadataFromLoader()
            // -> ObjectLoader.XmlMetadataLoader.GetMetaObjectXmlMetadata(...) dereferences it,
            // and a null loader NREs there. It answers from AlPageMetadataRegistry — the
            // emit-captured page metadata XML — so the page gets its REAL control tree.
            var meta = _mCreateEmptyNCLMetaForm.Invoke(null,
                new object?[] { RunnerMetaApplicationObjectLoader.Instance, pageId, _baseAppGroup, -1, string.Empty });

            // Mark metadataLoaded=true on the freshly-built skeleton so the shared
            // NCLMetaApplicationObject.Populate path is skipped (in addition to the
            // JMP-hook NoOp installed in §O).
            //
            // This still happens unconditionally, and deliberately: this builder runs at
            // Register() time, BEFORE the compile that captures page metadata XML, so
            // "does this page have metadata?" is always false here and deciding on it
            // would be deciding on a value that is not yet knowable. Pages are opted into
            // a REAL metadata load later and on demand, by EnsureRealPageMetadata below.
            EnsureCachePopulatorReflection();
            if (meta != null && _fNCLMetaAppObjMetadataLoaded != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

            return meta;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BuildNCLMetaForm({pageId}) failed: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? BuildNCLMetaReport(int reportId)
    {
        // Existence check only — nothing below reads the parsed source. It covers every
        // report the runner knows about, INCLUDING ones that live in a precompiled
        // dependency: a Base Application report needs this skeleton just as much as one we
        // compiled, and until now it never got one. (That gap was masked: the AL report
        // parser used to match prose in doc comments, so `/// ... report 1306 "Standard
        // Sales - Invoice": ...` fabricated a _parsedReports entry for 1306 and this
        // builder happened to accept it. Tightening the parser removed the accident and
        // exposed the real gap — hence the widened check here.)
        if (!KnownReportIdSet().Contains(reportId)) return null;
        EnsureFormReportReflection();
        if (_mCreateEmptyNCLMetaReport == null) return null;

        try
        {
            // loader = RunnerMetaApplicationObjectLoader.Instance (not null): NCLMetaReport.
            // LoadMetadata() -> GetMetadataFromLoader() -> ObjectLoader.XmlMetadataLoader.
            // GetMetaObjectXmlMetadata(...) dereferences this loader for any AL surface that
            // needs the report's real dataset shape (Report.WordXmlPart, NavGlobal.
            // MetadataProvider.GetReportMetadata(id), …) — see RunnerXmlMetadataLoader.cs for
            // the root-cause writeup. A null loader NREs there; this one answers from
            // AlReportMetadataRegistry (the same emit-captured XML NavReportSync already uses).
            var meta = _mCreateEmptyNCLMetaReport.Invoke(null,
                new object?[] { RunnerMetaApplicationObjectLoader.Instance, reportId, _baseAppGroup, -1, string.Empty });

            EnsureCachePopulatorReflection();
            if (meta != null && _fNCLMetaAppObjMetadataLoaded != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

            return meta;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BuildNCLMetaReport({reportId}) failed: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? BuildNCLMetaQuery(int queryId)
    {
        if (!_parsedQueries.TryGetValue(queryId, out var parsed)) return null;
        EnsureFormReportReflection();
        if (_mCreateEmptyNCLMetaQuery == null
            || _tApplicationObjectId == null || _tObjectTypeEnum == null) return null;

        try
        {
            // Build ApplicationObjectId(ObjectType.Query=9, queryId).
            var queryEnumVal = Enum.ToObject(_tObjectTypeEnum, 9);
            var appObjId = Activator.CreateInstance(_tApplicationObjectId, queryEnumVal, queryId);

            // CreateEmptyNCLMetaQuery(loader, ApplicationObjectId, NavAppGroup, int, string)
            var meta = _mCreateEmptyNCLMetaQuery.Invoke(null,
                new object?[] { null, appObjId, _baseAppGroup, -1, string.Empty });

            EnsureCachePopulatorReflection();
            if (meta != null && _fNCLMetaAppObjMetadataLoaded != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

            return meta;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BuildNCLMetaQuery({queryId}) failed: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? BuildNCLMetaXmlPort(int xmlPortId)
    {
        if (!_parsedXmlPorts.TryGetValue(xmlPortId, out var parsed)) return null;
        EnsureFormReportReflection();
        if (_mCreateEmptyNCLMetaXmlPort == null) return null;

        try
        {
            // (loader, xmlPortId, appGroup, depOrder=-1, alNamespace="")
            //
            // loader = RunnerMetaApplicationObjectLoader.Instance for the same reason the
            // report and page builders pass it: NCLMetaXmlPort.LoadMetadata() ->
            // GetMetadataFromLoader() -> ObjectLoader.XmlMetadataLoader.GetMetaObjectXmlMetadata(...)
            // dereferences it, and a null loader NREs there. It answers from
            // AlXmlPortMetadataRegistry — the emit-captured xmlport metadata XML — so the
            // port gets its REAL node schema. See RecordPatches.RealXmlPortMetadata.cs for
            // why the load itself is deferred rather than done here.
            var meta = _mCreateEmptyNCLMetaXmlPort.Invoke(null,
                new object?[] { RunnerMetaApplicationObjectLoader.Instance, xmlPortId, _baseAppGroup, -1, string.Empty });

            EnsureCachePopulatorReflection();
            if (meta != null && _fNCLMetaAppObjMetadataLoaded != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

            return meta;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[RecordPatches] BuildNCLMetaXmlPort({xmlPortId}) failed: {inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }
}
