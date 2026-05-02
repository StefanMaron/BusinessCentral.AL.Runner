using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Standalone replacement for BC's report handle. Supports helper-procedure
/// dispatch on generated ReportNNNN classes plus the minimal Run/RunRequestPage
/// surface that tests compile against.
/// </summary>
public class MockReportHandle
{
    public int ReportId { get; }

    private object? _reportInstance;
    private MockRecordHandle? _tableView;

    /// <summary>
    /// UseRequestForm property — maps to UseRequestPage(bool) in AL.
    /// BC emits: rep.Target.UseRequestForm = false;
    /// When false, Run/RunModal skip request page handler invocation.
    /// </summary>
    public bool UseRequestForm { get; set; } = true;

    // ── IsReadOnly / ObjectID ─────────────────────────────────────────────────

    /// <summary>BC emits <c>rep.ALIsReadOnly</c> for <c>Report.IsReadOnly()</c>. Always false in standalone mode.</summary>
    public bool ALIsReadOnly => false;

    /// <summary>BC emits <c>rep.ObjectID(useIdChar)</c> for <c>Report.ObjectId(UseIdChar)</c>. Returns the report ID as text.</summary>
    public string ObjectID(bool useIdChar) => ReportId.ToString();

    // ── Layout methods ────────────────────────────────────────────────────────

    /// <summary>BC emits <c>rep.DefaultLayout()</c> — returns None (no rendering in standalone mode).</summary>
    public NavDefaultLayout DefaultLayout() => default;

    /// <summary>BC emits <c>rep.RDLCLayout(errorLevel, ByRef&lt;InStream&gt;)</c> — no layout data in standalone mode.</summary>
    public bool RDLCLayout(DataError errorLevel, ByRef<MockInStream> inStream) => false;

    /// <summary>BC emits <c>rep.WordLayout(errorLevel, ByRef&lt;InStream&gt;)</c> — no layout data in standalone mode.</summary>
    public bool WordLayout(DataError errorLevel, ByRef<MockInStream> inStream) => false;

    /// <summary>BC emits <c>rep.ExcelLayout(errorLevel, ByRef&lt;InStream&gt;)</c> — no layout data in standalone mode.</summary>
    public bool ExcelLayout(DataError errorLevel, ByRef<MockInStream> inStream) => false;

    // ── TargetFormat / FormatRegion / Language ─────────────────────────────────

    /// <summary>BC emits <c>rep.ALTargetFormat</c> for <c>Report.TargetFormat()</c>. Returns default (None) in standalone mode.</summary>
    public NavReportFormat ALTargetFormat => default;

    /// <summary>BC emits <c>rep.FormatRegion = value</c> for <c>Report.FormatRegion(value)</c>.</summary>
    public string FormatRegion { get; set; } = string.Empty;

    /// <summary>BC emits <c>rep.Language = value</c> for <c>Report.Language(value)</c>.</summary>
    public int Language { get; set; } = 0;

    // ── WordXmlPart ───────────────────────────────────────────────────────────

    /// <summary>BC emits <c>rep.WordXmlPart()</c> for <c>Report.WordXmlPart()</c>. Returns empty in standalone mode.</summary>
    public string WordXmlPart() => string.Empty;

    // ── SaveAs* instance methods ───────────────────────────────────────────────
    // BC AL: ReportInstance.SaveAs/SaveAsPdf/etc. all return Boolean (true on success) — issue #1432.
    // The standalone runner cannot perform real file I/O so these are no-ops that
    // return true to allow `if not Rep.SaveAs(...)` guards to compile and run.

    /// <summary>BC emits <c>rep.SaveAsPdf(errorLevel, path)</c> for <c>Report.SaveAsPdf(FileName)</c>. Returns true (no-op) in standalone mode.</summary>
    public bool SaveAsPdf(DataError errorLevel, string path) => true;

    /// <summary>BC emits <c>rep.SaveAsExcel(errorLevel, path)</c> for <c>Report.SaveAsExcel(FileName)</c>. Returns true (no-op) in standalone mode.</summary>
    public bool SaveAsExcel(DataError errorLevel, string path) => true;

