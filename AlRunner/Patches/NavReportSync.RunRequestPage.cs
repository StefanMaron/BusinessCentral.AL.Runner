// NavReportSync — the request page Report.Run() opens, and the dataset a test asks for
// from inside its [RequestPageHandler].
//
// WHAT WAS MISSING (#2436)
//   SyncRun ran a report's lifecycle and then, for any report that is not ProcessingOnly,
//   threw out-of-scope on layout resolution. Two things were wrong with that for a report
//   run UNDER A TEST:
//
//     1. The request page was never opened at all, so a [RequestPageHandler] declared for
//        the report was silently never called. That is not what BC does — its
//        RunReportInternalCoreAsync opens the request page whenever
//        `UseRequestForm && RequestOptionsPage != null`, and under test that page goes to
//        the declared handler.
//     2. Microsoft's dominant report-test shape closes that request page with
//        `TestRequestPage.SaveAsXml(parametersFile, dataSetFile)`, which asks for the
//        report's DATASET, not a rendered layout. The dataset is report EXECUTION, which is
//        in scope (docs/scope.md#report-rendering puts only RENDERING out of scope), so
//        throwing on layout ended tests that never wanted a layout.
//
// WHAT THIS ADDS, AND WHOSE CODE DOES THE WORK
//   Every decision below is BC's own, taken from NavReport.RunReportInternalCoreAsync /
//   RunRequestPageCoreAsync / ReportResultSetProcessorFactory.GetTestResultProcessor:
//
//     * open the request page (BC's TestHandleModalForm dispatches it to the handler);
//     * FormResult.Cancel  → the report body does not run, and no error is raised;
//     * FormResult.OK on a report that is NOT ProcessingOnly → BC does not offer an OK
//       action on such a request page at all, so invoking one raises BC's own
//       "The built-in action = OK is not found on the page." (RequestPageTestPage);
//     * TestExecution.ReportOutputFileName set (what NavTestPage.ALSaveAsXml writes) →
//       install BC's own ReportSaveAsXmlRenderer as the data-item loop's ResultSetProcessor
//       and let it write the dataset file, instead of resolving a layout.
//
//   Nothing here re-implements a dataset format or a renderer: the file is produced by
//   Ncl's ReportSaveAsXmlRenderer over a NavDataSet built by Types' NavDataSetBuilder, the
//   same two objects BC uses on a real service tier.
//
// DELIBERATE DIVERGENCE
//   When the request page cannot be dispatched because the test declares no handler for it,
//   this falls back to NOT opening one — the pre-#2436 behaviour. Real BC raises there. The
//   runner cannot yet tell "no handler declared" apart from "handler lookup did not reach
//   us", and turning the second into an error would refuse reports that run fine today, so
//   the fallback is additive on purpose. It is recorded in docs/limitations.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AlRunner;

public static partial class NavReportSync
{
    /// <summary>How the request page Report.Run() opened was closed.</summary>
    internal enum RequestPageOutcome
    {
        /// <summary>No request page was opened (none exists, `UseRequestPage(false)`, or no handler).</summary>
        NotShown,
        /// <summary>The handler closed it with OK / LookupOK — no report intent selected.</summary>
        ConfirmedPlainOk,
        /// <summary>The handler asked for an output (SaveAsXml, preview, print, …).</summary>
        ConfirmedWithIntent,
        /// <summary>The handler cancelled. BC returns ReportExecutionResult.Cancel; the body never runs.</summary>
        Cancelled,
    }

