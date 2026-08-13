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
// Data-item iteration:
//   The row loop is NOT re-implemented here. InvokeDataItems drives BC's own
//   DataItemIterator.LoopRootDataItemsAsync — the same method the SaveAs /
//   RunReportInternalCoreAsync path uses — so DataItemTableView, DataItemLink,
//   SetTableView filters, nested data items, MaxIteration and CurrReport.Skip /
//   Break behave exactly as the runtime engine defines them.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

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
    private static FieldInfo? _metaReportDataItemsField; // MetaReport.dataItems : List<MetaDataItem> (private readonly)

    /// <summary>
    /// Marks MetaReport instances built by the legacy stub path (StubInitializeMetadata,
    /// for reports whose emit metadata was never captured — e.g. reports living in a
    /// precompiled MS/ISV dependency .app, which the runner never source-compiles).
    /// ReportAdd consults this to know it must synthesize a MetaDataItem for a
    /// not-yet-seen data-item name instead of calling the real
    /// MetaReport.GetDataItemByName (which throws ArgumentException for names it was
    /// never told about — real BC's metadata is pre-populated at publish time from the
    /// app's own compiled metadata store, which the runner does not have for
    /// dependency reports it never compiled).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _stubMetaReports = new();

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
        // NOTE (investigated, deliberately left as-is): ProcessingOnly=true is
        // unfaithful for reports that are genuinely ProcessingOnly=false in real
        // BC (BC's SaveReportAsFormatCoreAsync throws NavNCLReportProcessingOnlyException
        // whenever Metadata.ProcessingOnly is true, so today EVERY stub-metadata
        // report's Report.SaveAs returns false rather than running). Flipping
        // this to false was tried and reverted: it lets execution reach real
        // report-run code (RunReportInternalAsync -> LogReportExecutionStatus ->
        // DataItemIterator.GetCaption), which NREs on captionML — a SEPARATE,
        // pre-existing gap (captionML is only seeded in TryInstallRealMetadata's
        // real-metadata branch, never in this stub branch). Out of scope for the
        // CreateReportInstance/FinalizeDataItemLoading NRE this file's
        // BuildSyntheticFlatMetaDataItem path fixes — left as a follow-up.
        _processingOnlyBackingField?.SetValue(metaReport, true);

        // GetUninitializedObject skips every field initializer, including
        // `private readonly List<MetaDataItem> dataItems = new List<MetaDataItem>();`.
        // Left null, MetaReport.DataItems (=> dataItems) returns null, which
        // ReportAdd (NavReport.Add's faithful replica) reads as "no real metadata"
        // (metadataIsReal=false) — so DataItem.MetaData is NEVER bound and
        // DataItemIterator.FinalizeDataItemLoading NREs on dataItem.MetaData.
        // Give it a real (empty) list so ReportAdd takes the metadataIsReal=true
        // branch; see BuildSyntheticFlatMetaDataItem for how the (unknown, never
        // captured) per-data-item shape gets synthesized as ReportAdd runs.
        _metaReportDataItemsField ??= _metaReportType!.GetField("dataItems",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (_metaReportDataItemsField != null)
        {
            var emptyList = Activator.CreateInstance(
                typeof(List<>).MakeGenericType(_metaReportDataItemsField.FieldType.GetGenericArguments()[0]));
            _metaReportDataItemsField.SetValue(metaReport, emptyList);
        }
        _stubMetaReports.AddOrUpdate(metaReport, metaReport);

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
    /// <summary>
    /// The MasterPage to give a report whose metadata carries no request-page definition —
    /// which is every report whose metadata the runner RECONSTRUCTS from a precompiled
    /// dependency, since the reconstruction covers data items and columns but not the
    /// request page.
    ///
    /// It matters that this is not simply a blank MasterPage. BC picks the handler for a
    /// modal form from <c>masterPage.PageProperties.PageType</c>
    /// (NavTestExecution.FindPageType) and then matches the handler's declared object id
    /// against <c>form.ObjectId.ObjectNumber</c>, which comes from <c>masterPage.ID</c>. A
    /// blank MasterPage reports PageType.Card and id 0, so a test's [RequestPageHandler] for
    /// report N matched nothing and the run fell through to a client callback that headless
    /// mode cannot serve.
    ///
    /// Only those two properties are filled in. Loading BC's real request-page TEMPLATE
    /// (MetadataProvider.GetMasterPage(PageType.ReportPreview)) was tried first and is worse
    /// here: it brings the standard request-page controls with it, and NavForm's
    /// InitializeFromMetadata — which request pages are deliberately not opted into
    /// (RunnerFormInit) — then NREs walking them. A request page with no controls is the
    /// honest description of what a reconstructed report's metadata actually contains, and a
    /// handler that reaches for a control is refused by name in RequestPageTestPage.GetField
    /// rather than silently answering an empty value.
    /// </summary>
    public static object BuildRequestPageStubMasterPage(
        System.Reflection.Assembly typesAsm, Type masterPageType, int reportId)
    {
        var master = BuildEmptyMasterPage(typesAsm, masterPageType);
        try
        {
            masterPageType.GetProperty("ID",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .SetValue(master, reportId);

            var pageProps = _masterPagePagePropsProp?.GetValue(master);
            var pPageType = pageProps?.GetType().GetProperty("PageType",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pageProps != null && pPageType?.CanWrite == true && pPageType.PropertyType.IsEnum
                && Enum.IsDefined(pPageType.PropertyType, "ReportPreview"))
                pPageType.SetValue(pageProps, Enum.Parse(pPageType.PropertyType, "ReportPreview"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[NavReportSync] could not stamp report {reportId} onto its request-page MasterPage "
                + $"({ex.GetType().Name}: {ex.Message}) — its [RequestPageHandler] will not be reachable");
        }
        return master;
    }

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
    /// <summary>
    /// Replacement for every sync <c>NavReport.RunRequestPage</c> overload.
    ///
    /// WHY IT IS NOW IN SCOPE
    /// RunRequestPage was refused as "request-page UI rendering requires service tier". That
    /// conflated two different things. Rendering a request page for a HUMAN needs a client;
    /// running one under test does not — BC dispatches it to the test's own
    /// [RequestPageHandler] and never draws anything. AL that calls RunRequestPage to CAPTURE
    /// PARAMETERS (the documented way to obtain a report's RequestPageParameters XML) is
    /// therefore ordinary in-scope AL, and refusing it made every such test unrunnable while
    /// its declared handler sat unexecuted.
    ///
    /// HOW
    /// BC's real body awaits RunReportAsync, which NREs on the skeleton session — the same
    /// reason Run/RunModal are rewritten to SyncRun. But the request page itself is just a
    /// NavForm, and running it modally lands in NavTestExecution.TestHandleModalForm, which
    /// already branches on <c>form.IsRequestPage</c> to build a NavTestRequestPage and invoke
    /// the handler. That path is wired (see RunnerModalDispatch), so the request page runs
    /// through BC's own handler dispatch rather than anything reimplemented here.
    ///
    /// The parameters string returned is BC's own <c>GetReportParameters()</c> off the report
    /// instance, after the handler has had its chance to set filters — so a handler that
    /// changes a filter is reflected, which is the entire point of the call.
    ///
    /// <param name="navReportOrNull">The report instance for the instance overloads; null for
    /// the static (by-id) overloads, which construct one.</param>
    /// </summary>
    public static string SyncRunRequestPage(object? navReportOrNull, int reportId, string? parameters)
    {
        var report = navReportOrNull ?? CreateReportForRequestPage(reportId);
        if (report == null)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"NavReport.RunRequestPage({reportId})",
                "not-yet-implemented — the runner could not construct report " + reportId +
                " to run its request page. See docs/scope.md");

        var confirmed = RunRequestPageForHandler(report, reportId, parameters);

        // BC's own RunRequestPageAsync reaches GetReportParameters through
        // RunReportAsync(…, ReportIntent.Parameters, …), which sets NavReport.success when
        // the request page is confirmed. GetReportParameters returns string.Empty unless it
        // is set, which is precisely how "the user cancelled" is reported to AL. The runner
        // drives the request page directly, so it has to record the same fact.
        SetReportSuccess(report, confirmed);

        // Shape-tolerant: BC declares `GetReportParameters(bool requireSuccess = true)`, so a
        // Type.EmptyTypes lookup finds nothing — the optional parameter is still a parameter.
        var getParams = report.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "GetReportParameters" && m.ReturnType == typeof(string)
                        && m.GetParameters().All(pi => pi.ParameterType == typeof(bool)))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "NavReport.GetReportParameters not found — Ncl shape changed; do not commit");
        // requireSuccess: true — the success flag set just above is what makes a cancelled
        // request page answer with an empty string, exactly as it does in real BC.
        var getParamsArgs = getParams.GetParameters().Length == 0
            ? Array.Empty<object?>()
            : new object?[] { true };
        var result = Invoke(getParams, report, getParamsArgs) as string ?? string.Empty;
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_RP") == "1")
            Console.Error.WriteLine($"[NavReportSync] RunRequestPage({reportId}) confirmed={confirmed} params={result}");
        return result;
    }

    /// <summary>Set NavReport's private <c>success</c> flag (see SyncRunRequestPage).</summary>
    private static void SetReportSuccess(object report, bool value)
    {
        for (Type? t = report.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField("success", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f == null || f.FieldType != typeof(bool)) continue;
            f.SetValue(report, value);
            return;
        }
        Console.Error.WriteLine(
            "[NavReportSync] NavReport.success field not found — RunRequestPage will return empty "
            + "parameters even after the handler confirmed; Ncl shape changed");
    }

    /// <summary>
    /// The NCLMetaReport for a report id, via BC's own NavGlobal.NCLMetadata — which the
    /// runner already populates (see RecordPatches.NclMetadataCachePopulator). Returns null
    /// when BC does not know the report, so the caller refuses by name rather than
    /// constructing something meaningless.
    /// </summary>
    private static object? GetNclMetaReportById(int reportId)
    {
        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        var navGlobal = navNcl?.GetType("Microsoft.Dynamics.Nav.Runtime.NavGlobal");
        var nclMeta = navGlobal?.GetProperty("NCLMetadata", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (nclMeta == null) return null;
        // Shape-tolerant: BC declares GetMetaReportById(int, bool requireCompiled, int
        // appGroupId) with optionals, and the exact arity has moved between versions. Pick the
        // narrowest overload and fill the rest with their documented defaults —
        // requireCompiled:false, because a runner-compiled report has no "compiled" flag set
        // and requiring one is exactly what makes the lookup come back empty here.
        var getMeta = nclMeta.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "GetMetaReportById"
                        && m.GetParameters().Length >= 1
                        && m.GetParameters()[0].ParameterType == typeof(int)
                        && m.GetParameters().Skip(1).All(pi =>
                            pi.ParameterType == typeof(bool) || pi.ParameterType == typeof(int)))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault();
        if (getMeta == null) return null;

        var args = getMeta.GetParameters()
            .Select((pi, i) => i == 0 ? (object)reportId
                             : pi.ParameterType == typeof(bool) ? false : (object)0)
            .ToArray();
        try { return getMeta.Invoke(nclMeta, args); }
        catch (TargetInvocationException) { return null; }
    }

    /// <summary>Construct the report instance the static RunRequestPage overloads work on.</summary>
    private static object? CreateReportForRequestPage(int reportId)
    {
        var meta = GetNclMetaReportById(reportId);
        if (meta == null) return null;
        var parent = BcRuntime.SkeletonSession;
        return CreateReportInstance(meta, parent!, skipRestoreSavedReportSettings: true);
    }

    /// <summary>
    /// Construct-and-run entry point for the static NavReport.Run / NavReport.RunModal
    /// overloads (AL <c>Report.Run(id[, ...])</c> / <c>Report.RunModal(id[, ...])</c>).
    /// Cecil-rewritten call site (NclCecilRewrite.cs §NavReport block).
    ///
    /// History (#1771): the static overload bodies used to be blanked to a bare `ret` with a
    /// separate JmpHook (AlRunner/Patches/ReportPatches.cs) throwing an out-of-scope
    /// InvalidOperationException. That JmpHook was dead code under the default Cecil-only
    /// runtime (JmpHook.Apply silently skips non-Cecil-owned methods unless
    /// AL_RUNNER_ENABLE_JMPHOOK=1 is set) — so the call fell through the `ret` and silently
    /// did nothing: no execution, no error, a false PASS. This routes the static form through
    /// the same construction + SyncRun path the AL-report-variable (instance) form already
    /// uses, closing that hole for real instead of trading a silent no-op for a throw.
    ///
    /// There is no NavReportHandle/parent for a static-by-id call (unlike the
    /// AL-report-variable path), so the report is built against the skeleton session — the
    /// same approach already proven by <see cref="CreateReportForRequestPage"/> for the
    /// static RunRequestPage overloads.
    ///
    /// <paramref name="requestWindow"/> / <paramref name="systemPrinter"/> are accepted for
    /// AL-signature compatibility but not acted on: no dialog is ever raised here, matching
    /// the existing instance-form limitation (docs/limitations.md — request pages are
    /// handler-dispatch only via explicit RunRequestPage(), not real rendering during
    /// Run/RunModal). <paramref name="record"/>, present on the 4-arg overloads, applies the
    /// AL <c>SetTableView(Rec)</c> filter before the report runs — BC's own
    /// DataItemIterator.SetTableView body is kept unmodified (see the Cecil-rewrite comment
    /// for DataItemIterator.SetTableView) so this reuses real BC filtering logic.
    /// </summary>
    public static void SyncStaticRun(int reportId, bool requestWindow, bool systemPrinter, object? record)
    {
        var meta = GetNclMetaReportById(reportId);
        if (meta == null)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"NavReport.Run/RunModal({reportId})",
                "not-yet-implemented — the runner could not construct report " + reportId +
                " to run it. See docs/scope.md");

        var parent = BcRuntime.SkeletonSession;
        var instance = CreateReportInstance(meta, parent!, skipRestoreSavedReportSettings: true);

        if (record != null)
        {
            var setTableView = instance.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SetTableView"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(record));
            if (setTableView != null)
                Invoke(setTableView, instance, new[] { record });
        }

        SyncRun(instance);
    }

    /// <summary>
    /// Run the report's request page so its [RequestPageHandler] fires. BC's own
    /// TestHandleModalForm does the dispatch; all this does is get the form there.
    ///
    /// A report that declares no request page has nothing to run — that is not an error, the
    /// parameters are simply whatever the report already carries; it counts as confirmed,
    /// because there was no dialog for anyone to cancel.
    ///
    /// Returns whether the page was CONFIRMED (the handler invoked OK), which is what
    /// decides whether BC hands the caller parameters or an empty string.
    /// </summary>
    private static bool RunRequestPageForHandler(object report, int reportId, string? parameters)
    {
        var pRequestPage = FindProperty(report.GetType(), "RequestOptionsPage");
        if (pRequestPage == null) return true;
        object? requestPage;
        try { requestPage = pRequestPage.GetValue(report); }
        catch (TargetInvocationException) { return true; }
        if (requestPage == null) return true;

        if (parameters != null)
            TrySetParameterSet(requestPage, parameters);

        // Register the request-page surface BC's dispatch will ask the client session for
        // (RunnerTestClientSession.GetPage) — it is keyed by this form, and is also how the
        // handler's OK/Cancel is read back below.
        var testPage = AlRunner.Patches.RequestPageTestPage.Bind(requestPage, report, reportId);
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_RP") == "1")
        {
            try
            {
                var mp = FindProperty(requestPage.GetType(), "MasterPage")?.GetValue(requestPage);
                var pp = mp == null ? null : FindProperty(mp.GetType(), "PageProperties")?.GetValue(mp);
                var pt = pp == null ? null : FindProperty(pp.GetType(), "PageType")?.GetValue(pp);
                var oid = FindProperty(requestPage.GetType(), "ObjectId")?.GetValue(requestPage);
                Console.Error.WriteLine($"[NavReportSync] RP DIAG report={reportId} form={requestPage.GetType().Name} objectId={oid} masterPage={(mp==null?"null":"ok")} pageType={pt}");
            }
            catch (Exception dx) { Console.Error.WriteLine("[NavReportSync] RP DIAG probe failed: " + dx.Message); }
        }

        // NOTE: do NOT pre-register the form here. BC's TestHandleModalForm registers it
        // itself on the handler-dispatch path (Company.RegisterForm right before it builds
        // the FormRunModalRequest); registering first makes that call throw
        // NavNCLInvalidFormHandleException ("a page with the identical handle has
        // previously been registered"). An earlier revision did pre-register, because back
        // then dispatch was never reached and the unregistered-handle lookup was the
        // visible failure — that is no longer the path taken.
        var runModal = requestPage.GetType().GetMethod("RunModal",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        if (runModal == null) return true;
        try
        {
            Invoke(runModal, requestPage, Array.Empty<object?>());
        }
        catch (Exception ex) when (ex.GetType().Name == "NavNCLCallbackNotAllowedException")
        {
            // NavForm.RunModalAsync consults NavTestExecution.TestHandleModalForm first and
            // only falls through to the client callback — which headless mode cannot serve —
            // when that answered "not handled". It answers "not handled" when no handler
            // matched, and the handler it looks for is chosen from
            // masterPage.PageProperties.PageType. So landing here means the request page was
            // built without a real MasterPage (see GetRealMetaReport's fallback) or the test
            // declares no [RequestPageHandler] for this report.
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                "NavReport.RunRequestPage",
                "request-page-dispatch — the request page was built but BC found no handler for "
                + "it, so it fell through to a client callback the runner cannot serve. Either "
                + "the test declares no [RequestPageHandler] for report " + reportId
                + ", or the request page has no real MasterPage (run with --verbose: "
                + "'real request page for report N could not be built'). See docs/scope.md");
        }

        return testPage.Confirmed;
    }

    /// <summary>Register a form with the skeleton company, ignoring an already-registered one.</summary>
    private static void TryRegisterForm(object form)
    {
        var session = BcRuntime.SkeletonSession;
        var company = session?.GetType()
            .GetProperty("Company", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(session);
        var register = company?.GetType().GetMethod("RegisterForm",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (register == null) return;
        try { register.Invoke(company, new[] { form }); }
        catch (TargetInvocationException) { /* already registered — that is fine */ }
    }

    private static void TrySetParameterSet(object requestPage, string parameters)
    {
        var p = FindProperty(requestPage.GetType(), "ParameterSet");
        if (p == null || !p.CanWrite) return;
        var navTextType = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")?
            .GetType("Microsoft.Dynamics.Nav.Types.NavText");
        var navTextCreate = navTextType?.GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (navTextCreate == null) return;
        try { p.SetValue(requestPage, navTextCreate.Invoke(null, new object?[] { parameters })); }
        catch (TargetInvocationException) { /* a report that rejects a parameter set keeps its own */ }
    }

    private static PropertyInfo? FindProperty(Type? t, string name)
    {
        for (; t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    /// <summary>Invoke, surfacing the AL handler's own Error() rather than a reflection wrapper.</summary>
    private static object? Invoke(MethodInfo method, object target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

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
        bool po = AlRunner.Patches.RecordPatches.IsReportProcessingOnly(reportId);
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

    private static MethodInfo? _applyDataItemTableView;   // DataItemIterator.ApplyDataItemTableViewAndRequestFormFilters()
    private static MethodInfo? _loopRootDataItems;        // DataItemIterator.LoopRootDataItemsAsync()
    private static PropertyInfo? _resultSetProcessorProp; // DataItemIterator.ResultSetProcessor
    private static Type? _nullResultSetProcessorType;     // Runtime.NullResultSetProcessor

    /// <summary>
    /// Execute the report's data items through BC's OWN loop.
    ///
    /// This deliberately does not re-implement row iteration. It calls
    /// <c>DataItemIterator.ApplyDataItemTableViewAndRequestFormFilters()</c> and then
    /// <c>DataItemIterator.LoopRootDataItemsAsync()</c> — the exact pair
    /// <c>ExecuteDataItemIteratorAsync</c> (the SaveAs / RunReportInternalCoreAsync path)
    /// runs. Everything the loop implies therefore comes from the runtime engine rather
    /// than from us: <c>SetDataItemTableView</c> copies the filters AL set through
    /// <c>Report.SetTableView(Rec)</c> off <c>DataItem.TableViewRecord</c> onto
    /// <c>DataItem.Record</c>, <c>SetDataItemLink</c> applies DataItemLink, and
    /// OnPreDataItem / OnAfterGetRecord / OnPostDataItem fire per BC's ordering with
    /// CurrReport.Skip / Break honoured.
    ///
    /// The one piece of state the loop needs that only the render path normally supplies
    /// is <c>ResultSetProcessor</c> (it calls <c>SetCurrentDataSet</c> / <c>EndLevelAsync</c>
    /// on it). We install BC's own <c>NullResultSetProcessor</c> — the instance BC's
    /// <c>ReportResultSetProcessorFactory.CreateInstance()</c> itself returns for a report
    /// with no layout — so no dataset is accumulated and nothing is rendered, which is the
    /// faithful shape for the runner's non-rendering Run(). Only installed when the report
    /// does not already carry a processor.
    /// </summary>
    private static void InvokeDataItems(object navReport)
    {
        Type? iter = navReport.GetType();
        while (iter != null && iter.Name != "DataItemIterator") iter = iter.BaseType;
        if (iter == null) return;

        EnsureResultSetProcessor(navReport, iter);

        _applyDataItemTableView ??= iter.GetMethod("ApplyDataItemTableViewAndRequestFormFilters",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        _loopRootDataItems ??= iter.GetMethod("LoopRootDataItemsAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (_loopRootDataItems == null)
            throw new InvalidOperationException(
                "DataItemIterator.LoopRootDataItemsAsync() not found — Ncl shape changed; do not commit");

        if (_applyDataItemTableView != null)
            Invoke(_applyDataItemTableView, navReport, Array.Empty<object?>());

        AwaitValueTask(Invoke(_loopRootDataItems, navReport, Array.Empty<object?>()));
    }

    /// <summary>
    /// Give the data-item loop the <c>IResultSetProcessor</c> it writes rows into.
    /// <c>NullResultSetProcessor</c> is BC's own discard implementation, chosen by BC's
    /// factory for a layout-less report, so using it here is not a runner stand-in.
    /// </summary>
    private static void EnsureResultSetProcessor(object navReport, Type dataItemIteratorType)
    {
        _resultSetProcessorProp ??= dataItemIteratorType.GetProperty("ResultSetProcessor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_resultSetProcessorProp == null) return;
        if (_resultSetProcessorProp.GetValue(navReport) != null) return;

        _nullResultSetProcessorType ??= dataItemIteratorType.Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NullResultSetProcessor")
            ?? throw new InvalidOperationException(
                "NullResultSetProcessor not found in Ncl — Ncl shape changed; do not commit");

        var setter = _resultSetProcessorProp.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException(
                "DataItemIterator.ResultSetProcessor has no setter — Ncl shape changed; do not commit");
        setter.Invoke(navReport, new[] { Activator.CreateInstance(_nullResultSetProcessorType) });
    }

    /// <summary>
    /// Block on a <c>ValueTask</c> returned reflectively. The report loop completes
    /// synchronously in the runner (no real SQL round-trips), so this never actually
    /// parks — but going through <c>AsTask().GetAwaiter().GetResult()</c> keeps the
    /// exception unwrapping identical to BC's own sync-over-async wrappers.
    /// </summary>
    private static void AwaitValueTask(object? valueTask)
    {
        if (valueTask == null) return;
        var asTask = valueTask.GetType().GetMethod("AsTask", Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"{valueTask.GetType().FullName}.AsTask() not found — cannot await report loop");
        var task = (System.Threading.Tasks.Task)asTask.Invoke(valueTask, null)!;
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (AggregateException agg) when (agg.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(agg.InnerException).Throw();
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
            // Emit-captured XML first (reports the runner compiled). Failing that, a report
            // declared by a precompiled dependency: its metadata is reconstructable from the
            // .app's own SymbolReference.json + embedded AL source (DependencyReportMetadata).
            // Without this second source the report falls through to the legacy stub, which
            // stamps ProcessingOnly — and BC then refuses SaveAs on it entirely
            // ("Report N is a processing-only report"), even though the real report renders.
            if (!AlReportMetadataRegistry.TryGet(id, out var xml))
                xml = AlRunner.Patches.RecordPatches.TryBuildDependencyReportMetadata(id);
            if (string.IsNullOrEmpty(xml)) return null;
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
                // The CreateRequestForm delegate is how MetaReport builds the report's
                // REQUEST PAGE. Left null (as it was), MetaReport.CreateMasterPage produces
                // nothing and the code below installed an empty MasterPage instead — which
                // is what made every [RequestPageHandler] unreachable: BC's handler lookup
                // (NavTestExecution.FindPageType) switches on
                // masterPage.PageProperties.PageType, an empty MasterPage reports the
                // default (a plain page), so TestHandleModalForm looked for a
                // [ModalPageHandler], found none, answered "not handled", and NavForm fell
                // through to the client callback that headless mode cannot serve.
                // Binding BC's own MetadataProvider.CreateRequestPage — the exact delegate
                // the service tier passes here — gives the genuine request page instead:
                // PageType.ReportPreview, the DataItem filter controls a handler drives, and
                // the request-page fields bound to report globals.
                BindRealCreateRequestForm(ps2, args);
                var meta = _metaReportCtor.Invoke(args);

                // Force the request-page master page NOW rather than on first use, so a
                // failure to build it is contained here and falls back to the previous
                // behaviour instead of surfacing from deep inside a report run.
                //
                // Fallback shape: an empty MasterPage carrying a minimal
                // PageProperties+SourceObject. The SaveAs report-information-xml path forces
                // NavForm.InitializeFromMetadata on the request page, which dereferences
                // masterPage.PageProperties.SourceObject.* (SaveValues, SourceTable,
                // PageDataSource); a bare `new MasterPage()` leaves PageProperties null → NRE.
                var tMaster = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MasterPage");
                var masterField = tMeta.GetField("masterPage", BindingFlags.Instance | BindingFlags.NonPublic);
                if (tMaster != null && masterField != null && masterField.GetValue(meta) == null)
                {
                    object? built = null;
                    try
                    {
                        built = tMeta.GetProperty("RequestFormMetadata",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(meta);
                    }
                    catch (Exception rex)
                    {
                        var rinner = rex is TargetInvocationException rtie ? rtie.InnerException ?? rex : rex;
                        Console.Error.WriteLine(
                            $"[NavReportSync] real request page for report {id} could not be built " +
                            $"({rinner.GetType().Name}: {rinner.Message}) — falling back to an empty MasterPage; " +
                            "its [RequestPageHandler] will not be reachable");
                    }
                    if (built == null && masterField.GetValue(meta) == null)
                        masterField.SetValue(meta, BuildRequestPageStubMasterPage(typesAsm, tMaster, id));
                }

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
    /// Fill the <c>MetaReport.CreateRequestForm</c> constructor argument with BC's own
    /// <c>MetadataProvider.CreateRequestPage</c>.
    ///
    /// This is deliberately BC's method rather than anything reimplemented here: it is
    /// what the service tier itself passes (MetadataProvider.GetReportMetadata →
    /// CreateMetaReportWithExtensions(CreateRequestPage, …)), and it is the only thing
    /// that produces a request page with PageType.ReportPreview, the auto-generated
    /// DataItem filter controls, and the report's own request-page fields.
    ///
    /// Silent no-op when the shape cannot be resolved — the caller then keeps the empty
    /// MasterPage fallback, which is the previous behaviour, and says so on stderr.
    /// </summary>
    private static void BindRealCreateRequestForm(ParameterInfo[] ctorParams, object?[] args)
    {
        try
        {
            var delegateSlot = Array.FindIndex(ctorParams, p => p.ParameterType.Name == "CreateRequestForm");
            if (delegateSlot < 0) return;

            var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            var navGlobal = nclAsm?.GetType("Microsoft.Dynamics.Nav.Runtime.NavGlobal");
            var provider = navGlobal?.GetProperty("MetadataProvider",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (provider == null) return;

            // MasterPage CreateRequestPage(MetaPageDefinition, MultiLanguage, int) — internal.
            var create = provider.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "CreateRequestPage" && m.GetParameters().Length == 3);
            if (create == null) return;

            args[delegateSlot] = Delegate.CreateDelegate(ctorParams[delegateSlot].ParameterType, provider, create);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[NavReportSync] could not bind MetadataProvider.CreateRequestPage ({ex.GetType().Name}: {ex.Message}) — " +
                "request pages will be empty and their [RequestPageHandler] unreachable");
        }
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

        bool isStubMeta = meta != null && _stubMetaReports.TryGetValue(meta, out _);

        if (metadataIsReal)
        {
            try
            {
                object? metaDataItem = null;
                if (isStubMeta)
                {
                    // Stub metadata (report lives in a precompiled dependency whose emit
                    // metadata the runner never captured — see StubInitializeMetadata).
                    // Real BC's GetDataItemByName expects the name to already be present
                    // (metadata is pre-populated at publish time); ours isn't, so build the
                    // MetaDataItem on the fly instead of calling the real lookup (which
                    // would throw ArgumentException for an as-yet-unknown name).
                    metaDataItem = BuildSyntheticFlatMetaDataItem(meta!, dataItemName ?? $"DataItem{(list?.Count ?? 0)}");
                }
                else if (dataItemName != null)
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
            if (f != null) AlRunner.Infrastructure.FieldPoke.SetInstance(f, self, value!);
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
                AlRunner.Infrastructure.FieldPoke.SetInstance(f, navReport,
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

        var reportType = AlRunner.BcRuntime.FindReportTypePublic(id)
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

        // Seed the skeleton state BEFORE the saved-settings restore, exactly as this
        // method did before CompleteReportConstruction was factored out — the restore
        // walk reads session/ObjectId state. Both seeds are idempotent, so the
        // CompleteReportConstruction call below is a no-op for them.
        SeedSessionSystemTenant(parent);
        SeedObjectId(instance, id);

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
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        CompleteReportConstruction(instance, parent, id);
        return instance;
    }

    /// <summary>
    /// The post-construction steps BC's own <c>NCLMetaReport.CreateObjectInstance</c> runs
    /// on a freshly constructed report, plus the two pieces of skeleton state that path
    /// depends on. Idempotent, so it is safe to run from every construction entry point.
    ///
    /// This MUST run for EVERY report instance, however it was constructed — including the
    /// AL-report-variable path (<c>NavReportHandle.CreateTarget</c>), which bypasses
    /// <c>CreateObjectInstance</c> entirely. <c>FinalizeDataItemLoading</c> is what allocates
    /// <c>DataItemIterator.TableViewIsSet</c> (<c>new bool[DataItems.Count]</c>); skipping it
    /// leaves that array null, and BC's own <c>DataItemIterator.SetTableView</c> then NREs on
    /// <c>TableViewIsSet[i] = true</c> the moment AL calls <c>Report.SetTableView(Rec)</c>
    /// (issue #1718). It also builds each DataItem's parent/child links, which the data-item
    /// loop walks.
    /// </summary>
    public static void CompleteReportConstruction(object instance, object? parent, int reportId)
    {
        // The report executes on `parent` (its owning session/handle). BC's own
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
        SeedSessionSystemTenant(TryGetSession(instance));

        // Seed the inherited base.ObjectId with the true report id. The compiled
        // Report{id} ctor chain leaves NavApplicationObjectBase.objectId at
        // ObjectNumber=0 (a known runner wiring quirk worked around elsewhere by
        // parsing the type name). Paths such as NavReport.DetermineStandardLayoutAsync
        // key off base.ObjectId.ObjectNumber and otherwise resolve "report 0".
        SeedObjectId(instance, reportId);

        try
        {
            // BC's MetaReport(XmlNode) ctor calls BuildDataItemTree() before the report runs;
            // report metadata that did not come through that ctor leaves each MetaDataItem's
            // ChildDataItems null (→ NRE in FindDataItemChildrenAndParent) and OwningDataItem
            // unset (→ ArgumentNullException 'parent'). Replicate it here. Idempotent.
            EnsureDataItemTreeBuilt(instance);

            var fin = instance.GetType().GetMethod("FinalizeDataItemLoading",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            fin?.Invoke(instance, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    private static PropertyInfo? _appObjBaseSessionProp;

    /// <summary>The <c>NavApplicationObjectBase.Session</c> the object runs on, or null.</summary>
    private static object? TryGetSession(object instance)
    {
        try
        {
            if (_appObjBaseSessionProp == null)
            {
                Type? t = instance.GetType();
                while (t != null && _appObjBaseSessionProp == null)
                {
                    _appObjBaseSessionProp = t.GetProperty("Session",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly);
                    t = t.BaseType;
                }
            }
            return _appObjBaseSessionProp?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static PropertyInfo? _diDataItemsProp;   // DataItemIterator.DataItems (List<DataItem>)
    private static MethodInfo? _mdiBuildChildren;     // MetaDataItem.BuildDataItemChildren(List<MetaDataItem>)

    /// <summary>
    /// Replicates BC MetaReport.BuildDataItemTree(): for every report DataItem's MetaDataItem,
    /// call BuildDataItemChildren(allMetaDataItems) so MetaDataItem.ChildDataItems (null until
    /// built) is populated and each child's OwningDataItem is set. BC does this inside the
    /// MetaReport(XmlNode) ctor; report metadata that did not come through that ctor leaves the
    /// links unbuilt, so DataItemIterator.FinalizeDataItemLoading → FindDataItemChildrenAndParent
    /// dereferences the null ChildDataItems (NRE) and leaves Parent/OwningDataItem null
    /// (ArgumentNullException 'parent' downstream). BuildDataItemChildren is idempotent (no-ops
    /// when childDataItems is already set), so this is a safe no-op when the metadata was
    /// tree-built upstream. Observably equivalent to real BC's report-load: same links, built by
    /// BC's own method on BC's own MetaDataItem instances.
    /// </summary>
    private static Type? _synthMdiType;                     // Types.Metadata.MetaDataItem
    private static ConstructorInfo? _synthMdiCtor;           // MetaDataItem() (public, parameterless)
    private static PropertyInfo? _synthMdiVarNameProp;       // MetaDataItem.DataItemVarName (private set)
    private static PropertyInfo? _synthMdiExtNameProp;       // MetaDataItem.ExternalizedName (private set)
    private static FieldInfo? _synthMdiChildDataItemsField;  // MetaDataItem.childDataItems : List<MetaDataItem>

    /// <summary>
    /// Builds a minimal, real (field-initializer-run) <c>MetaDataItem</c> for a data
    /// item whose report lives in a precompiled dependency the runner never
    /// source-compiled — see the doc comment on <see cref="_stubMetaReports"/> for why
    /// real BC's <c>GetDataItemByName</c> cannot be used here.
    ///
    /// Faithfulness: <c>DataItemIndent</c> is left at its default (0), i.e. every
    /// data item is modeled as a report-root with no children. This is an HONEST
    /// simplification, not a silent one: v0's DataItemIterator does not yet execute
    /// nested dataitem row-iteration for ANY report (documented at the top of this
    /// file) — flattening the (unknown, uncaptured) real nesting changes nothing
    /// observable for what the runner currently executes. Each data item still gets
    /// its own OnPreDataItem/OnPostDataItem trigger dispatch and column evaluation;
    /// only parent/child row-nesting (already unimplemented) is affected.
    /// <c>ChildDataItems</c> is set directly to a real empty list (rather than routed
    /// through the real <c>BuildDataItemChildren</c>, whose name-matching walk
    /// presupposes the full report-order list is known up front, which it isn't here
    /// — data items are discovered one at a time as ReportAdd is called).
    /// </summary>
    private static object BuildSyntheticFlatMetaDataItem(object meta, string dataItemName)
    {
        if (_synthMdiType == null)
        {
            _synthMdiType = meta.GetType().Assembly.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaDataItem")
                ?? throw new InvalidOperationException("MetaDataItem type not found");
            _synthMdiCtor = _synthMdiType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            _synthMdiVarNameProp = _synthMdiType.GetProperty("DataItemVarName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _synthMdiExtNameProp = _synthMdiType.GetProperty("ExternalizedName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _synthMdiChildDataItemsField = _synthMdiType.GetField("childDataItems",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (_synthMdiCtor == null)
            throw new InvalidOperationException("MetaDataItem() parameterless ctor not found");

        var mdi = _synthMdiCtor.Invoke(null);
        _synthMdiVarNameProp?.SetValue(mdi, dataItemName);
        _synthMdiExtNameProp?.SetValue(mdi, dataItemName);
        if (_synthMdiChildDataItemsField != null)
        {
            var emptyChildren = Activator.CreateInstance(
                typeof(List<>).MakeGenericType(_synthMdiChildDataItemsField.FieldType.GetGenericArguments()[0]));
            _synthMdiChildDataItemsField.SetValue(mdi, emptyChildren);
        }

        // Register into the stub MetaReport's own dataItems list too — faithful to
        // real BC (GetDataItemByName/DataItems reflect every data item that has been
        // bound), and lets a later ReportAdd for the SAME name (legacy index-based
        // Add(dataItem) overload, dataItemName == null) find it again if ever needed.
        _metaReportDataItemsField ??= meta.GetType().GetField("dataItems",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (_metaReportDataItemsField?.GetValue(meta) is System.Collections.IList metaList)
            metaList.Add(mdi);

        return mdi;
    }

    private static void EnsureDataItemTreeBuilt(object instance)
    {
        _diDataItemsProp ??= instance.GetType().GetProperty("DataItems",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_diDataItemsProp?.GetValue(instance) is not System.Collections.IList dataItems || dataItems.Count == 0)
            return;

        _dataItemMetaData ??= dataItems[0]!.GetType().GetProperty("MetaData",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_dataItemMetaData == null) return;

        // Gather each DataItem's MetaDataItem in report (declaration) order — the order
        // BuildDataItemChildren's DataItemIndent walk depends on.
        var metas = new List<object>(dataItems.Count);
        foreach (var di in dataItems)
        {
            var m = di == null ? null : _dataItemMetaData.GetValue(di);
            if (m == null) return; // metadata incomplete — don't half-build; leave existing path
            metas.Add(m);
        }

        var mdiType = metas[0].GetType();
        // BuildDataItemChildren's parameter is List<MetaDataItem>; build that exact typed list.
        var typedList = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(mdiType))!;
        foreach (var m in metas) typedList.Add(m);

        _mdiBuildChildren ??= mdiType.GetMethod("BuildDataItemChildren",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_mdiBuildChildren == null) return;

        foreach (var m in metas)
            _mdiBuildChildren.Invoke(m, new object[] { typedList });
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
        var skeletonTenant = AlRunner.BcRuntime.SkeletonSystemTenant;
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
                AlRunner.Infrastructure.FieldPoke.SetInstance(_sessionSystemTenantField, session, skeletonTenant);
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
