// RequestPageTestPage — the ITestPage BC hands to a test's [RequestPageHandler].
//
// WHY A SEPARATE SHAPE FROM LiveNavTestPage
//   NavTestExecution.TestHandleModalForm builds a NavTestRequestPage (rather than a plain
//   NavTestPage) whenever form.IsRequestPage, and a request page is not a page over a
//   record: it has no source table at all. LiveNavTestPage is built around one — it refuses
//   by name when SourceTable is null — so a request page reaching it failed with
//   "the modal form has no source table bound", which is true and beside the point.
//
//   What a request page actually offers a handler is:
//     * one filter group per report DATA ITEM (`Rep.Header.SetFilter("No.", …)`), which is
//       what NavTestPageBase.GetDataItem → ITestPage.GetDataItemFilter resolves, and
//     * the built-in OK / Cancel actions that close it.
//
//   Both are answered here directly against the report the request page belongs to: a
//   filter set on a data item is set on that data item's own NavRecord, which is exactly
//   where NavReport.GetReportParameters reads it back from
//   (`dataItem.Record.RecordImplementation.GetView(...)`). So a handler's filter survives
//   into the parameters XML the AL under test receives, which is the entire purpose of
//   Report.RunRequestPage.
//
// WHAT IS NOT ANSWERED HERE
//   Request-page CONTROLS bound to report globals. Resolving one needs the request page's
//   NavForm.SourceExpressions table, which the runner only populates for forms it opted
//   into real initialisation (RunnerFormInit) — request pages are deliberately not among
//   them today. GetField therefore refuses by name rather than silently answering an empty
//   value, which would let a handler "set" an option the report never sees.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;

namespace AlRunner.Patches;

internal sealed class RequestPageTestPage : MockITestPage
{
    private readonly object _report;
    private readonly int _reportId;
    private readonly Dictionary<string, ITestFilter> _dataItemFilters = new(StringComparer.OrdinalIgnoreCase);
    private FormResult _formResult = FormResult.None;

    private readonly Guid _formHandle;

    private RequestPageTestPage(object requestPageForm, object report, int reportId)
    {
        _report = report;
        _reportId = reportId;
        _formHandle = ReadProperty(requestPageForm, "Handle") is Guid handle ? handle : Guid.Empty;
    }

    /// <summary>
    /// The handle of the request-page form BC registered. LOAD-BEARING: NavTestPageBase
    /// resolves its <c>ServerForm</c> as <c>Company.GetRegisteredForm(TestPage.FormHandle)</c>,
    /// so answering Guid.Empty (the mock's value) makes that lookup fail with "a page with the
    /// specified handle has not been registered" — blaming registration for a handle that was
    /// never supplied.
    /// </summary>
    public override Guid FormHandle => _formHandle;

    // ── form → page binding ───────────────────────────────────────────────────
    // The client session is handed only a form handle, so it cannot tell which report a
    // request page belongs to. The caller that runs the request page (NavReportSync) knows
    // both, and also needs the SAME instance back afterwards to read how the handler closed
    // it — hence one table keyed by the form, weak so a finished report is collectable.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RequestPageTestPage> _byForm = new();

    internal static RequestPageTestPage Bind(object requestPageForm, object report, int reportId)
    {
        var page = new RequestPageTestPage(requestPageForm, report, reportId);
        _byForm.Remove(requestPageForm);
        _byForm.Add(requestPageForm, page);
        return page;
    }

    internal static RequestPageTestPage? TryGetFor(object requestPageForm)
        => _byForm.TryGetValue(requestPageForm, out var page) ? page : null;

    public override int PageId => _reportId;

    /// <summary>How the handler closed the page. <c>None</c> until it invokes OK or Cancel —
    /// a handler that returns without closing has cancelled, which is what BC reports too.</summary>
    public override FormResult FormResult => _formResult;

    /// <summary>True once the handler confirmed with OK (or LookupOK).</summary>
    internal bool Confirmed => _formResult is FormResult.OK or FormResult.LookupOK;

    public override ITestAction GetBuiltInAction(FormResult formResult)
        => new RecordingBuiltInAction(this, formResult);

    /// <summary>
    /// The filter group for one of the report's data items, addressed by the data item's AL
    /// variable name — the name the handler writes (<c>Rep.Header.SetFilter(…)</c>).
    /// </summary>
    public override ITestFilter GetDataItemFilter(string id)
    {
        if (_dataItemFilters.TryGetValue(id, out var cached)) return cached;

        var record = FindDataItemRecord(id)
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestRequestPage data item '{id}' (report {_reportId})",
                "request-page-dataitem — the report has no data item by that name, so there is "
                + "no filter group for the [RequestPageHandler] to set. Known data items: "
                + string.Join(", ", DataItemNames().DefaultIfEmpty("(none)")) + ". See docs/scope.md");

