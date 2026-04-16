using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

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
    /// Executes the report lifecycle: OnPreReport → (data iteration) → OnPostReport.
    /// The BC service tier normally orchestrates this; we replicate it here since
    /// the base class Run() override is stripped by the rewriter.
    /// </summary>
    private static void ExecuteReportLifecycle(object report)
    {
        var reportType = report.GetType();

        InvokeTrigger(report, reportType, "OnPreReport");
        // Data-item iteration is not yet implemented (architectural gap).
        // OnPreReport and OnPostReport are called unconditionally.
        InvokeTrigger(report, reportType, "OnPostReport");
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

    // Report.Execute / Report.Print — no-ops in standalone mode
    public static void StaticExecute(int reportId) { }
    public static void StaticPrint(int reportId) { }

    // Report.SaveAs* — no-ops (no real file I/O in standalone mode)
    public static void StaticSaveAs(int reportId, string format, string path) { }
    public static void StaticSaveAsPdf(int reportId, string path) { }
    public static void StaticSaveAsPdf(int reportId, string path, object recordRef) { }
    public static void StaticSaveAsWord(int reportId, string path) { }
    public static void StaticSaveAsWord(int reportId, string path, object recordRef) { }
    public static void StaticSaveAsExcel(int reportId, string path) { }
    public static void StaticSaveAsExcel(int reportId, string path, object recordRef) { }
    public static void StaticSaveAsHtml(int reportId, string path) { }
    public static void StaticSaveAsHtml(int reportId, string path, object recordRef) { }
    public static void StaticSaveAsXml(int reportId, string path) { }
    public static void StaticSaveAsXml(int reportId, string path, object recordRef) { }

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
    public static string StaticWordXmlPart(int reportId) => string.Empty;
}
