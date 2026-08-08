// RecordPatches.AlReportParser — parses AL `report` / `reportextension`
// declarations into ParsedReport records keyed by report ID. Mirror of the
// page parser; same minimal shape (id + name).
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Nothing but whitespace may sit between a report's name and its opening brace, so the
    // pattern demands exactly that. The older `[^{]*?\{` tail matched ordinary PROSE:
    // `/// against report 1306 "Standard Sales - Invoice": the returned XML's ...` and
    // `Label 'No posted Sales Invoice Header to preview report 1306 against.'` both
    // registered a report 1306 — named "Invoice" and "against" — which then SHADOWED the
    // real Base Application report 1306 everywhere _parsedReports is read (AllObj, the
    // Report Metadata virtual table, ProcessingOnly). A source-parsed entry always wins
    // over a dependency's, so the fabricated one silently replaced the real metadata.
    private static readonly Regex RxReport = new(
        @"\breport\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxReportExtension = new(
        @"\breportextension\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s+extends\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void ParseAllReportSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParseReportFile(File.ReadAllText(file));
        }
    }

    // Property-level scanner: finds `ProcessingOnly = true|false` inside a
    // report body. Scoped within the object body via balanced-brace extraction
    // so we don't pick up a `ProcessingOnly` declaration from a neighbouring
    // object in the same .al file.
    private static readonly Regex RxProcessingOnly = new(
        @"\bProcessingOnly\s*=\s*(true|false)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void TryParseReportFile(string text)
    {
        text = AlCommentBlanker.Blank(text); // see AlPageParser — same reason (#1690/#1697)

        foreach (Match m in RxReport.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            var body = ExtractObjectBody(text, m.Index + m.Length - 1);
            bool processingOnly = TryReadProcessingOnly(body);
            _parsedReports[id] = new ParsedReport(id, name, IsExtension: false, ProcessingOnly: processingOnly)
            {
                Caption = ReadTopLevelProperty(body, "Caption"),
                // AL's own default. A report that declares no `requestpage` block still
                // gets BC's standard one, so absence of the property means true.
                UseRequestPage = !string.Equals(ReadTopLevelProperty(body, "UseRequestPage"), "false",
                    StringComparison.OrdinalIgnoreCase),
                DataItems = ParseReportDataItems(body),
            };
        }

        foreach (Match m in RxReportExtension.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            // reportextension has its OWN id namespace, separate from `report`.
            // Keep them in a dedicated dict so an extension declared with the
            // same numeric id as a plain report (legal in AL) does not clobber
            // the base report's ProcessingOnly flag. Regression: bucket-2
            // suite 135-addfirst-dataset declared `reportextension 50001`
            // which silently turned suite 100-report-preview's report 50001
            // (ProcessingOnly=true) into a layout-bound report → OOS at run.
            _parsedReportExtensions[id] = new ParsedReport(id, name, IsExtension: true, ProcessingOnly: false);
        }
    }

    // Returns the substring spanning the balanced-brace object body starting at
    // the position of the opening '{'. Falls back to the full remainder if no
    // closing brace is found (defensive).
    private static string ExtractObjectBody(string text, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= text.Length || text[openBraceIndex] != '{')
            return string.Empty;
        int depth = 0;
        for (int i = openBraceIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(openBraceIndex, i - openBraceIndex + 1);
            }
        }
        return text.Substring(openBraceIndex);
    }

    // AL default for ProcessingOnly on `report` is false. Returns true only if
    // the body declares `ProcessingOnly = true;` (case-insensitive).
    private static bool TryReadProcessingOnly(string body)
    {
        var m = RxProcessingOnly.Match(body);
        if (!m.Success) return false;
        return string.Equals(m.Groups[1].Value, "true", System.StringComparison.OrdinalIgnoreCase);
    }

    // Report-level property assignment, e.g. `Caption = 'Sales - Invoice';` or
    // `ProcessingOnly = true;`. Matched only at the object body's OWN depth (see
    // ReadTopLevelProperty) so a `Caption` on a nested column/dataitem/layout never
    // masquerades as the report's own.
    private static readonly Regex RxTopLevelProperty = new(
        @"^\s*(\w+)\s*=\s*(?:'((?:[^']|'')*)'|([^;]*?))\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Value of a property declared directly in <paramref name="body"/> (an object body
    /// including its outer braces), ignoring every nested block. Returns null when the
    /// property is not declared at that level — callers apply the AL default themselves
    /// rather than having one silently invented here.
    /// </summary>
    private static string? ReadTopLevelProperty(string body, string propertyName)
    {
        foreach (Match m in RxTopLevelProperty.Matches(body))
        {
            if (!string.Equals(m.Groups[1].Value, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (DepthAt(body, m.Index) != 1) continue;   // 1 == directly inside the object's own braces
            // Quoted form is an AL text literal: '' is an escaped apostrophe.
            return m.Groups[2].Success ? m.Groups[2].Value.Replace("''", "'") : m.Groups[3].Value.Trim();
        }
        return null;
    }

    /// <summary>Brace nesting depth at <paramref name="index"/> within <paramref name="body"/>.</summary>
    private static int DepthAt(string body, int index)
    {
        int depth = 0;
        for (int i = 0; i < index && i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}') depth--;
        }
        return depth;
    }

    // `dataitem(Header; "Sales Invoice Header")` — name and bound table, each either
    // quoted or a bare identifier. A namespaced table reference (Microsoft.Sales.X)
    // keeps its dots; ResolveTableIdByName matches on the plain name, so the trailing
    // segment is what we hand it.
    private static readonly Regex RxDataItem = new(
        @"\bdataitem\s*\(\s*(?:""([^""]+)""|([A-Za-z_]\w*))\s*;\s*(?:""([^""]+)""|([A-Za-z_][\w.]*))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The report's data-item tree, flattened in declaration order with each entry's
    /// nesting depth — the exact shape the Report Data Items virtual table exposes
    /// (Name / Related Table / Indentation Level). Indentation is derived from real
    /// brace nesting, so a nested data item is never reported as another root.
    /// </summary>
    private static List<ParsedReportDataItem> ParseReportDataItems(string body)
    {
        var result = new List<ParsedReportDataItem>();
        // Spans of the data-item bodies already opened, used to compute nesting.
        var openSpans = new List<(int Start, int End)>();

        foreach (Match m in RxDataItem.Matches(body))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var table = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            // A namespaced reference (`Microsoft.Sales.History."Sales Invoice Header"`
            // collapses to the last dotted segment when unquoted).
            int dot = table.LastIndexOf('.');
            if (dot >= 0 && dot < table.Length - 1) table = table.Substring(dot + 1);

            int braceIndex = body.IndexOf('{', m.Index + m.Length);
            if (braceIndex < 0) continue;
            var span = ExtractObjectBody(body, braceIndex);
            var itemBody = span;
            int end = braceIndex + span.Length;

            int indentation = openSpans.Count(s => m.Index > s.Start && m.Index < s.End);
            openSpans.Add((braceIndex, end));

            result.Add(new ParsedReportDataItem(
                Ordinal: result.Count + 1,
                Name: name,
                RelatedTable: table,
                Indentation: indentation,
                DataItemTableView: ReadTopLevelProperty(itemBody, "DataItemTableView"),
                RequestFilterFields: ReadTopLevelProperty(itemBody, "RequestFilterFields")));
        }

        return result;
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