    /// <summary>BC emits <c>rep.SaveAsWord(errorLevel, path)</c> for <c>Report.SaveAsWord(FileName)</c>. Returns true (no-op) in standalone mode.</summary>
    public bool SaveAsWord(DataError errorLevel, string path) => true;

    /// <summary>BC emits <c>rep.SaveAsHtml(errorLevel, path)</c> for <c>Report.SaveAsHtml(FileName)</c>. Returns true (no-op) in standalone mode.</summary>
    public bool SaveAsHtml(DataError errorLevel, string path) => true;

    /// <summary>BC emits <c>rep.SaveAsXml(errorLevel, path)</c> for <c>Report.SaveAsXml(FileName)</c>. Returns true (no-op) in standalone mode.</summary>
    public bool SaveAsXml(DataError errorLevel, string path) => true;

    /// <summary>BC emits <c>rep.SaveAs(errorLevel, params, format, ByRef&lt;OutStream&gt;)</c> for <c>Report.SaveAs(Params, Format, OutStream)</c>. Returns true (no-op) in standalone mode.</summary>
    public bool SaveAs(DataError errorLevel, string requestParams, NavReportFormat format, ByRef<MockOutStream> outStream) => true;

    // ── CreateTotals ──────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>rep.CreateTotals()</c> for <c>ReportInstance.CreateTotals()</c> (0-arg overload).
    /// No-op in standalone mode — the totals engine is a BC service-tier concept.
    /// </summary>
    public void CreateTotals() { }

    /// <summary>
    /// BC emits <c>rep.CreateTotals(field1 [, field2, ...])</c> for the N-arg overload of
    /// <c>ReportInstance.CreateTotals(Field1 [, Field2, ...])</c>.
    /// No-op in standalone mode.
    /// </summary>
    public void CreateTotals(params object[] fields) { }

    /// <summary>
    /// BC emits <c>rep.CreateTotals(decimal1, decimal2)</c> for
    /// <c>ReportInstance.CreateTotals(Field1, Field2)</c> when both fields are Decimal.
    /// No-op in standalone mode — the totals engine is a BC service-tier concept.
    /// </summary>
    public void CreateTotals(decimal field1, decimal field2) { }

    // ── ShowOutput ────────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits <c>rep.ALShowOutput(value)</c> for <c>CurrReport.ShowOutput(value)</c>.
    /// Controls whether the current line is included in output. No-op in standalone mode
    /// (no rendering engine exists; all lines are always "included").
    /// </summary>
    public void ALShowOutput(bool value) { }

    public MockReportHandle() { }

    public MockReportHandle(int reportId)
    {
        ReportId = reportId;
    }

    public void SetTableView(MockRecordHandle rec)
    {
        _tableView = rec;
    }

    public void Run()
    {
        // If a ReportHandler is registered, invoke it instead of running the report class
        if (HandlerRegistry.InvokeReportHandler(ReportId))
            return;

        // If UseRequestPage is true and a RequestPageHandler is registered, show the request page
        if (UseRequestForm)
            HandlerRegistry.TryInvokeRequestPageHandler(ReportId);

        var report = EnsureReportInstance();
        if (report == null)
            return;

        ExecuteReportLifecycle(report);
    }

    public void RunModal()
    {
        // If a ReportHandler is registered, invoke it instead of running the report class
        if (HandlerRegistry.InvokeReportHandler(ReportId))
            return;

        // If UseRequestPage is true and a RequestPageHandler is registered, show the request page
        if (UseRequestForm)
            HandlerRegistry.TryInvokeRequestPageHandler(ReportId);

        var report = EnsureReportInstance();
        if (report == null)
            return;

        ExecuteReportLifecycle(report);
    }

    /// <summary>
    /// Executes the report lifecycle:
    /// OnPreReport → for each data-item: OnPreDataItem → [OnAfterGetRecord per row] → OnPostDataItem → OnPostReport.
    /// The BC service tier normally orchestrates this; we replicate it here since
    /// the base class Run() override is stripped by the rewriter.
    /// </summary>
    private static void ExecuteReportLifecycle(object report)
    {
        var reportType = report.GetType();

        InvokeTrigger(report, reportType, "OnPreReport");
        ExecuteDataItems(report, reportType);
        InvokeTrigger(report, reportType, "OnPostReport");
    }