        var filter = new DataItemTestFilter(record);
        _dataItemFilters[id] = filter;
        return filter;
    }

    public override ITestField GetField(int id)
        => throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestRequestPage control {id} (report {_reportId})",
            "request-page-control — request-page controls bound to report globals are not yet "
            + "resolvable: they need the request page's NavForm.SourceExpressions table, which "
            + "the runner only builds for forms it drives as a TestPage. Data-item filters and "
            + "the built-in OK/Cancel actions are supported. See docs/scope.md");

    // ── report data items ─────────────────────────────────────────────────────

    private IEnumerable<object> DataItems()
    {
        var prop = FindDataItemsProperty(_report.GetType());
        if (prop?.GetValue(_report) is not System.Collections.IEnumerable items) yield break;
        foreach (var item in items)
            if (item != null) yield return item;
    }

    private IEnumerable<string> DataItemNames()
        => DataItems().Select(NameOf).Where(n => n.Length > 0);

    private NavRecord? FindDataItemRecord(string name)
    {
        // The data item's own AL variable name, which is what a handler writes.
        foreach (var item in DataItems())
            if (string.Equals(NameOf(item), name, StringComparison.OrdinalIgnoreCase))
                return ReadProperty(item, "Record") as NavRecord;

        // …but that is not always the name that arrives. The AL compiler resolves
        // `Rep.Header` to the request page's auto-generated table-view CONTROL for that data
        // item, named `Report<reportId>DataItem<index>TableView` — so what BC passes to
        // GetDataItemFilter can be a positional control name rather than the AL name. Kept
        // as an ADDITIONAL rule after the name match, never instead of it.
        var index = TableViewControlIndex(name);
        if (index >= 0)
        {
            var items = DataItems().ToList();
            if (index < items.Count)
                return ReadProperty(items[index], "Record") as NavRecord;
        }
        return null;
    }

    /// <summary>
    /// The data-item index encoded in a `Report&lt;id&gt;DataItem&lt;n&gt;TableView` control
    /// name, or -1 when the name is not of that shape.
    /// </summary>
    private static int TableViewControlIndex(string name)
    {
        const string marker = "DataItem";
        const string suffix = "TableView";
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return -1;
        var start = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return -1;
        start += marker.Length;
        var digits = name.Substring(start, name.Length - suffix.Length - start);
        return int.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var index) ? index : -1;
    }

    private static string NameOf(object dataItem)
    {
        var meta = ReadProperty(dataItem, "MetaData");
        return (meta == null ? null : ReadProperty(meta, "DataItemVarName") as string) ?? string.Empty;
    }

    private static PropertyInfo? FindDataItemsProperty(Type? t)
    {
        for (; t != null; t = t.BaseType)
        {
            var p = t.GetProperty("DataItems",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    private static object? ReadProperty(object target, string name)
        => target.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(target);

    // ── the pieces ────────────────────────────────────────────────────────────

    /// <summary>
    /// A data item's filter group. Writes straight through to the data item's own NavRecord,
    /// because that record is what NavReport.GetReportParameters serialises — anything held
    /// aside here would be a filter the report never sees.
    /// </summary>
    private sealed class DataItemTestFilter : ITestFilter
    {
        private readonly NavRecord _record;
        private int[] _currentKeyFields = Array.Empty<int>();
        private bool _ascending = true;

        internal DataItemTestFilter(NavRecord record) => _record = record;

        public void SetFilter(int fieldId, string filterValue) => _record.ALSetFilter(fieldId, filterValue);
        public string GetFilter(int fieldId) => _record.ALGetFilter(fieldId)?.ToString() ?? string.Empty;
        public IEnumerable<NavFilter> GetFilter() => Array.Empty<NavFilter>();
        public void SetCurrentKeyFields(int[] fields) => _currentKeyFields = fields ?? Array.Empty<int>();
        public int[] GetCurrentKeyFields() => _currentKeyFields;
        public bool Ascending { get => _ascending; set => _ascending = value; }
        public string CurrentKey => string.Join(", ", _currentKeyFields);
    }

    private sealed class RecordingBuiltInAction : ITestAction
    {
        private readonly RequestPageTestPage _page;
        private readonly FormResult _result;

        internal RecordingBuiltInAction(RequestPageTestPage page, FormResult result)
        {
            _page = page;
            _result = result;
        }

        public void Invoke() => _page._formResult = _result;
        public bool Visible => true;
        public bool Enabled => true;
    }
}
