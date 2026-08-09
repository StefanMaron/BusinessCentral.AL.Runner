// RecordPatches.AlReportParser — parses AL `report` / `reportextension` declarations into
// ParsedReport records keyed by report ID, including the data-item tree the Report Data Items
// virtual table exposes.
//
// Parsed from BC's own AL syntax tree (#1696). Two things the old regex implementation needed
// and this does not:
//   * A tightened header pattern. `[^{]*?\{` used to match ordinary PROSE — a doc comment
//     reading "against report 1306 …" and a Label reading "… to preview report 1306 against."
//     each registered a report 1306, named "Invoice" and "against", which then SHADOWED the
//     real Base Application report 1306 everywhere _parsedReports is read (AllObj, the Report
//     Metadata virtual table, ProcessingOnly), because a source-parsed entry always beats a
//     dependency's. A report is now a node; prose is not.
//   * Balanced-brace body extraction to keep a nested column's Caption from masquerading as
//     the report's. That brace counting was NOT string-literal aware, so a caption containing
//     a literal `{` desynchronised the depth counter for everything after it in the file.
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static void ParseAllReportSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParseReportFile(File.ReadAllText(file));
        }
    }


    private static void TryParseReportFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            switch (obj)
            {
                case NavSyntax.ReportSyntax r when ObjectIdOf(r) is int id:
                    var props = r.PropertyList;
                    _parsedReports[id] = new ParsedReport(id, IdentText(r.Name), IsExtension: false,
                        // AL's default for ProcessingOnly is false. Reading it off the report's
                        // own property list also removes an old asymmetry: the regex scanned the
                        // whole body, unlike every other property here, which was depth-scoped.
                        ProcessingOnly: PropIs(props, "ProcessingOnly", "true"))
                    {
                        Caption = PropertyTextFrom(PropValue(props, "Caption")),
                        // AL's own default. A report that declares no `requestpage` block still
                        // gets BC's standard one, so absence of the property means true — and,
                        // as before, so does any value that is not literally `false`.
                        UseRequestPage = !string.Equals(
                            PropertyTextFrom(PropValue(props, "UseRequestPage")), "false",
                            StringComparison.OrdinalIgnoreCase),
                        DataItems = ParseReportDataItems(r.DataSet),
                    };
                    break;

                // reportextension has its OWN id namespace, separate from `report`.
                // Keep them in a dedicated dict so an extension declared with the
                // same numeric id as a plain report (legal in AL) does not clobber
                // the base report's ProcessingOnly flag. Regression: bucket-2
                // suite 135-addfirst-dataset declared `reportextension 50001`
                // which silently turned suite 100-report-preview's report 50001
                // (ProcessingOnly=true) into a layout-bound report → OOS at run.
                case NavSyntax.ReportExtensionSyntax rx when ObjectIdOf(rx) is int extId:
                    _parsedReportExtensions[extId] = new ParsedReport(
                        extId, IdentText(rx.Name), IsExtension: true, ProcessingOnly: false);
                    break;
            }
        }
    }

    /// <summary>
    /// The report's data-item tree, flattened in declaration order with each entry's
    /// nesting depth — the exact shape the Report Data Items virtual table exposes
    /// (Name / Related Table / Indentation Level). Indentation is derived from real
    /// brace nesting, so a nested data item is never reported as another root.
    /// </summary>
    private static List<ParsedReportDataItem> ParseReportDataItems(
        NavSyntax.ReportDataSetSectionSyntax? dataSet)
    {
        var result = new List<ParsedReportDataItem>();
        if (dataSet == null) return result;
        foreach (var di in dataSet.DataItems) AddDataItem(di, indentation: 0, result);
        return result;
    }

    /// <summary>
    /// Appends one data item then descends into its children, so the list comes out in
    /// declaration order with each entry's real nesting depth. A data item's child data items
    /// are interleaved with its own <c>column(...)</c> entries in a single <c>Elements</c>
    /// list, so the children are selected by node type.
    /// </summary>
    private static void AddDataItem(
        NavSyntax.ReportDataItemSyntax di, int indentation, List<ParsedReportDataItem> result)
    {
        var props = di.PropertyList;
        result.Add(new ParsedReportDataItem(
            Ordinal: result.Count + 1,
            Name: IdentText(di.Name),
            // A namespaced reference collapses to its last segment.
            RelatedTable: LastNameSegment(di.DataItemTable?.ToString()),
            Indentation: indentation,
            DataItemTableView: PropertyTextFrom(PropValue(props, "DataItemTableView")),
            RequestFilterFields: PropertyTextFrom(PropValue(props, "RequestFilterFields"))));

        foreach (var child in di.Elements.OfType<NavSyntax.ReportDataItemSyntax>())
            AddDataItem(child, indentation + 1, result);
    }

    /// <summary>
    /// AL-source-derived ProcessingOnly for the given report ID. Returns
    /// <c>false</c> when the report is unknown — matches the AL default and
    /// causes the runner to throw an out-of-scope error at Run time (no
    /// rendering pipeline available without a service tier).
    /// </summary>
    public static bool IsReportProcessingOnly(int reportId) =>
        _parsedReports.TryGetValue(reportId, out var p) && p.ProcessingOnly;
}

internal record ParsedReport(int Id, string Name, bool IsExtension, bool ProcessingOnly = false)
{
    /// <summary>Report-level Caption property; null when the report declares none.</summary>
    public string? Caption { get; init; }

    /// <summary>AL's UseRequestPage, defaulted to its AL default (true) when undeclared.</summary>
    public bool UseRequestPage { get; init; } = true;

    /// <summary>Data-item tree, flattened in declaration order. Empty for processing-only reports.</summary>
    public List<ParsedReportDataItem> DataItems { get; init; } = new();
}

/// <summary>
/// One entry of a report's data-item tree, as the Report Data Items virtual table
/// (2000000203) exposes it. <paramref name="Ordinal"/> is 1-based declaration order.
/// </summary>
internal record ParsedReportDataItem(
    int Ordinal, string Name, string RelatedTable, int Indentation,
    string? DataItemTableView, string? RequestFilterFields);
