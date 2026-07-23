// NavReportSync — in-process synchronous report execution for the v2 runner.
//
// Why this exists:
//   NavReport.Run() / RunModal() in Ncl.dll are sync-over-async wrappers around
//   NavReport.RunReportAsync (ValueTask). The async path NREs deep inside
//   RunReportInternalAsync on a null `parent`/Session.MetadataProvider — the
//   runner has no service tier to satisfy those preconditions. Rewriting the
//   async ValueTask state machine bodies is forbidden (CoreCLR R2R segfault
//   risk — see checkpoint 002).
//
// Approach:
//   Cecil rewrites NavReport.Run / RunModal to call the static method below
//   instead of entering RunReportAsync. The method invokes the report's
//   lifecycle triggers (OnPreReport, per-DataItem Pre/Post, OnPostReport)
//   reflectively against the same NavReport instance the AL code holds. No AL
//   semantics are silently dropped: trigger code authored in AL still runs.
//
// Runner policy (documented in docs/scope.md):
//   The runner has no service tier and cannot render report layouts. All
//   reports execute as if `ProcessingOnly = true`. Layout-rendering APIs that
//   would produce a rendered artifact (SaveAsPdf / SaveAsHtml / SaveAsWord /
//   SaveAsExcel / SaveAsDocx / RunRequestPage) throw an AL-observable
//   NavNCLDialogException with the "out-of-scope:" prefix — tests rewrite
//   those calls as `asserterror`.
//
// Limitations of v0:
//   - DataItem row iteration (FindSet + OnAfterGetRecord per row) is not yet
//     wired through. OnPreDataItem / OnPostDataItem triggers still fire once.
//     Reports whose only data-item logic is row-iteration triggers will not
//     execute that logic. Tracked as future work.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace AlRunnerV2;

public static class NavReportSync
{
    /// <summary>Diagnostic marker; gated by AL_RUNNER_DIAG_IC=1.</summary>
    public static void Diag(string msg)
    {
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_IC") == "1")
            Console.Error.WriteLine($"[DiagIC] {msg}");
    }

    // Reflection handles cached after first use.
    private static FieldInfo? _dataItemsField;     // DataItemIterator.dataItems : List<DataItem>
    private static PropertyInfo? _onPreDataItem;   // DataItem.OnPreDataItem : NavTrigger
    private static PropertyInfo? _onPostDataItem;  // DataItem.OnPostDataItem : NavTrigger
    private static MethodInfo? _onPreReport;       // NavReport.OnPreReport()  (protected virtual)
    private static MethodInfo? _onPostReport;      // NavReport.OnPostReport() (protected virtual)
    private static MethodInfo? _onInitReport;      // NavReport.OnInitReport() (protected virtual)
    private static PropertyInfo? _objectIdProp;    // NavApplicationObjectBase.ObjectId : ApplicationObjectId
    private static PropertyInfo? _objectNumberProp;// ApplicationObjectId.ObjectNumber : int
    private static FieldInfo? _objectIdField;      // NavApplicationObjectBase.objectId (private readonly ApplicationObjectId)
    private static ConstructorInfo? _appObjIdCtor; // ApplicationObjectId(ObjectType, int)
    private static object? _objectTypeReport;      // ObjectType.Report boxed enum value
    private static PropertyInfo? _realMetaDefaultLayout; // Types.Metadata.MetaReport.DefaultLayout
    private static MethodInfo? _rdlcLayoutMethod;  // NavReport.RDLCLayout(DataError, int, NavInStream)
    private static Type? _dataErrorType;           // Microsoft.Dynamics.Nav.Types.DataError

    // Stub-Metadata path (called from Cecil-rewritten NavReport.BeginInitialization).
    private static Type? _metaReportType;          // Microsoft.Dynamics.Nav.Types.MetaReport
    private static Type? _masterPageType;          // Microsoft.Dynamics.Nav.Types.MasterPage
    private static ConstructorInfo? _masterPageCtor;
    private static FieldInfo? _metaReportMasterPageField; // MetaReport.masterPage : MasterPage
    private static FieldInfo? _processingOnlyBackingField; // MetaReport.<ProcessingOnly>k__BackingField : bool
    private static PropertyInfo? _metadataSetter;  // DataItemIterator.Metadata : MetaReport (protected set)

