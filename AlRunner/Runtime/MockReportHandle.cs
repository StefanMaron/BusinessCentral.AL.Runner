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
        var report = EnsureReportInstance();
        if (report == null)
            return;

        var runMethod = report.GetType().GetMethod("Run",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        runMethod?.Invoke(report, null);
    }

    public string RunRequestPage()
    {
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

        // Skip the generated constructor (may reference BC runtime infrastructure).
        // InitializeComponent handles field wiring. Risk: field initializers outside
        // InitializeComponent will be null/default.
        _reportInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(reportType);
        var initMethod = reportType.GetMethod("InitializeComponent",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        initMethod?.Invoke(_reportInstance, null);

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
}
