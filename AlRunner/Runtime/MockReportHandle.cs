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

    /// <summary>BC emits <c>rep.SaveAsPdf(errorLevel, path)</c> for <c>Report.SaveAsPdf(FileName)</c>. No-op in standalone mode.</summary>
    public void SaveAsPdf(DataError errorLevel, string path) { }

    /// <summary>BC emits <c>rep.SaveAsExcel(errorLevel, path)</c> for <c>Report.SaveAsExcel(FileName)</c>. No-op in standalone mode.</summary>
    public void SaveAsExcel(DataError errorLevel, string path) { }

    /// <summary>BC emits <c>rep.SaveAsWord(errorLevel, path)</c> for <c>Report.SaveAsWord(FileName)</c>. No-op in standalone mode.</summary>
    public void SaveAsWord(DataError errorLevel, string path) { }

    /// <summary>BC emits <c>rep.SaveAsHtml(errorLevel, path)</c> for <c>Report.SaveAsHtml(FileName)</c>. No-op in standalone mode.</summary>
    public void SaveAsHtml(DataError errorLevel, string path) { }

    /// <summary>BC emits <c>rep.SaveAsXml(errorLevel, path)</c> for <c>Report.SaveAsXml(FileName)</c>. No-op in standalone mode.</summary>
    public void SaveAsXml(DataError errorLevel, string path) { }

    /// <summary>BC emits <c>rep.SaveAs(errorLevel, params, format, ByRef&lt;OutStream&gt;)</c> for <c>Report.SaveAs(Params, Format, OutStream)</c>. No-op in standalone mode.</summary>
    public void SaveAs(DataError errorLevel, string requestParams, NavReportFormat format, ByRef<MockOutStream> outStream) { }

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

        var assembly = MockCodeunitHandle.CurrentAssembly;
        if (assembly == null)
            return null;

        var reportType = assembly.GetTypes().FirstOrDefault(t => t.Name == $"Report{ReportId}");
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
    /// AL's <c>Clear(rep)</c> — the rewriter emits <c>rep.Clear()</c>. Resets the
    /// report handle to its default (un-run) state. No-op in standalone mode.
    /// </summary>
    public void Clear() { }

    // ── Instance Print method ────────────────────────────────────────────────
    // BC emits rep.Print(requestPageXml) for Report.Print() on an instance variable.
    /// <summary>Instance <c>Rep.Print(requestPageXml)</c> — no-op in standalone mode.</summary>
    public void Print(string requestPageXml) { }

    // Report.Execute / Report.Print — no-ops in standalone mode
    public static void StaticExecute(int reportId) { }

    /// <summary>BC emits <c>MockReportHandle.StaticExecute(reportId, requestPage)</c> for <c>Report.Execute(N, requestPage)</c>.</summary>
    public static void StaticExecute(int reportId, string requestPage) { }

    public static void StaticPrint(int reportId) { }

    // Report.SaveAs* — no-ops (no real file I/O in standalone mode)
    // BC emits NavReport.SaveAs*(DataError, int, string) — first arg is a DataError status object
    public static void StaticSaveAs(int reportId, string format, string path) { }
    public static void StaticSaveAsPdf(int reportId, string path) { }
    public static void StaticSaveAsPdf(object err, int reportId, string path) { }
    public static void StaticSaveAsWord(int reportId, string path) { }
    public static void StaticSaveAsWord(object err, int reportId, string path) { }
    public static void StaticSaveAsExcel(int reportId, string path) { }
    public static void StaticSaveAsExcel(object err, int reportId, string path) { }
    public static void StaticSaveAsHtml(int reportId, string path) { }
    public static void StaticSaveAsHtml(object err, int reportId, string path) { }
    public static void StaticSaveAsXml(int reportId, string path) { }
    public static void StaticSaveAsXml(object err, int reportId, string path) { }

    // Report.DefaultLayout / layout enum methods — return 0 (default enum ordinal)
    public static int StaticDefaultLayout(int reportId) => 0;
    public static int StaticRdlcLayout(int reportId) => 0;
    public static int StaticWordLayout(int reportId) => 0;
    public static int StaticExcelLayout(int reportId) => 0;

    // Report.GetSubstituteReportId — no substitution in standalone mode
    public static int StaticGetSubstituteReportId(int reportId) => reportId;

    // Report.RunRequestPage — no request page UI in standalone mode
    public static string StaticRunRequestPage(int reportId) => string.Empty;

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
