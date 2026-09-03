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
//   CONTROLS bound to report globals (#2442). `{Report}.RequestPage`'s generated
//   OnMetadataLoaded registers one source expression per control, whose getter and setter
//   read and write the REPORT's own global field — so a handler's SetValue lands on the
//   global the report body then reads, which is the only thing that makes a request-page
//   control worth setting. That registration used to be no-opped for every request page
//   (NavForm.SourceExpressions stayed an empty dictionary and GetField refused by name);
//   the request-page path now opts its own form into registration and nothing else, see
//   RunnerFormInit.MarkSourceExpressionsWanted.
//
// WHAT IS NOT ANSWERED HERE
//   A control the request page publishes no source expression for. GetField still refuses
//   by name rather than silently answering an empty value, which would let a handler "set"
//   an option the report never sees.
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
    private readonly object _requestPageForm;
    private readonly object _report;
    private readonly int _reportId;
    private readonly bool _offersOk;
    private readonly Dictionary<string, ITestFilter> _dataItemFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ITestField> _controlFields = new();
    private RunnerPageInstance? _pageInstance;
    private FormResult _formResult = FormResult.None;

    private readonly Guid _formHandle;

    private RequestPageTestPage(object requestPageForm, object report, int reportId, bool offersOk)
    {
        _requestPageForm = requestPageForm;
        _report = report;
        _reportId = reportId;
        _offersOk = offersOk;
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

    /// <param name="offersOk">
    /// Whether this request page has a plain OK built-in action at all — see
    /// <see cref="GetBuiltInAction"/>. True when the page was opened to capture PARAMETERS
    /// (<c>Report.RunRequestPage</c>), or when the report is ProcessingOnly; false for a
    /// plain <c>Report.Run()</c> on a report that renders.
    /// </param>
    internal static RequestPageTestPage Bind(object requestPageForm, object report, int reportId, bool offersOk)
    {
        var page = new RequestPageTestPage(requestPageForm, report, reportId, offersOk);
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

    /// <summary>
    /// The request page's built-in actions. Invoking one records how the page was closed.
    ///
    /// Returning null for OK is LOAD-BEARING and measured, not defensive. On a real service
    /// tier a request page opened by a plain <c>Report.Run()</c> on a report that is NOT
    /// ProcessingOnly has no OK action at all: OK selects no report output, and BC answers
    /// <c>NavTestActionNotFoundException</c> — "The built-in action = OK is not found on the
    /// page." — rather than running the report and then objecting. The same page DOES offer
    /// OK when it was opened to capture parameters (<c>Report.RunRequestPage</c>), because
    /// there OK means "these are the parameters", not "produce output".
    ///
    /// Both halves are pinned upstream in the al-language corpus
    /// (handlers/TestReportRunWithRequestPage.al and handlers/TestReportRunRequestPage.al),
    /// green on BC 27.0 through 28.4. Answering every result with an action made the
    /// difference invisible here.
    ///
    /// Null is how BC's NavTestPageBase.GetBuiltInAction is told an action is absent — it
    /// raises the exception itself, so the message is BC's own rather than one written here.
    /// </summary>
    public override ITestAction GetBuiltInAction(FormResult formResult)
    {
        if (!_offersOk && formResult is FormResult.OK or FormResult.LookupOK) return null!;
        return new RecordingBuiltInAction(this, formResult);
    }

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

    /// <summary>
    /// A control on the request page, addressed by the control id the AL compiler derived for
    /// it. Resolved through the request page's own <c>NavForm.SourceExpressions</c> — BC's
    /// control -> value binding table, whose getter/setter for a request-page control read and
    /// write the REPORT's global variable. That indirection is the whole point: a handler's
    /// <c>SetValue</c> has to land where the report body's <c>OnAfterGetRecord</c> reads it,
    /// and a value held aside here would make the handler look like it worked while the report
    /// went on seeing the old one.
    ///
    /// The binding table is BC's, not a map assembled here — <see cref="PageVariableTestField"/>
    /// drives the same expression object the TestPage path drives for a page-global control,
    /// so option captions, the declared NavType, and the control's OnValidate trigger all work
    /// the same way on a request page as on a page.
    /// </summary>
    public override ITestField GetField(int id)
    {
        if (_controlFields.TryGetValue(id, out var cached)) return cached;

        var page = PageInstance();
        var expression = page?.TryGetSourceExpression(id);
        if (expression == null)
            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestRequestPage control {id} (report {_reportId})",
                "request-page-control — the request page publishes no source expression for this "
                + "control, so there is no report global for the [RequestPageHandler] to read or "
                + "write. Registered controls: "
                + string.Join(", ", RegisteredControlKeys().DefaultIfEmpty("(none)"))
                + ". See docs/scope.md");

        var field = new PageVariableTestField(page!, expression, id);
        _controlFields[id] = field;
        return field;
    }

    /// <summary>
    /// The request page wrapped as a <see cref="RunnerPageInstance"/>. <c>Adopt</c>, never
    /// <c>TryCreate</c>: the form is already live — NavReportSync constructed it and BC's
    /// RunModal ran its metadata load — so re-initialising it would register every source
    /// expression a second time ("An item with the same key has already been added").
    /// </summary>
    private RunnerPageInstance? PageInstance()
    {
        if (_pageInstance != null) return _pageInstance;
        try { return _pageInstance = RunnerPageInstance.Adopt(_requestPageForm, _reportId); }
        catch (Exception ex)
        {
            // stdout on purpose: the test-execution child's stderr is not captured, so a
            // Console.Error line would be invisible exactly when it is needed.
            Console.Out.WriteLine(
                $"[RequestPageTestPage] report {_reportId}: could not adopt the request-page form "
                + $"({ex.GetType().Name}: {ex.Message}); its controls stay unresolvable");
            return null;
        }
    }

    /// <summary>The control ids the request page actually registered, for the refusal above.</summary>
    private IEnumerable<string> RegisteredControlKeys()
    {
        var table = ReadProperty(_requestPageForm, "SourceExpressions") as System.Collections.IDictionary;
        if (table == null) yield break;
        foreach (var key in table.Keys)
            if (key?.ToString() is { Length: > 0 } text) yield return text;
    }

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