    /// <summary>
    /// Open the report's request page so its <c>[RequestPageHandler]</c> fires, mirroring
    /// <c>RunReportInternalCoreAsync</c>'s <c>runWithRequestPage</c> branch:
    /// <c>!(!UseRequestForm || IsBackGroundSession) &amp;&amp; displayResult &amp;&amp; RequestOptionsPage != null</c>.
    /// <c>displayResult</c> is true for Run/RunModal, which is the only caller here.
    /// </summary>
    private static RequestPageOutcome RunRequestPageForReportRun(object navReport, Type navReportBase)
    {
        if (!ReadBoolProperty(navReport, "UseRequestForm", fallback: true)) return RequestPageOutcome.NotShown;
        if (ReadBoolProperty(navReport, "IsBackGroundSession", fallback: false)) return RequestPageOutcome.NotShown;

        var pRequestPage = FindProperty(navReport.GetType(), "RequestOptionsPage");
        if (pRequestPage == null) return RequestPageOutcome.NotShown;
        object? requestPage;
        try { requestPage = pRequestPage.GetValue(navReport); }
        catch (TargetInvocationException) { return RequestPageOutcome.NotShown; }
        if (requestPage == null) return RequestPageOutcome.NotShown;

        int reportId = TryGetObjectId(navReport, navReportBase);
        try
        {
            // offersOk: a plain OK closes a request page only when the report has no output
            // to choose — i.e. it is ProcessingOnly. On a report that renders, real BC does
            // not put an OK action on the page at all; see RequestPageTestPage.GetBuiltInAction.
            RunRequestPageForHandler(navReport, reportId, parameters: null,
                offersOk: IsProcessingOnly(navReport, navReportBase));
        }
        catch (AlRunner.Infrastructure.RunnerOutOfScopeException ex)
            when (ex.Message.Contains("request-page-dispatch", StringComparison.Ordinal))
        {
            // ONLY "the request page was built but BC found no handler for it" — see
            // RunRequestPageForHandler. Report.Run() worked without a request page before
            // #2436 and must keep working; see DELIBERATE DIVERGENCE at the top of the file.
            //
            // The reason filter is load-bearing. Catching every RunnerOutOfScopeException
            // swallowed the ones raised INSIDE the handler — a handler that sets a
            // request-page control raises request-page-control, and turning that into "no
            // request page" made the run fall through to the layout throw, which reported
            // report rendering as the unsupported surface when the actual one was the
            // control. That is a worse message, not a smaller failure.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_RP") == "1")
                Console.Error.WriteLine(
                    $"[NavReportSync] Run({reportId}): no [RequestPageHandler] matched — continuing without a request page");
            return RequestPageOutcome.NotShown;
        }

        var page = AlRunner.Patches.RequestPageTestPage.TryGetFor(requestPage);
        var formResult = page?.FormResult;
        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_RP") == "1")
            Console.Error.WriteLine($"[NavReportSync] Run({reportId}): request page closed with {formResult}");

        // BC's RunRequestPageCoreAsync maps the FormResult onto a ReportIntent: Pdf / Word /
        // Excel / Xml / ExcelDataset → Download, Preview / PreviewPrint → Preview,
        // Print → Print, Schedule → Schedule; everything else (OK, Cancel, None) → None.
        var name = formResult?.ToString();
        switch (name)
        {
            case "Cancel":
                return RequestPageOutcome.Cancelled;
            case "OK":
            case "LookupOK":
                // Only reachable for a ProcessingOnly report — offersOk above makes OK absent
                // on any other request page, so BC's own "built-in action = OK is not found"
                // fires before this ever sees an OK from a report that renders.
                return RequestPageOutcome.ConfirmedPlainOk;
            case null:
            case "None":
                // A handler that returned without closing the page. BC treats an unclosed
                // request page as cancelled — there is no intent and no confirmation.
                return RequestPageOutcome.Cancelled;
            default:
                return RequestPageOutcome.ConfirmedWithIntent;
        }
    }

    // ── the test dataset (TestRequestPage.SaveAsXml) ──────────────────────────

    private static Type? _reportSaveAsXmlRendererType;
    private static ConstructorInfo? _reportSaveAsXmlRendererCtor;
    private static Type? _getReportParametersDelegateType;
    private static MethodInfo? _navReportGetParameters;
    private static MethodInfo? _createNavDataSet;

    /// <summary>
    /// Build the processor BC's <c>ReportResultSetProcessorFactory.GetTestResultProcessor</c>
    /// builds when a test asked for a dataset: <c>NavTestPage.ALSaveAsXml</c> (what AL's
    /// <c>TestRequestPage.SaveAsXml(parametersFile, dataSetFile)</c> compiles to) parks the two
    /// file names plus <c>FormResult.Xml</c> on the session's TestExecution, and this reads them
    /// back off it. Returns null when no dataset was asked for.
    ///
    /// The three TestExecution fields are cleared afterwards, exactly as BC's own factory does —
    /// otherwise the next report to run in the same test session would write over this test's
    /// dataset file.
    /// </summary>
    private static object? TryCreateTestDatasetProcessor(object navReport)
    {
        var testExecution = FindProperty(navReport.GetType(), "Session")?.GetValue(navReport) is object session
            ? FindProperty(session.GetType(), "TestExecution")?.GetValue(session)
            : null;
        if (testExecution == null) return null;