    /// <summary>
    /// Replacement body for NavReport.BeginInitialization (sync wrapper).
    /// The real implementation calls VerifyExecutePermission + reads
    /// Tree.Session.MetadataProvider.GetReportMetadata(NclMetaReport) — both
    /// of which NRE on the runner's skeleton Session. We instead populate
    /// `base.Metadata` with an uninitialized MetaReport whose `masterPage`
    /// field points at an empty MasterPage. That makes the BC-emitted IC's
    /// tail line `RequestOptionsPage = new RequestPage(this, Metadata.RequestFormMetadata)`
    /// null-safe: `RequestFormMetadata` calls `EnsureMasterPageLoaded()` →
    /// `CreateMasterPage()` which early-returns when `masterPage != null`.
    /// </summary>
    public static void StubInitializeMetadata(object navReport)
    {
        Diag($"IC step: BeginInitialization (StubInit) on {navReport?.GetType().Name}");
        if (navReport == null) { Console.Error.WriteLine("[NavReportSync] StubInit: navReport=null"); return; }

        // Real-metadata path: when the emit pipeline captured this report's
        // runtime metadata XML (AlReportMetadataRegistry), build a genuine
        // MetaReport from it — the BC execution chain (SaveAsAsync →
        // RunReportInternalCoreAsync → ExecuteDataItemIteratorAsync →
        // NavDataSetBuilder) then runs on real DataItems/columns/ProcessingOnly.
        // Falls through to the legacy uninitialized-stub path when no XML exists
        // (e.g. reports living in precompiled MS/ISV DLLs — no emit capture).
        if (TryInstallRealMetadata(navReport)) return;

        if (_metaReportType == null)
        {
            var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            if (typesAsm == null) { Console.Error.WriteLine("[NavReportSync] StubInit: Types asm not loaded"); return; }
            _metaReportType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaReport");
            _masterPageType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MasterPage");
            if (_metaReportType == null || _masterPageType == null) { Console.Error.WriteLine($"[NavReportSync] StubInit: MetaReport={_metaReportType}, MasterPage={_masterPageType}"); return; }
            _masterPageCtor = _masterPageType.GetConstructor(Type.EmptyTypes);
            _metaReportMasterPageField = _metaReportType.GetField("masterPage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _processingOnlyBackingField = _metaReportType.GetField("<ProcessingOnly>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Console.Error.WriteLine($"[NavReportSync] StubInit: cached MetaReport={_metaReportType.FullName}, MasterPageCtor={_masterPageCtor != null}, masterPageField={_metaReportMasterPageField != null}, ProcessingOnlyBacking={_processingOnlyBackingField != null}");
        }
        if (_masterPageCtor == null || _metaReportMasterPageField == null) { Console.Error.WriteLine("[NavReportSync] StubInit: missing ctor/field"); return; }

        if (_metadataSetter == null)
        {
            var t = navReport.GetType();
            while (t != null && _metadataSetter == null)
            {
                _metadataSetter = t.GetProperty("Metadata",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                t = t.BaseType;
            }
            Console.Error.WriteLine($"[NavReportSync] StubInit: Metadata prop found={_metadataSetter != null} canWrite={_metadataSetter?.CanWrite}");
        }
        if (_metadataSetter == null) return;

        if (_metadataSetter.GetValue(navReport) != null) return;

        var masterPage = BuildEmptyMasterPage(_masterPageType!.Assembly, _masterPageType!);
        var metaReport = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(_metaReportType!);
        _metaReportMasterPageField.SetValue(metaReport, masterPage);
        _processingOnlyBackingField?.SetValue(metaReport, true);
        _metadataSetter.SetValue(navReport, metaReport);
        Console.Error.WriteLine($"[NavReportSync] StubInit: installed stub on {navReport.GetType().Name}");
    }

    private static Type? _ppType;   // Types.Metadata.PageProperties
    private static Type? _sodType;  // Types.Metadata.SourceObjectDefinition
    private static PropertyInfo? _masterPagePagePropsProp;
    private static PropertyInfo? _ppSourceObjectProp;

    /// <summary>
    /// Build an empty request-page <c>MasterPage</c> carrying a minimal,
    /// default-constructed <c>PageProperties</c> whose <c>SourceObject</c> is a
    /// default <c>SourceObjectDefinition</c> (SourceTable=0). This is the faithful
    /// "report has no request page" shape: NavForm.InitializeFromMetadata reads
    /// masterPage.PageProperties.SourceObject.* on the SaveAs info-xml path, and a
    /// bare MasterPage (null PageProperties) NREs there. Falls back to a bare
    /// MasterPage if the metadata types cannot be resolved.
    /// </summary>
    public static object BuildEmptyMasterPage(System.Reflection.Assembly typesAsm, Type masterPageType)
    {
        var master = Activator.CreateInstance(masterPageType)!;
        try
        {
            _ppType ??= typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.PageProperties");
            _sodType ??= typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.SourceObjectDefinition");
            if (_ppType == null || _sodType == null) return master;
            _masterPagePagePropsProp ??= masterPageType.GetProperty("PageProperties",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _ppSourceObjectProp ??= _ppType.GetProperty("SourceObject",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_masterPagePagePropsProp?.CanWrite != true || _ppSourceObjectProp?.CanWrite != true) return master;

            var pageProps = Activator.CreateInstance(_ppType)!;
            var sourceObj = Activator.CreateInstance(_sodType)!;
            _ppSourceObjectProp.SetValue(pageProps, sourceObj);
            _masterPagePagePropsProp.SetValue(master, pageProps);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NavReportSync] BuildEmptyMasterPage: fell back to bare MasterPage ({ex.GetType().Name}: {ex.Message})");
        }
        return master;
    }

    /// <summary>
    /// Replacement for NavReport.Run() / RunModal(). Invoked from Cecil-rewritten
    /// IL — the instance is the same NavReport the AL code constructed and holds.
    /// </summary>
    public static void SyncRun(object navReport)
    {
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_IC") == "1")
            Console.Error.WriteLine($"[NavReportSync] SyncRun entry: type={navReport?.GetType().FullName}");
        if (navReport == null) return;

        var t = navReport.GetType();
        // Walk down to NavReport base type so we can find protected virtuals.
        Type? navReportBase = t;
        while (navReportBase != null && navReportBase.Name != "NavReport")
            navReportBase = navReportBase.BaseType;
        if (navReportBase == null) return;

        // DataItemIterator (NavReport's base) owns the dataItems list.
        Type? dataItemIteratorBase = navReportBase.BaseType;
        if (dataItemIteratorBase != null && _dataItemsField == null)
        {
            _dataItemsField = dataItemIteratorBase.GetField("dataItems",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (_onInitReport == null)
            _onInitReport = navReportBase.GetMethod("OnInitReport",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, Type.EmptyTypes, null);
        if (_onPreReport == null)
            _onPreReport = navReportBase.GetMethod("OnPreReport",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, Type.EmptyTypes, null);
        if (_onPostReport == null)
            _onPostReport = navReportBase.GetMethod("OnPostReport",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, Type.EmptyTypes, null);

        TryRunOrControlFlow(navReport, navReportBase);
    }

    // Runs the full lifecycle (OnInitReport → OnPreReport → DataItems →
    // OnPostReport → layout). Catches NavControlException (Skip/Quit/etc.)
    // as control-flow termination, not error.
    private static bool TryRunOrControlFlow(object navReport, Type navReportBase)
    {
        try
        {
            InvokeVirtual(_onInitReport, navReport);
            InvokeVirtual(_onPreReport, navReport);
            InvokeDataItems(navReport);
            InvokeVirtual(_onPostReport, navReport);

            // Strict AL semantics: when the AL source declares `ProcessingOnly =
            // false` (the AL default), Run() must attempt rendering after the
            // lifecycle triggers. The runner has no service tier and cannot
            // render layouts, so the rendering attempt must surface as an
            // AL-observable error. We trigger that via NavReport.RDLCLayout —
            // a public static method that forwards to GetLayoutCore (Cecil-
            // rewritten to throw an OOS InvalidOperationException on
            // ThrowError). The error therefore originates from the actual
            // layout-resolution code path, not from a guard at the top of Run.
            if (!IsProcessingOnly(navReport, navReportBase))
                InvokeLayoutForReport(navReport, navReportBase);
            return false;
        }
        catch (Exception ex) when (IsNavControlException(ex))
        {
            // CurrReport.Skip() / Quit() / Cancel() / Break() in a report-level
            // trigger (OnPreReport, OnPostReport, or per-record data-item
            // triggers we don't yet special-case) is a control-flow signal,
            // not an error. NavReport.Skip() etc. throw NavControlException
            // by design. Swallow it — the report ends here, no layout.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_IC") == "1")
                Console.Error.WriteLine($"[NavReportSync] SyncRun: report terminated by {ex.GetType().Name} (control-flow)");
            return true;
        }
    }

    // NavControlException lives in Microsoft.Dynamics.Nav.Types and is
    // internal — match by full type name so we don't need an InternalsVisibleTo
    // bridge. NavReport.Skip/Quit/Cancel/Break and DataItem.Skip/Break/etc.
    // all throw this type as a control-flow signal.
    private static bool IsNavControlException(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            var n = e.GetType().Name;
            if (n == "NavControlException") return true;
        }
        return false;
    }

    // Looks up ProcessingOnly from the parsed AL source (RecordPatches).
    // Falls back to true when the report ID cannot be resolved — defensive
    // so unknown reports do not trip the rendering guard.
    private static bool IsProcessingOnly(object navReport, Type navReportBase)
    {
        int reportId = TryGetObjectId(navReport, navReportBase);
        bool diag = Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_IC") == "1";
        if (reportId <= 0)
        {
            if (diag) Console.Error.WriteLine($"[NavReportSync] SyncRun: reportId=0 (could not resolve), defaulting ProcessingOnly=true");
            return true;
        }
        bool po = AlRunnerV2.Patches.RecordPatches.IsReportProcessingOnly(reportId);
        if (diag) Console.Error.WriteLine($"[NavReportSync] SyncRun: report {reportId} ProcessingOnly={po}");
        return po;
    }

    private static int TryGetObjectId(object navReport, Type navReportBase)
    {
        // Primary path: AL-emitted report types are named "Report<N>" (e.g.
        // "Report50600"). This avoids the ApplicationObjectId.ObjectNumber=0
        // mystery we hit going through the inherited ObjectId property —
        // the field IS set by base ctor IL but boxed-struct reflection
        // returns 0 in some scenarios on the Cecil-rewritten ctor chain.
        // Type-name parse is robust against that and trivially correct
        // for AL-emitted reports.
        var name = navReport.GetType().Name;
        if (name.Length > 6 && name.StartsWith("Report", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(6), out int idFromName))
        {
            return idFromName;
        }

        // Fallback: reflective ObjectId.ObjectNumber.
        if (_objectIdProp == null)
        {
            Type? t = navReportBase;
            while (t != null && _objectIdProp == null)
            {
                _objectIdProp = t.GetProperty("ObjectId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                t = t.BaseType;
            }
        }
        if (_objectIdProp == null) return 0;
        var appObjId = _objectIdProp.GetValue(navReport);
        if (appObjId == null) return 0;
        if (_objectNumberProp == null)
            _objectNumberProp = appObjId.GetType().GetProperty("ObjectNumber",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_objectNumberProp == null) return 0;
        var n = _objectNumberProp.GetValue(appObjId);
        return n is int i ? i : 0;
    }

    private static void InvokeLayoutForReport(object navReport, Type navReportBase)
    {
        if (_rdlcLayoutMethod == null)
        {
            // Look up NavReport.RDLCLayout(DataError, int, NavInStream).
            _rdlcLayoutMethod = navReportBase.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "RDLCLayout" && m.GetParameters().Length == 3);
        }
        if (_dataErrorType == null)
        {
            var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            _dataErrorType = typesAsm?.GetType("Microsoft.Dynamics.Nav.Types.DataError");
        }
        int reportId = TryGetObjectId(navReport, navReportBase);
        if (_rdlcLayoutMethod != null && _dataErrorType != null)
        {
            // DataError.ThrowError = 1 → GetLayoutCore (Cecil-rewritten) throws OOS.
            var throwError = Enum.ToObject(_dataErrorType, 1);
            try
            {
                _rdlcLayoutMethod.Invoke(null, new object?[] { throwError, reportId, null });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
            // RDLCLayout returning normally would mean we somehow found a
            // layout — defensive throw in case Cecil rewrite didn't apply.
            throw new InvalidOperationException(
                "out-of-scope: NavReport.Run on layout-rendering report (ProcessingOnly = false) " +
                "— rendering requires a service tier — see docs/scope.md#report-rendering");
        }

        // Fallback if reflection failed: still throw AL-observable error.
        throw new InvalidOperationException(
            "out-of-scope: NavReport.Run on layout-rendering report (ProcessingOnly = false) " +
            "— rendering requires a service tier — see docs/scope.md#report-rendering");
    }

    private static void InvokeVirtual(MethodInfo? m, object instance)
    {
        if (m == null) return;
        try { m.Invoke(instance, null); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Surface the AL trigger's exception (e.g. Assert.AreEqual failure)
            // as the original, not wrapped in TargetInvocationException.
            throw tie.InnerException;
        }
    }

    private static void InvokeDataItems(object navReport)
    {
        if (_dataItemsField == null) return;
        if (_dataItemsField.GetValue(navReport) is not IEnumerable items) return;

        foreach (var di in items)
        {
            if (di == null) continue;
            if (_onPreDataItem == null)
                _onPreDataItem = di.GetType().GetProperty("OnPreDataItem",
                    BindingFlags.Instance | BindingFlags.Public);
            if (_onPostDataItem == null)
                _onPostDataItem = di.GetType().GetProperty("OnPostDataItem",
                    BindingFlags.Instance | BindingFlags.Public);

            InvokeTrigger(_onPreDataItem, di);
            // TODO: iterate source table and fire OnAfterGetRecord per row.
            // For test reports without row triggers this is a no-op anyway.
            InvokeTrigger(_onPostDataItem, di);
        }
    }

    private static void InvokeTrigger(PropertyInfo? prop, object dataItem)
    {
        if (prop == null) return;
        var trigger = prop.GetValue(dataItem) as Delegate;
        if (trigger == null) return;
        try { trigger.DynamicInvoke(); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Real report metadata (M1 of the report-execution build)
    // ─────────────────────────────────────────────────────────────────────────

    // reportId → constructed MetaReport (Types.Metadata.MetaReport) or null when
    // no metadata XML is available for that id.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _realMetaCache = new();
    private static ConstructorInfo? _metaReportCtor;          // MetaReport(XmlElement, CreateRequestForm, int, int, RemoveItems…)
    private static MethodInfo? _getDataItemByName;            // MetaReport.GetDataItemByName(string)
    private static PropertyInfo? _metaReportDataItems;        // MetaReport.DataItems
    private static PropertyInfo? _metaDataItemPrintOnlyIfDetail; // MetaDataItem.PrintOnlyIfDetail
    private static PropertyInfo? _dataItemMetaData;           // DataItem.MetaData
    private static PropertyInfo? _dataItemPrintOnlyIfDetail;  // DataItem.PrintOnlyIfDetail
    private static MethodInfo? _dataItemSetAutoCalcFields;    // DataItem.SetAutoCalcFields()

    public static void ResetMetadataCache() => _realMetaCache.Clear();

    /// <summary>
    /// Build (or fetch cached) the real MetaReport for a report id from the
    /// emit-captured metadata XML. Returns null when no XML was captured.
    /// </summary>
    public static object? GetRealMetaReport(int reportId)
    {
        if (reportId <= 0) return null;
        return _realMetaCache.GetOrAdd(reportId, static id =>
        {
            if (!AlReportMetadataRegistry.TryGet(id, out var xml)) return null;
            try
            {
                var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")
                    ?? throw new InvalidOperationException("Types asm not loaded");
                var tMeta = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaReport")
                    ?? throw new InvalidOperationException("MetaReport type not found");
                if (_metaReportCtor == null)
                {
                    // MetaReport(XmlElement node, CreateRequestForm createRequestForm,
                    //            int metadataAppGroupId, int languageAppGroupId,
                    //            RemoveItemsOnPageBasedOnLicenseAndApplicationArea removeItems = null)
                    _metaReportCtor = tMeta.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(c =>
                        {
                            var ps = c.GetParameters();
                            return ps.Length >= 3
                                && typeof(System.Xml.XmlNode).IsAssignableFrom(ps[0].ParameterType)
                                && ps.Skip(1).Any(p => p.ParameterType == typeof(int));
                        })
                        ?? throw new InvalidOperationException("MetaReport(XmlElement, …) ctor not found");
                }
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                var ps2 = _metaReportCtor.GetParameters();
                var args = new object?[ps2.Length];
                args[0] = doc.DocumentElement;
                for (int i = 1; i < ps2.Length; i++)
                    args[i] = ps2[i].ParameterType == typeof(int) ? 0 : null;
                var meta = _metaReportCtor.Invoke(args);

                // IC tail safety: Report{N}.InitializeComponent ends with
                // `RequestOptionsPage = new RequestPage(this, Metadata.RequestFormMetadata)`.
                // Pre-poke `masterPage` with an empty MasterPage so
                // EnsureMasterPageLoaded → CreateMasterPage early-returns instead of
                // building a full request-page master (page UI is out of runner scope).
                // The MasterPage must carry a minimal PageProperties+SourceObject: the
                // SaveAs report-information-xml path forces NavForm.InitializeFromMetadata
                // on this request page, which dereferences
                // masterPage.PageProperties.SourceObject.* (SaveValues, SourceTable,
                // PageDataSource). A bare `new MasterPage()` leaves PageProperties null →
                // NRE. Default-constructed PageProperties/SourceObjectDefinition give the
                // faithful empty request page (SourceTable=0 → no source table bound).
                var tMaster = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MasterPage");
                var masterField = tMeta.GetField("masterPage", BindingFlags.Instance | BindingFlags.NonPublic);
                if (tMaster != null && masterField != null && masterField.GetValue(meta) == null)
                    masterField.SetValue(meta, BuildEmptyMasterPage(typesAsm, tMaster));

                Console.Error.WriteLine($"[NavReportSync] built REAL MetaReport for report {id} from emit-captured metadata XML");
                return meta;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                Console.Error.WriteLine($"[NavReportSync] real MetaReport build FAILED for report {id}: {inner.GetType().Name}: {inner.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// Install the real MetaReport on `navReport.Metadata` when metadata XML is
    /// available. Returns false to fall back to the legacy stub.
    /// </summary>
    private static bool TryInstallRealMetadata(object navReport)
    {
        int reportId = 0;
        var name = navReport.GetType().Name;
        if (name.Length > 6 && name.StartsWith("Report", StringComparison.Ordinal))
            int.TryParse(name.AsSpan(6), out reportId);
        var meta = GetRealMetaReport(reportId);
        if (meta == null) return false;

        if (_metadataSetter == null)
        {
            var t = navReport.GetType();
            while (t != null && _metadataSetter == null)
            {
                _metadataSetter = t.GetProperty("Metadata",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                t = t.BaseType;
            }
        }
        if (_metadataSetter == null) return false;
        if (_metadataSetter.GetValue(navReport) == null)
            _metadataSetter.SetValue(navReport, meta);

        // Replicate the real DataItemIterator.BeginInitializationAsync body:
        //   captionML = NCLCaptionStrings.CreateNCLCaptionStrings(Metadata.CaptionML, Metadata.Name)
        // (GetCaption/ObjectID NRE on null captionML otherwise — reached from
        // LogReportExecutionStatus at the end of every report run).
        try
        {
            Type? iterT = navReport.GetType();
            while (iterT != null && iterT.Name != "DataItemIterator") iterT = iterT.BaseType;
            var fCaption = iterT?.GetField("captionML", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fCaption != null && fCaption.GetValue(navReport) == null)
            {
                var metaT = meta.GetType();
                var captionMl = metaT.GetProperty("CaptionML",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(meta);
                var metaName = metaT.GetProperty("Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(meta) as string;
                var tCap = fCaption.FieldType;
                // CreateNCLCaptionStrings is overloaded (string / MultiLanguage) —
                // pick the overload matching the actual CaptionML instance type.
                var create = tCap.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateNCLCaptionStrings"
                        && m.GetParameters().Length == 2
                        && (captionMl == null
                            ? m.GetParameters()[0].ParameterType != typeof(string)
                            : m.GetParameters()[0].ParameterType.IsInstanceOfType(captionMl)));
                if (create != null)
                {
                    var cap = create.Invoke(null, new object?[] { captionMl, metaName ?? string.Empty });
                    if (cap != null) fCaption.SetValue(navReport, cap);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NavReportSync] captionML seed failed: {ex.Message}");
        }
        return true;
    }

    /// <summary>
    /// Replacement body for NavReport.Add(DataItem, string) (Cecil-rewritten).
    /// With real metadata: faithful to BC's override — binds DataItem.MetaData
    /// from Metadata.GetDataItemByName, applies PrintOnlyIfDetail and
    /// SetAutoCalcFields, then appends to the dataItems list. Without real
    /// metadata (legacy stub): plain list append, as before.
    /// </summary>
    public static void ReportAdd(object navReport, object dataItem, string? dataItemName)
    {
        // Resolve the dataItems list (DataItemIterator private field).
        Type? navReportBase = navReport.GetType();
        while (navReportBase != null && navReportBase.Name != "NavReport")
            navReportBase = navReportBase.BaseType;
        var iteratorType = navReportBase?.BaseType;
        if (_dataItemsField == null && iteratorType != null)
            _dataItemsField = iteratorType.GetField("dataItems",
                BindingFlags.Instance | BindingFlags.NonPublic);
        var list = _dataItemsField?.GetValue(navReport) as System.Collections.IList;

        // Real metadata present?
        object? meta = null;
        if (_metadataSetter != null || navReportBase != null)
        {
            var metaProp = _metadataSetter;
            if (metaProp == null)
            {
                Type? t = navReport.GetType();
                while (t != null && metaProp == null)
                {
                    metaProp = t.GetProperty("Metadata",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    t = t.BaseType;
                }
            }
            meta = metaProp?.GetValue(navReport);
        }

        bool metadataIsReal = false;
        if (meta != null)
        {
            if (_metaReportDataItems == null)
                _metaReportDataItems = meta.GetType().GetProperty("DataItems",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try { metadataIsReal = _metaReportDataItems?.GetValue(meta) != null; }
            catch { metadataIsReal = false; }
        }

        if (metadataIsReal)
        {
            try
            {
                object? metaDataItem = null;
                if (dataItemName != null)
                {
                    if (_getDataItemByName == null)
                        _getDataItemByName = meta!.GetType().GetMethod("GetDataItemByName",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    metaDataItem = _getDataItemByName?.Invoke(meta, new object[] { dataItemName });
                }
                else if (_metaReportDataItems?.GetValue(meta) is System.Collections.IList metaItems && list != null)
                {
                    metaDataItem = metaItems[list.Count];
                }
                if (metaDataItem != null)
                {
                    if (_dataItemMetaData == null)
                        _dataItemMetaData = dataItem.GetType().GetProperty("MetaData",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _dataItemMetaData?.SetValue(dataItem, metaDataItem);

                    if (_metaDataItemPrintOnlyIfDetail == null)
                        _metaDataItemPrintOnlyIfDetail = metaDataItem.GetType().GetProperty("PrintOnlyIfDetail",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_dataItemPrintOnlyIfDetail == null)
                        _dataItemPrintOnlyIfDetail = dataItem.GetType().GetProperty("PrintOnlyIfDetail",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var po = _metaDataItemPrintOnlyIfDetail?.GetValue(metaDataItem);
                    if (po != null) _dataItemPrintOnlyIfDetail?.SetValue(dataItem, po);

                    if (_dataItemSetAutoCalcFields == null)
                        _dataItemSetAutoCalcFields = dataItem.GetType().GetMethod("SetAutoCalcFields",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, Type.EmptyTypes, null);
                    _dataItemSetAutoCalcFields?.Invoke(dataItem, null);
                    // NOTE: BC's override also caches per-column OptionCaptionML into
                    // metaColumnOptionCaptions for GetOptionValue. Option caption
                    // localization is not yet wired — GetOptionValue falls back to the
                    // raw option name, which is what headless test assertions compare.
                }
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_IC") == "1")
        {
            var recProp = dataItem.GetType().GetProperty("Record",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var rec = recProp?.GetValue(dataItem);
            Console.Error.WriteLine($"[NavReportSync] ReportAdd: name={dataItemName} metadataIsReal={metadataIsReal} Record={(rec == null ? "NULL" : rec.GetType().Name)} listCount={(list?.Count ?? -1)}");
        }
        list?.Add(dataItem);
    }

    /// <summary>
    /// Replacement body for TempPathHelper..ctor(string) (Cecil-rewritten).
    /// The real ctor roots every server temp path under
    /// ProductApplicationData.ServerPath — /usr/share/Microsoft/… on Linux,
    /// not writable. Root under the process temp dir instead; the lazily-called
    /// CreatePathIfNonExistent then creates subfolders on demand (with the
    /// null-ACL CreateDirectory path — see the NavDirectorySecurity rewrite).
    /// Reached from NavFile's cctor via ReportProcessorXmlGenerator's temp
    /// stream buffer on the report-execution path.
    /// </summary>
    public static void TempPathHelper_Ctor(object self, string? folderName)
    {
        var t = self.GetType();
        string name = string.IsNullOrEmpty(folderName)
            ? Environment.ProcessId.ToString()
            : string.Concat(folderName.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)));
        string basePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "al-runner-navserver", name);
        System.IO.Directory.CreateDirectory(basePath);

        void Set(string field, object? value)
        {
            var f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) AlRunnerV2.Infrastructure.FieldPoke.SetInstance(f, self, value!);
        }
        Set("instanceFolderName", name);
        Set("instanceBasePath", basePath);
        Set("configurableBasePath", basePath);
        Set("classSpecificSourceFolders", new System.Collections.Generic.List<string>());
    }

    // Fields declared on NavReport whose `= new List<>()/new Dictionary<>()`
    // initializers live in the ORIGINAL ctor body that the Cecil rewrite replaces
    // (see NclCecilRewrite "NavReport..ctor" block). Cached once.
    private static FieldInfo[]? _navReportCollectionFields;

    /// <summary>
    /// Called from the Cecil-rewritten NavReport 3-arg ctor right after the base
    /// ctor chain: re-creates the collection field initializers the body rewrite
    /// dropped (reportRecords, reportLabels, metaColumnOptionCaptions, report
    /// extension maps, …). Without this, GetReportRecords/label processing NRE.
    /// </summary>
    public static void InitializeNavReportCollections(object navReport)
    {
        if (_navReportCollectionFields == null)
        {
            Type? t = navReport.GetType();
            while (t != null && t.Name != "NavReport") t = t.BaseType;
            _navReportCollectionFields = t == null
                ? Array.Empty<FieldInfo>()
                : t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                   .Where(f => f.FieldType.IsGenericType
                       && (f.FieldType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>)
                        || f.FieldType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.Dictionary<,>)))
                   .ToArray();
        }
        foreach (var f in _navReportCollectionFields)
        {
            if (f.GetValue(navReport) == null)
                AlRunnerV2.Infrastructure.FieldPoke.SetInstance(f, navReport,
                    Activator.CreateInstance(f.FieldType)!); // FieldPoke handles initonly fields
        }
    }

    /// <summary>
    /// Replacement body for NCLMetaReport.CreateObjectInstance(ITreeObject, bool)
    /// (Cecil-rewritten). The skeleton NCLMetaReport has no
    /// ApplicationObjectConstructor delegate, so construct Report{id} directly
    /// from the loaded assemblies (same approach as NavReportHandle_CreateTarget)
    /// and run the same post-construction steps as BC's original
    /// (InitializeReportValues + FinalizeDataItemLoading).
    /// </summary>
    public static object CreateReportInstance(object nclMetaReport, object parent, bool skipRestoreSavedReportSettings)
    {
        // report id from NCLMetaApplicationObject.ApplicationObjectId.ObjectNumber
        var idProp = nclMetaReport.GetType().GetProperty("ApplicationObjectId",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var appObjId = idProp?.GetValue(nclMetaReport)
            ?? throw new InvalidOperationException("NCLMetaReport.ApplicationObjectId unavailable");
        var numProp = appObjId.GetType().GetProperty("ObjectNumber",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        int id = (int)(numProp?.GetValue(appObjId)
            ?? throw new InvalidOperationException("ApplicationObjectId.ObjectNumber unavailable"));

        var reportType = AlRunnerV2.BcRuntime.FindReportTypePublic(id)
            ?? throw new InvalidOperationException(
                $"Report{id} is not present in the test assembly or any loaded dependency.");

        object instance;
        var ctors = reportType.GetConstructors();
        var twoArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 2);
        var oneArg = ctors.FirstOrDefault(c => c.GetParameters().Length == 1);
        if (twoArg != null)
            instance = twoArg.Invoke(new object?[] { parent, nclMetaReport });
        else if (oneArg != null)
            instance = oneArg.Invoke(new object?[] { parent });
        else
            throw new InvalidOperationException(
                $"Report{id} has no (ITreeObject[, NCLMetaReport]) constructor");

        // The report executes on `parent` (the SaveAs session). BC's own
        // MetadataPatches.InjectSkeletonSystemTenant deliberately seeds only
        // NavSession.tenant, not NavSession.systemTenant (the latter was "unnecessary"
        // for every prior path because runner record construction goes through the
        // hooked NavRecordHandle.CreateTarget, which passes an explicit metaTable and so
        // never reaches NavRecord..ctor's `metaTable == null` branch).
        //
        // Report dataitem iteration breaks that assumption: DataItemIterator.
        // ApplyDataItemTableViewAndRequestFormFilters constructs a *bare* scratch record
        // (`new NavRecord(dataItem.Record.Session, tableId, securityFiltering)`) to parse
        // the DataItemTableView string. That 3-arg ctor passes metaTable == null, so
        // NavRecord..ctor dereferences `ParentSession.NCLMetadata`
        // (= session.SystemTenant.NCLMetadata) — which NREs when session.systemTenant is
        // null. Seed it with the skeleton system tenant (already carrying the skeleton
        // NCLMetadata) so the in-scope dataset spine can build its scratch records.
        SeedSessionSystemTenant(parent);


        // Seed the inherited base.ObjectId with the true report id. The compiled
        // Report{id} ctor chain leaves NavApplicationObjectBase.objectId at
        // ObjectNumber=0 (a known runner wiring quirk worked around elsewhere by
        // parsing the type name). Paths such as NavReport.DetermineStandardLayoutAsync
        // key off base.ObjectId.ObjectNumber and otherwise resolve "report 0".
        SeedObjectId(instance, id);

        // Same post-steps as BC's CreateObjectInstance (extension binding is not
        // yet wired for this path — report extensions on SaveAs TODO).
        //
        // InitializeReportValues restores SAVED request-page values
        // (requestOptionsPage.InitializeRequestPageWithCustomValues). The runner has
        // no saved report settings store, and BC itself skips the restore when
        // skipRestoreSavedReportSettings=true (the static SaveAs path passes true
        // for empty parameters). The request-page ctor chain is null-safe-minimal
        // on the skeleton, so the restore walk NREs — skipping it is observably
        // equivalent to a tenant with no saved settings.
        try
        {
            if (!skipRestoreSavedReportSettings)
            {
                var init = instance.GetType().GetMethod("InitializeReportValues",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                try { init?.Invoke(instance, null); }
                catch (TargetInvocationException tie) when (tie.InnerException is NullReferenceException)
                {
                    // Saved-settings restore walked skeleton request-page state; no
                    // saved settings exist in the runner — equivalent to none stored.
                }
            }
            var fin = instance.GetType().GetMethod("FinalizeDataItemLoading",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            fin?.Invoke(instance, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
        return instance;
    }

    /// <summary>
    /// Seed the inherited NavApplicationObjectBase.objectId (private readonly
    /// ApplicationObjectId) with the true report id when the compiled Report ctor
    /// left it at ObjectNumber=0. Idempotent; only fires when currently 0.
    /// </summary>
    private static void SeedObjectId(object instance, int id)
    {
        if (id <= 0) return;
        try
        {
            if (_objectIdField == null)
            {
                Type? t = instance.GetType();
                while (t != null && _objectIdField == null)
                {
                    _objectIdField = t.GetField("objectId",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    t = t.BaseType;
                }
            }
            if (_objectIdField == null) return;
            var appObjIdType = _objectIdField.FieldType;

            // Only reseed when the inherited id is the 0 sentinel.
            var current = _objectIdField.GetValue(instance);
            if (current != null)
            {
                _objectNumberProp ??= appObjIdType.GetProperty("ObjectNumber",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_objectNumberProp?.GetValue(current) is int n && n != 0) return;
            }

            _appObjIdCtor ??= appObjIdType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 2
                    && c.GetParameters()[1].ParameterType == typeof(int));
            if (_appObjIdCtor == null) return;
            if (_objectTypeReport == null)
            {
                var otType = _appObjIdCtor.GetParameters()[0].ParameterType; // ObjectType enum
                _objectTypeReport = Enum.Parse(otType, "Report");
            }
            var appObjId = _appObjIdCtor.Invoke(new object?[] { _objectTypeReport, id });
            _objectIdField.SetValue(instance, appObjId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NavReportSync] SeedObjectId({id}) failed: {ex.Message}");
        }
    }

    private static FieldInfo? _sessionSystemTenantField;

    /// <summary>
    /// Ensure the report execution session's <c>systemTenant</c> field is non-null so
    /// the report dataset spine (DataItemIterator's bare scratch-record construction,
    /// which reaches NavRecord..ctor's <c>metaTable == null</c> branch and dereferences
    /// <c>session.SystemTenant.NCLMetadata</c>) does not NRE. Field-poke of framework
    /// session state with the runner's own skeleton NavSystemTenant — no MS/ISV AL body
    /// is rewritten (see .claude/rules/precompiled-dll-respect.md).
    /// </summary>
    private static void SeedSessionSystemTenant(object? session)
    {
        if (session == null) return;
        var skeletonTenant = AlRunnerV2.BcRuntime.SkeletonSystemTenant;
        if (skeletonTenant == null) return;
        try
        {
            if (_sessionSystemTenantField == null)
            {
                Type? t = session.GetType();
                while (t != null && _sessionSystemTenantField == null)
                {
                    _sessionSystemTenantField = t.GetField("systemTenant",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    t = t.BaseType;
                }
            }
            if (_sessionSystemTenantField == null) return;
            if (_sessionSystemTenantField.GetValue(session) == null)
                AlRunnerV2.Infrastructure.FieldPoke.SetInstance(_sessionSystemTenantField, session, skeletonTenant);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NavReportSync] SeedSessionSystemTenant failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve the ReportModel (layout format) a report should render with, for
    /// the runner's rewritten ReportLayoutSelection.TryGetSelectedLayoutOrDefault.
    /// When the caller already resolved a concrete format (requestedModel != None)
    /// that wins. Otherwise the report's own default layout drives the format,
    /// read from the real emit-captured MetaReport — the NCL skeleton carries only
    /// the enum default (RDLC) so it cannot distinguish Custom (in-scope custom
    /// merger) from RDLC/Word/Excel (out-of-scope external renderers).
    /// Types.DefaultLayout {RDLC=0,Word=1,Excel=2,Custom=3} shares the numeric
    /// values of Report.Base.ReportModel {Rdlc=0,Word=1,Excel=2,Custom=3}, so the
    /// mapping is identity. Returns ReportModel.None (100) when undeterminable.
    /// </summary>
    public static int ResolveDefaultReportModel(int reportId, int requestedModel)
    {
        const int None = 100;
        if (requestedModel != None) return requestedModel;
        try
        {
            var meta = GetRealMetaReport(reportId);
            if (meta == null) return None;
            _realMetaDefaultLayout ??= meta.GetType().GetProperty("DefaultLayout",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var dl = _realMetaDefaultLayout?.GetValue(meta);
            if (dl == null) return None;
            return Convert.ToInt32(dl); // DefaultLayout → ReportModel is identity for 0..3
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NavReportSync] ResolveDefaultReportModel({reportId}) failed: {ex.Message}");
            return None;
        }
    }
}
