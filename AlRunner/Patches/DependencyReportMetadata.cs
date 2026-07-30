// DependencyReportMetadata — runtime metadata XML for reports that live in a PRECOMPILED
// dependency .app, which the runner never source-compiles.
//
// THE GAP
//   NCLMetaReport.LoadMetadata() → GetMetadataFromLoader() →
//   INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata(objectId) is BC's only route to a
//   report's dataset shape. RunnerXmlMetadataLoader answers it from
//   AlReportMetadataRegistry, which the EMIT pipeline fills — so it holds every report the
//   runner compiled and none that it didn't. Any AL that reaches for a Base Application /
//   ISV report's metadata (Report.WordXmlPart, request-page/filter discovery, dataset
//   introspection) therefore hit "not-yet-implemented" and the whole test died there.
//
//   Real BC does not have this problem because the service tier stores each object's
//   compiled metadata at PUBLISH time. The runner publishes nothing, and an R2R .app ships
//   no compiled metadata form of its objects.
//
// WHAT IS RECONSTRUCTED, AND FROM WHAT
//   Two sources, both shipped inside the .app itself — nothing here is inferred from
//   behaviour or defaulted to something convenient:
//
//   1. SymbolReference.json — the compiler's own statement of the report: its Id, Name,
//      Caption/ProcessingOnly/UseRequestPage properties, the full DataItems tree (each with
//      RelatedTable and Indentation) and each data item's Columns (compiler-assigned Id,
//      Name, resolved type). This is the same file the Report Metadata (2000000139) and
//      AllObj virtual tables already read, so the shape is authoritative.
//
//   2. The report's own AL source, read back out of the .app's embedded src/ tree. The
//      symbol file states every column's NAME and TYPE but NOT its source expression —
//      `column(No; Header."No.")` records `No`/`Code`, never `Header."No."`. The expression
//      is what a dataset actually evaluates, so inventing one would produce a report that
//      renders confident wrong values. SymbolReference states the report's own
//      ReferenceSourceFileName, so exactly one file is read to recover it.
//
//   A column whose expression cannot be recovered is emitted WITHOUT a SourceExpr rather
//   than with a guessed one: BC then has the column's declared shape and no false claim
//   about where its value comes from.
//
// FAITHFULNESS
//   The emitted XML follows the shape BC's own Compilation.Emit produces for a
//   source-compiled report (captured in AlReportMetadataRegistry — compare a sample there
//   against EmitReportXml below). Elements the two sources do not state are omitted, never
//   filled with a plausible default. A report the symbol files do not describe at all falls
//   through to the caller's existing out-of-scope throw — it is not answered with an empty
//   document, which would read to BC as "this report has no dataset".
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly ConcurrentDictionary<int, string?> _depReportMetadataXml = new();

    /// <summary>
    /// Runtime metadata XML for a report declared by a precompiled dependency, or null when
    /// no dependency .app describes that report. Result cached per id (including the null),
    /// since the answer is a property of the loaded dependency set.
    /// </summary>
    internal static string? TryBuildDependencyReportMetadata(int reportId)
        => _depReportMetadataXml.GetOrAdd(reportId, BuildDependencyReportMetadata);

    private static string? BuildDependencyReportMetadata(int reportId)
    {
        var found = FindDependencyReportSymbol(reportId);
        if (found == null) return null;
        var (appPath, report) = found.Value;

        var sourceExprByColumn = TryReadColumnSourceExpressions(appPath, report);
        var xml = EmitReportXml(report, sourceExprByColumn);

        Console.Error.WriteLine(
            $"[RecordPatches] dependency report metadata: synthesized Report {reportId} "
            + $"\"{report.Name}\" from {Path.GetFileName(appPath)} "
            + $"({report.DataItems.Count} data item(s), "
            + $"{report.DataItems.Sum(d => d.Columns?.Count ?? 0)} column(s), "
            + $"{sourceExprByColumn?.Count ?? 0} source expression(s) recovered)");
        return xml;
    }

    private static (string AppPath, BcAppSymbolCache.ReportSymbol Report)? FindDependencyReportSymbol(int reportId)
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.ReportSymbol> reports;
            try
            {
                reports = BcAppSymbolCache.Get(appPath).Reports;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] dependency report metadata: SymbolReference read failed for "
                    + $"{Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var r in reports)
                if (r.Id == reportId)
                    return (appPath, r);
        }
        return null;
    }

    // ── column source expressions ────────────────────────────────────────────

    // `column(Name; Expression)` — the name is a plain or quoted AL identifier, the
    // expression is everything up to the closing paren of the column header. An expression
    // may itself contain parentheses (`Format(Amount)`) and quoted names containing
    // parens (`Header."Amount (LCY)"`), so the paren scan below is what actually finds the
    // end; this pattern only anchors the start.
    private static readonly Regex RxReportColumnHeader = new(
        @"\bcolumn\s*\(\s*(?:""([^""]+)""|([A-Za-z_]\w*))\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Map of column name → AL source expression, read from the report's own source file
    /// inside the .app. Null when the app ships no source for the report (symbols-only
    /// package) — callers then emit columns without a SourceExpr rather than guessing one.
    /// </summary>
    private static Dictionary<string, string>? TryReadColumnSourceExpressions(
        string appPath, BcAppSymbolCache.ReportSymbol report)
    {
        if (string.IsNullOrEmpty(report.ReferenceSourceFileName)) return null;
        var source = BcAppSymbolCache.TryReadSourceFile(appPath, report.ReferenceSourceFileName!);
        if (string.IsNullOrEmpty(source)) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RxReportColumnHeader.Matches(source!))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var expr = ReadColumnExpression(source!, m.Index + m.Length);
            // First declaration wins: a report and a reportextension in one file may both
            // declare a column of the same name, and the report's own is the one whose id
            // the symbol file carries.
            if (expr != null && !result.ContainsKey(name)) result[name] = expr;
        }
        return result;
    }

    /// <summary>
    /// The expression between a column's `;` and its matching `)`. Tracks quote state so a
    /// paren inside a quoted AL identifier (`Header."Amount (LCY)"`) does not end the scan,
    /// and nesting depth so a call's own parens (`Format(Amount)`) do not either.
    /// </summary>
    private static string? ReadColumnExpression(string source, int start)
    {
        int depth = 0;
        bool inQuotes = false, inString = false;
        for (int i = start; i < source.Length; i++)
        {
            char c = source[i];
            if (inQuotes) { if (c == '"') inQuotes = false; continue; }
            if (inString) { if (c == '\'') inString = false; continue; }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case '\'': inString = true; break;
                case '(': depth++; break;
                case ')':
                    if (depth == 0)
                    {
                        var expr = source.Substring(start, i - start).Trim();
                        return expr.Length > 0 ? expr : null;
                    }
                    depth--;
                    break;
                case '\n':
                    // A column header is a single line; an unbalanced scan means the source
                    // is not shaped the way this reader assumes, so recover nothing rather
                    // than return a truncated expression.
                    if (depth == 0) return null;
                    break;
            }
        }
        return null;
    }

    // ── XML emission ─────────────────────────────────────────────────────────

    /// <summary>
    /// Emit the <c>&lt;Report&gt;</c> runtime metadata document, matching the shape BC's own
    /// emit produces for a source-compiled report. Data items are flat siblings carrying
    /// their nesting as <c>DataItemIndent</c> — the same encoding the emitted XML uses.
    /// </summary>
    private static string EmitReportXml(
        BcAppSymbolCache.ReportSymbol report, Dictionary<string, string>? sourceExprByColumn)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("Report");
            w.WriteAttributeString("Extensible", "0");

            w.WriteElementString("ProcessingOnly", report.ProcessingOnly ? "1" : "0");
            w.WriteElementString("UseRequestPage", report.UseRequestPage ? "1" : "0");
            if (!string.IsNullOrEmpty(report.Caption))
            {
                w.WriteStartElement("CaptionML");
                w.WriteStartElement("Caption");
                w.WriteAttributeString("Id", "1033");
                w.WriteString(report.Caption);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            if (!string.IsNullOrEmpty(report.WordMergeDataItem))
                w.WriteElementString("WordMergeDataItem", report.WordMergeDataItem);
            w.WriteElementString("MetadataVersion", "130000");
            w.WriteElementString("ID", report.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteElementString("Name", report.Name);

            foreach (var di in report.DataItems)
                WriteDataItem(w, di, sourceExprByColumn);

            w.WriteEndElement();
        }
        return sb.ToString();
    }

    private static void WriteDataItem(
        XmlWriter w, BcAppSymbolCache.ReportDataItemSymbol di, Dictionary<string, string>? sourceExprByColumn)
    {
        int tableId = ResolveTableIdByName(di.RelatedTable);

        w.WriteStartElement("DataItem");
        // A data item whose table the runner cannot resolve still belongs in the document —
        // its name and nesting are what shape discovery reads. Emitting a fabricated table
        // id instead would point the data item at the wrong table entirely, so an
        // unresolved one is simply left unstated.
        if (tableId > 0)
            w.WriteElementString("DataItemTable", tableId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteElementString("DataItemIndent", di.Indentation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (di.Id != 0)
            w.WriteElementString("ID", di.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteElementString("DataItemVarName", di.Name);
        if (!string.IsNullOrEmpty(di.DataItemTableView))
            w.WriteElementString("DataItemTableView", di.DataItemTableView);
        if (!string.IsNullOrEmpty(di.RequestFilterFields))
            w.WriteElementString("ReqFilterFields", di.RequestFilterFields);

        foreach (var col in di.Columns ?? new List<BcAppSymbolCache.ReportColumnSymbol>())
            WriteDataItemField(w, col, tableId, sourceExprByColumn);

        w.WriteEndElement();
    }

    private static void WriteDataItemField(
        XmlWriter w, BcAppSymbolCache.ReportColumnSymbol col, int tableId,
        Dictionary<string, string>? sourceExprByColumn)
    {
        string? sourceExpr = null;
        sourceExprByColumn?.TryGetValue(col.Name, out sourceExpr);

        w.WriteStartElement("DataItemField");
        if (col.Id != 0)
            w.WriteElementString("ID", col.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        w.WriteElementString("FriendlyFieldName", col.Name);
        if (CanonicalNavTypeName(col.TypeName) is { } fieldType)
            w.WriteElementString("FieldType", fieldType);

        // FieldNo is only stated when the expression is a plain field of the data item's own
        // table. A computed column (`Format(...)`, a variable, a nested record's field) has
        // no field number at all, and claiming one would bind the column to an unrelated
        // field on the table.
        if (sourceExpr != null && tableId > 0)
        {
            int fieldNo = TryResolveFieldNoForExpression(tableId, sourceExpr);
            if (fieldNo > 0)
                w.WriteElementString("FieldNo", fieldNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (sourceExpr != null)
            w.WriteElementString("SourceExpr", sourceExpr);
        w.WriteEndElement();
    }

    private static Type? _navTypeEnum;
    private static readonly ConcurrentDictionary<string, string?> _canonicalNavTypeNames = new();

    /// <summary>
    /// The AL type name a column's TypeDefinition states, spelled the way BC's own
    /// <c>NavType</c> enum spells it.
    ///
    /// <c>MetaDataItemColumn</c>'s ctor reads <c>FieldType</c> with a CASE-SENSITIVE
    /// <c>Enum.Parse</c>, while the symbol file uses AL's casing — so a BLOB column arrives
    /// as "Blob" and threw ArgumentException, killing the whole document over one column.
    /// Matching against the live enum (rather than a hand-written table) also means a type
    /// this BC version added needs no change here.
    ///
    /// Returns null for a type the enum does not know, so the column is emitted without a
    /// FieldType instead of with an unparseable one.
    /// </summary>
    private static string? CanonicalNavTypeName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        return _canonicalNavTypeNames.GetOrAdd(typeName!, static name =>
        {
            _navTypeEnum ??= AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")?
                .GetType("Microsoft.Dynamics.Nav.Types.NavType");
            if (_navTypeEnum == null) return null;
            foreach (var member in Enum.GetNames(_navTypeEnum))
                if (string.Equals(member, name, StringComparison.OrdinalIgnoreCase))
                    return member;
            return null;
        });
    }

    /// <summary>
    /// Field number for a column expression that is a direct field reference — bare
    /// (<c>Name</c>), quoted (<c>"No."</c>) or record-qualified (<c>Header."No."</c>).
    /// Returns 0 for anything else, which is the honest answer for a computed column.
    /// </summary>
    private static int TryResolveFieldNoForExpression(int tableId, string expression)
    {
        var expr = expression.Trim();
        // Strip a leading record qualifier: `Header."No."` / `Header.Name`. A quoted
        // segment may itself contain dots (`"Amount (LCY)"`), so only an unquoted dot
        // outside quotes separates the qualifier.
        if (!expr.StartsWith('"'))
        {
            int dot = expr.IndexOf('.');
            if (dot > 0 && dot < expr.Length - 1) expr = expr.Substring(dot + 1).Trim();
        }
        if (expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
            expr = expr.Substring(1, expr.Length - 2);
        // Reject anything that cannot be a field name. Spaces and parens are NOT
        // disqualifying on their own — "Posting Date" and "Amount (LCY)" are real field
        // names — so the test is a call/operator/literal shape, matched against the
        // unquoted form only.
        if (expr.Length == 0
            || expr.Contains('+') || expr.Contains('-') || expr.Contains('\'')
            || expr.Contains(':') || expr.Contains(','))
            return 0;
        // `Format(x)` survives the checks above; a genuine field name never has a paren
        // immediately after a word with no space, whereas a call always does.
        if (Regex.IsMatch(expr, @"\w\(")) return 0;

        if (!_parsedTables.TryGetValue(tableId, out var table)) return 0;
        foreach (var f in table.Fields)
            if (string.Equals(f.FieldName, expr, StringComparison.OrdinalIgnoreCase))
                return f.FieldId;
        return 0;
    }
}