        var pOutFile = FindProperty(testExecution.GetType(), "ReportOutputFileName");
        var pParamFile = FindProperty(testExecution.GetType(), "ReportParameterOutputFileName");
        var pFormat = FindProperty(testExecution.GetType(), "ReportOutputFormat")
            ?? throw new InvalidOperationException(
                "NavTestExecution.ReportOutputFormat not found — Ncl shape changed; do not commit");
        if (pOutFile?.GetValue(testExecution) is not string dataSetFileName || dataSetFileName.Length == 0)
            return null;

        // #2887: WHICH output the handler asked for decides this, not merely that it parked a
        // file name. BC's own GetTestResultProcessor branches on the format FIRST:
        //
        //     NavReportFormat f = Parameters.ReportTargetFormat;
        //     if (f != None && f != Xml) { ...clear the three fields...; return null; }
        //     return new ReportSaveAsXmlRenderer(...);
        //
        // This method implemented only the second half. Every ALSaveAs* on a TestRequestPage
        // parks a file name (NavTestPage.ALSaveAsExcel sets ReportOutputFileName plus
        // ReportOutputFormat = FormResult.Excel and invokes the built-in Excel action), so a
        // handler calling SaveAsExcel got a ReportSaveAsXmlRenderer: an XML dataset written
        // into the .xlsx path it asked for, reported as success, with the rendering step
        // skipped because a dataset had been "written". Six Tests-SINGLESERVER tests in
        // Codeunit134335 failed several statements later inside the test toolkit's OpenXml
        // reader ("File contains corrupted data"), naming a corrupt workbook rather than the
        // unsupported surface — the silent wrong answer .claude/rules/loud-failures.md exists
        // to prevent.
        //
        // Returning null installs the NullResultSetProcessor instead, so datasetWritten stays
        // false and TryRunOrControlFlow goes on to InvokeLayoutForReport — the existing loud
        // out-of-scope refusal for rendering, which is what a real service tier's renderer
        // would have been asked for here.
        //
        // Not mirrored from BC, deliberately: the ReportTargetPath copy and the
        // Download -> Save intent flip in that branch feed a renderer the runner refuses
        // before it can consume them.
        if (!RequestedOutputIsDataset(pFormat.GetValue(testExecution)?.ToString()))
        {
            ClearParkedReportOutput(testExecution, pOutFile, pParamFile, pFormat);
            if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_RP") == "1")
                Console.Error.WriteLine(
                    "[NavReportSync] request page asked for a rendered artifact, not a dataset "
                    + "— no test processor; the rendering path refuses");
            return null;
        }

        var parametersFileName = pParamFile?.GetValue(testExecution) as string;

        var metadata = FindProperty(navReport.GetType(), "Metadata")?.GetValue(navReport)
            ?? throw new InvalidOperationException(
                "NavReport.Metadata is null — cannot build the dataset a [RequestPageHandler] asked for");

        var renderer = CreateReportSaveAsXmlRenderer(navReport, parametersFileName, dataSetFileName, metadata);

        // BC clears all three here, not after rendering — and in BOTH of its branches, which
        // is why the refusal above clears them too: a parked file name that outlives the run
        // that asked for it is the next report's dataset, written where nobody asked.
        ClearParkedReportOutput(testExecution, pOutFile, pParamFile, pFormat);

        if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_RP") == "1")
            Console.Error.WriteLine($"[NavReportSync] dataset requested → {dataSetFileName}");
        return renderer;
    }

    /// <summary>
    /// BC's rule, from <c>ReportResultSetProcessorFactory.GetTestResultProcessor</c>: only
    /// <c>None</c> and <c>Xml</c> are answered with the dataset renderer. Every other
    /// requested output is a rendered artifact — Excel, Word, Pdf, print, preview — which the
    /// runner has no service tier to produce and refuses loudly on the rendering path.
    ///
    /// Fails CLOSED on a name it does not recognise: a FormResult value added or renamed in a
    /// future BC build must not be silently answered with an XML dataset in a file named for
    /// something else, which is the defect this rule exists to close (#2887). The paired
    /// rot guard in AlRunner.Tests fails when Ncl stops declaring the names below.
    /// </summary>
    internal static bool RequestedOutputIsDataset(string? formResultName)
        => formResultName is "None" or "Xml";