    /// <summary>
    /// Discovers all MockRecordHandle fields in the report class (each represents
    /// a data-item), resolves their table IDs via <see cref="TableFieldRegistry"/>,
    /// and runs the Pre/AfterGetRecord/Post trigger sequence for each one.
    /// </summary>
    private static void ExecuteDataItems(object report, Type reportType)
    {
        var dataItemFields = reportType
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(MockRecordHandle))
            .ToList();

        foreach (var field in dataItemFields)
        {
            var fieldName = field.Name;

            // Resolve the table ID by matching the field name against all registered
            // table names (stripping spaces/punctuation for the comparison).
            var tableId = TableFieldRegistry.GetTableIdByNormalizedName(fieldName);
            if (tableId == null)
                continue;

            // Initialise the field if BC did not do so (it is null in generated code).
            var rec = (MockRecordHandle?)field.GetValue(report) ?? new MockRecordHandle(tableId.Value);
            field.SetValue(report, rec);

            InvokeTrigger(report, reportType, $"{fieldName}_a45_OnPreDataItem");

            if (rec.ALFindSet(DataError.TrapError))
            {
                do
                {
                    InvokeTrigger(report, reportType, $"{fieldName}_a45_OnAfterGetRecord");
                }
                while (rec.ALNext() != 0);
            }

            InvokeTrigger(report, reportType, $"{fieldName}_a45_OnPostDataItem");
        }
    }

    private static void InvokeTrigger(object report, Type reportType, string name)
    {
        var method = reportType.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        method?.Invoke(report, null);
    }

    public string RunRequestPage()
    {
        if (UseRequestForm)
            HandlerRegistry.InvokeRequestPageHandler(ReportId);
        return "<RequestPage />";
    }

    /// <summary>
    /// Instance <c>Rep.RunRequestPage(requestParameters)</c> — 1-argument overload.
    /// BC emits this when AL code calls <c>SomeReport.RunRequestPage(OldParameters)</c>
    /// on a report variable. Returns empty string in standalone mode (no request-page UI).
    /// </summary>
    public string RunRequestPage(string requestParameters) => string.Empty;

    /// <summary>
    /// Extension-scoped Invoke — called when invoking a method defined in a report
    /// extension. The BC compiler emits (extensionId, memberId, args).
    /// We ignore the extensionId and delegate to the standard Invoke.
    /// </summary>
    public object? Invoke(int extensionId, int memberId, object[] args)
    {
        return Invoke(memberId, args);
    }

    public object? Invoke(int memberId, object[] args)
    {
        var report = EnsureReportInstance();
        if (report == null)
            return null;

        var reportType = report.GetType();
        var absMemberId = Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();

        foreach (var nested in reportType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!nested.Name.Contains($"_Scope_{memberIdStr}") &&
                !nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                continue;
            }

            var scopeIdx = nested.Name.IndexOf("_Scope_", StringComparison.Ordinal);
            if (scopeIdx < 0)
                continue;

            var methodName = nested.Name.Substring(0, scopeIdx);
            var suffixedName = $"{methodName}_{absMemberId}";
            var method = reportType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == suffixedName)
                ?? reportType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == methodName && !m.IsSpecialName)
                    .OrderByDescending(m => ScoreMethodMatch(m, args))
                    .FirstOrDefault();

            if (method == null)
                continue;

            var parameters = method.GetParameters();
            var convertedArgs = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < args.Length)
                    convertedArgs[i] = MockCodeunitHandle.ConvertArg(args[i], parameters[i].ParameterType);
            }

            return method.Invoke(report, convertedArgs);
        }

        return null;
    }

    private object? EnsureReportInstance()
    {
        if (_reportInstance != null)
            return _reportInstance;

        var reportType = MockRecordHandle.FindTypeAcrossAssemblies($"Report{ReportId}");
        if (reportType == null)
            return null;

        // The rewriter strips BC-infrastructure constructors and leaves a safe
        // default .ctor(), so Activator.CreateInstance runs field initializers
        // correctly (unlike GetUninitializedObject which skips them entirely).
        _reportInstance = Activator.CreateInstance(reportType);

        if (_tableView != null)
        {
            var recProp = reportType.GetProperty("Rec",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (recProp?.CanRead == true && recProp.GetValue(_reportInstance) is MockRecordHandle reportRec)
                reportRec.ALCopy(_tableView, true);
        }

        return _reportInstance;
    }

    private static int ScoreMethodMatch(MethodInfo method, object[] args)
        => MockCodeunitHandle.ScoreMethodMatch(method, args);

    /// <summary>
    /// Static Report.Run(reportId) — redirected from NavReport.Run() by the rewriter.
    /// If a ReportHandler is registered, invoke it; otherwise create an instance and Run().
    /// </summary>
    public static void StaticRun(int reportId)
    {
        var handle = new MockReportHandle(reportId);
        handle.Run();
    }

    /// <summary>
    /// Static Report.Run(reportId, requestPage) — 2-argument overload.
    /// BC emits this form for <c>Report.Run(id, true)</c> when only the request-page flag
    /// is supplied and <c>systemPrinter</c> is omitted.
    /// <paramref name="requestPage"/> is ignored in standalone mode.
    /// Fixes CS1501 'StaticRun' (2 args) — issue #1427.
    /// </summary>
    public static void StaticRun(int reportId, bool requestPage)
    {
        var handle = new MockReportHandle(reportId) { UseRequestForm = requestPage };
        handle.Run();
    }

    /// <summary>
    /// Static Report.Run(reportId, requestPage, systemPrinter) — 3-argument overload.
    /// BC emits this form when no record argument is supplied (e.g. <c>Report.Run(id, true, false)</c>).
    /// <paramref name="requestPage"/> and <paramref name="systemPrinter"/> are ignored in standalone mode.
    /// </summary>
    public static void StaticRun(int reportId, bool requestPage, bool systemPrinter)
    {
        var handle = new MockReportHandle(reportId) { UseRequestForm = requestPage };
        handle.Run();
    }

    /// <summary>
    /// Static Report.Run(reportId, requestPage, systemPrinter, record) — 4-argument overload.
    /// <paramref name="requestPage"/> and <paramref name="systemPrinter"/> are ignored in standalone mode.
    /// <paramref name="record"/> is applied as a table-view filter when it is a <see cref="MockRecordHandle"/>.
    /// </summary>
    public static void StaticRun(int reportId, bool requestPage, bool systemPrinter, object record)
    {
        var handle = new MockReportHandle(reportId) { UseRequestForm = requestPage };
        if (record is MockRecordHandle rec)
            handle.SetTableView(rec);
        handle.Run();
    }

    /// <summary>
    /// Static Report.Run(reportName, requestPage, systemPrinter) — Text-name 3-argument overload.
    /// BC emits this form when AL passes a report name string and no record argument.
    /// Report-name lookup is not supported in standalone mode; call is a no-op.
    /// </summary>
    public static void StaticRun(string reportName, bool requestPage, bool systemPrinter) { }

    /// <summary>
    /// Static Report.Run(reportName, requestPage, systemPrinter, record) — Text-name 4-argument overload.
    /// BC emits this form for <c>Report.Run(Report::"X", requestPage, systemPrinter, Rec)</c> when the
    /// first argument is resolved to a string name at compile time.
    /// Report-name lookup is not supported in standalone mode; call is a no-op.
    /// </summary>
    public static void StaticRun(string reportName, bool requestPage, bool systemPrinter, object record) { }

    /// <summary>
    /// Static Report.RunModal(reportId) — redirected from NavReport.RunModal() by the rewriter.
    /// If a ReportHandler is registered, invoke it; otherwise create an instance and RunModal().
    /// </summary>
    public static void StaticRunModal(int reportId)
    {
        var handle = new MockReportHandle(reportId);
        handle.RunModal();
    }

    /// <summary>
    /// Static Report.RunModal(reportId, requestWindow) — 2-argument overload.
    /// <paramref name="requestWindow"/> is ignored in standalone mode (no UI).
    /// </summary>
    public static void StaticRunModal(int reportId, bool requestWindow)
    {
        var handle = new MockReportHandle(reportId) { UseRequestForm = requestWindow };
        handle.RunModal();
    }

    /// <summary>
    /// Static Report.RunModal(reportId, requestWindow, systemPrinter) — 3-argument overload.
    /// <paramref name="requestWindow"/> and <paramref name="systemPrinter"/> are ignored in standalone mode.
    /// </summary>
    public static void StaticRunModal(int reportId, bool requestWindow, bool systemPrinter)
    {
        var handle = new MockReportHandle(reportId) { UseRequestForm = requestWindow };
        handle.RunModal();
    }

    /// <summary>
    /// Static Report.RunModal(reportId, requestWindow, systemPrinter, record) — 4-argument overload.
    /// <paramref name="requestWindow"/>, <paramref name="systemPrinter"/>, and <paramref name="record"/>
    /// are ignored in standalone mode (no rendering, no service-tier table filtering).
    /// </summary>
    public static void StaticRunModal(int reportId, bool requestWindow, bool systemPrinter, object record)
    {
        var handle = new MockReportHandle(reportId) { UseRequestForm = requestWindow };
        if (record is MockRecordHandle rec)
            handle.SetTableView(rec);
        handle.RunModal();
    }

    /// <summary>
    /// Static Report.RunModal(reportName, requestWindow, systemPrinter) — Text-name 3-argument overload.
    /// BC emits this form when AL passes a report name string and no record argument.
    /// Report-name lookup is not supported in standalone mode; call is a no-op.
    /// </summary>
    public static void StaticRunModal(string reportName, bool requestWindow, bool systemPrinter) { }

    /// <summary>
    /// Static Report.RunModal(reportName, requestWindow, systemPrinter, record) — Text-name 4-argument overload.
    /// BC emits this form for <c>Report.RunModal(Report::"X", requestWindow, systemPrinter, Rec)</c> when the
    /// first argument is resolved to a string name at compile time.
    /// Report-name lookup is not supported in standalone mode; call is a no-op.
    /// </summary>
    public static void StaticRunModal(string reportName, bool requestWindow, bool systemPrinter, object record) { }

    /// <summary>
    /// AL assignment: <c>Rep1 := Rep2</c>.
    /// The BC compiler emits <c>Rep1.ALAssign(Rep2)</c> for report variable assignment.
    /// Copies the report ID, table-view filter, and internal report instance reference
    /// so both variables point at the same execution state — matching AL semantics.
    /// </summary>
    public void ALAssign(MockReportHandle other)
    {
        // ReportId is init-only; share internal state by copying mutable fields.
        _reportInstance = other._reportInstance;
        _tableView = other._tableView;
        UseRequestForm = other.UseRequestForm;
        FormatRegion = other.FormatRegion;
        Language = other.Language;
    }

    /// <summary>
    /// AL's <c>Clear(rep)</c> — the rewriter emits <c>rep.Clear()</c>. Resets the
    /// report handle to its default (un-run) state. No-op in standalone mode.
    /// </summary>
    public void Clear() { }

    // ── Instance Print method ────────────────────────────────────────────────
    // BC emits rep.Print(requestPageXml) for Report.Print() on an instance variable.
    /// <summary>Instance <c>Rep.Print(requestPageXml)</c> — no-op in standalone mode.</summary>
    public void Print(string requestPageXml) { }

    // ── Instance Execute method ──────────────────────────────────────────────
    // BC emits rep.Execute(xmlText) for Report.Execute(XmlText) on an instance variable.
    /// <summary>Instance <c>Rep.Execute(xmlText)</c> — no-op in standalone mode (no rendering engine).</summary>
    public void Execute(string xmlText) { }

    // Report.Execute / Report.Print — no-ops in standalone mode
    public static void StaticExecute(int reportId) { }

    /// <summary>BC emits <c>MockReportHandle.StaticExecute(reportId, requestPage)</c> for <c>Report.Execute(N, requestPage)</c>.</summary>
    public static void StaticExecute(int reportId, string requestPage) { }

    /// <summary>
    /// BC emits <c>MockReportHandle.StaticExecute(reportId, requestPage, recordRef)</c> for
    /// <c>Report.Execute(N, RequestPageXml, RecordRef)</c> — Integer-id 3-argument overload.
    /// No-op in standalone mode (no rendering engine).
    /// </summary>
    public static void StaticExecute(int reportId, string requestPage, MockRecordRef recordRef) { }

    /// <summary>
    /// BC emits <c>MockReportHandle.StaticExecute(reportName, requestPage, recordRef)</c> for
    /// <c>Report.Execute(ReportName, RequestPageXml, RecordRef)</c> — Text-name 3-argument overload.
    /// Report-name lookup is not supported in standalone mode; call is a no-op.
    /// </summary>
    public static void StaticExecute(string reportName, string requestPage, MockRecordRef recordRef) { }

    public static void StaticPrint(int reportId) { }

    // Report.SaveAs* — AL allows these in a Boolean context (`if Report.SaveAs(...) then`).
    // All static overloads return bool (true = success no-op) to match BC semantics — issue #1526.
    // BC emits NavReport.SaveAs*(DataError, int, string) — first arg is a DataError status object
    public static bool StaticSaveAs(int reportId, string format, string path) => true;

    /// <summary>
    /// BC emits <c>MockReportHandle.StaticSaveAs(DataError, reportId, requestData, format, ByRef&lt;OutStream&gt;)</c>
    /// for <c>Report.SaveAs(ReportId, RequestData, Format, OutStream)</c>. Returns true (no-op) in standalone mode.
    /// </summary>
    public static bool StaticSaveAs(object err, int reportId, string requestData, object format, MockOutStream outStream) => true;

    /// <summary>
    /// BC emits <c>MockReportHandle.StaticSaveAs(DataError, reportId, requestData, format, ByRef&lt;OutStream&gt;, RecordRef)</c>
    /// for <c>Report.SaveAs(ReportId, RequestData, Format, OutStream, RecordRef)</c>. Returns true (no-op) in standalone mode.
    /// </summary>
    public static bool StaticSaveAs(object err, int reportId, string requestData, object format, MockOutStream outStream, MockRecordRef recordRef) => true;
    public static bool StaticSaveAsPdf(int reportId, string path) => true;
    public static bool StaticSaveAsPdf(object err, int reportId, string path) => true;
    public static bool StaticSaveAsWord(int reportId, string path) => true;
    public static bool StaticSaveAsWord(object err, int reportId, string path) => true;
    public static bool StaticSaveAsExcel(int reportId, string path) => true;
    public static bool StaticSaveAsExcel(object err, int reportId, string path) => true;
    public static bool StaticSaveAsHtml(int reportId, string path) => true;
    public static bool StaticSaveAsHtml(object err, int reportId, string path) => true;
    public static bool StaticSaveAsXml(int reportId, string path) => true;
    public static bool StaticSaveAsXml(object err, int reportId, string path) => true;

    // Report.DefaultLayout / layout enum methods — return 0 (default enum ordinal)
    public static int StaticDefaultLayout(int reportId) => 0;
    public static int StaticRdlcLayout(int reportId) => 0;
    public static int StaticWordLayout(int reportId) => 0;
    public static int StaticExcelLayout(int reportId) => 0;

    // Report.GetSubstituteReportId — no substitution in standalone mode
    public static int StaticGetSubstituteReportId(int reportId) => reportId;

    // Report.RunRequestPage — no request page UI in standalone mode
    public static string StaticRunRequestPage(int reportId) => string.Empty;

    /// <summary>
    /// BC emits <c>MockReportHandle.StaticRunRequestPage(reportId, requestParameters)</c>
    /// for <c>Report.RunRequestPage(ReportId, RequestPageParameters)</c>.
    /// Returns empty string in standalone mode (no UI available).
    /// </summary>
    public static string StaticRunRequestPage(int reportId, string requestParameters) => string.Empty;

    // Report.ValidateAndPrepareLayout / Report.WordXmlPart — no-ops
    public static void StaticValidateAndPrepareLayout(int reportId) { }

    /// <summary>
    /// BC emits <c>MockReportHandle.StaticValidateAndPrepareLayout(errorLevel, reportId, inStreamIn, ByRef&lt;inStreamOut&gt;, layoutType)</c>
    /// for <c>Report.ValidateAndPrepareLayout(N, InStrIn, InStrOut, LayoutType)</c>. No-op in standalone mode.
    /// </summary>
    public static void StaticValidateAndPrepareLayout(
        DataError errorLevel,
        int reportId,
        MockInStream inStreamIn,
        ByRef<MockInStream> inStreamOut,
        object layoutType) { }

    public static string StaticWordXmlPart(int reportId) => string.Empty;
}