    /// <summary>
    /// Drop the output request a <c>[RequestPageHandler]</c> parked on the session, exactly as
    /// BC's own factory does — in both of its branches, whether or not it built a processor.
    /// </summary>
    private static void ClearParkedReportOutput(
        object testExecution, PropertyInfo? pOutFile, PropertyInfo? pParamFile, PropertyInfo pFormat)
    {
        pOutFile?.SetValue(testExecution, null);
        pParamFile?.SetValue(testExecution, null);
        if (pFormat.PropertyType.IsEnum)
            pFormat.SetValue(testExecution, Enum.Parse(pFormat.PropertyType, "None"));
    }

    private static object CreateReportSaveAsXmlRenderer(
        object navReport, string? parametersFileName, string dataSetFileName, object metaReport)
    {
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl")
            ?? throw new InvalidOperationException("Ncl assembly not loaded");
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("Types assembly not loaded");

        _reportSaveAsXmlRendererType ??= nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.ReportSaveAsXmlRenderer")
            ?? throw new InvalidOperationException(
                "ReportSaveAsXmlRenderer not found in Ncl — Ncl shape changed; do not commit");
        _getReportParametersDelegateType ??=
            _reportSaveAsXmlRendererType.GetNestedType("GetReportParameters", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ReportSaveAsXmlRenderer.GetReportParameters delegate not found — Ncl shape changed; do not commit");
        // internal ReportSaveAsXmlRenderer(string parametersFileName, GetReportParameters
        //                                  reportParameters, string dataSetFileName, NavDataSet dataSet)
        _reportSaveAsXmlRendererCtor ??= _reportSaveAsXmlRendererType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 4
                    && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == _getReportParametersDelegateType
                    && ps[2].ParameterType == typeof(string);
            })
            ?? throw new InvalidOperationException(
                "ReportSaveAsXmlRenderer(string, GetReportParameters, string, NavDataSet) not found — "
                + "Ncl shape changed; do not commit");

        // NavReport.GetParameters() : KeyValuePair<string,string>[] — the same method BC hands
        // the renderer, so the parameters file carries the filters the handler actually set.
        _navReportGetParameters ??= FindMethodUpHierarchy(navReport.GetType(), "GetParameters")
            ?? throw new InvalidOperationException(
                "NavReport.GetParameters() not found — Ncl shape changed; do not commit");

        _createNavDataSet ??= typesAsm.GetType("Microsoft.Dynamics.Nav.Types.NavDataSetBuilder")?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CreateNavDataSet"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.IsInstanceOfType(metaReport))
            ?? throw new InvalidOperationException(
                "NavDataSetBuilder.CreateNavDataSet(MetaReport) not found — Types shape changed; do not commit");

        var dataSet = Invoke(_createNavDataSet, null!, new[] { metaReport });
        var getParameters = Delegate.CreateDelegate(_getReportParametersDelegateType, navReport, _navReportGetParameters);
        try
        {
            return _reportSaveAsXmlRendererCtor.Invoke(
                new object?[] { parametersFileName, getParameters, dataSetFileName, dataSet });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// <c>ResultSetProcessor.StartAsync()</c> / <c>FinishAsync()</c> — the pair
    /// <c>ExecuteDataItemIteratorAsync</c> brackets <c>LoopRootDataItemsAsync</c> with. For the
    /// XML renderer, StartAsync writes the document header + schema and FinishAsync closes it,
    /// so the file only exists if both run.
    /// </summary>
    private static void InvokeProcessorLifecycle(object processor, string methodName)
    {
        var m = processor.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException(
                $"{processor.GetType().Name}.{methodName}() not found — Ncl shape changed; do not commit");
        AwaitValueTask(Invoke(m, processor, Array.Empty<object?>()));
    }

    private static bool ReadBoolProperty(object target, string name, bool fallback)
    {
        var p = FindProperty(target.GetType(), name);
        if (p == null || p.PropertyType != typeof(bool)) return fallback;
        try { return p.GetValue(target) is bool b ? b : fallback; }
        catch (TargetInvocationException) { return fallback; }
    }

    private static MethodInfo? FindMethodUpHierarchy(Type? t, string name)
    {
        for (; t != null; t = t.BaseType)
        {
            var m = t.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (m != null) return m;
        }
        return null;
    }
}
