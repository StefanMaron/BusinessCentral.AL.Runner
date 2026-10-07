// DependencyReportExtensionDeltas — the runtime-delta document of a PRECOMPILED reportextension
// (#4837), in the shape BC's emitter writes for a source-compiled one, so BC's own
// MetadataRuntimeDeltaApplicator (NavReportSync.ApplyReportExtensionDeltas) can merge the data
// items it adds into the report's MetaReport.
//
// Sources, both inside the .app: SymbolReference.json states each added data item (name, table,
// absolute Indentation, parent, id, table view, link, columns with ids and types); the extension's
// own AL source states what the symbol file does not — WHICH data item each root item is anchored
// to and HOW (addfirst/addlast/addbefore/addafter), and each column's source expression.
//
// Shape measured against BC's emitter (a probe compiled with it; AlRunner.Tests/
// PrecompiledReportExtensionDeltaTests): DataItemAdd{AnchorName, AnchorId, Operation, DataItem}, a
// nested item anchored AddLast on its parent. Anything that cannot be stated that way returns null
// with a reason, never a guess: the extension then stays unbound and running its report refuses.
using System.Text;
using System.Xml;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The delta document of every precompiled reportextension of <paramref name="reportId"/> whose
    /// added data items can be derived, in id order. An extension that adds none, or whose items
    /// cannot be stated whole, is absent — see <see cref="TryBuildPrecompiledReportExtensionDelta"/>.
    /// </summary>
    internal static IReadOnlyList<(int ExtensionId, string Xml)> PrecompiledReportExtensionDeltasFor(int reportId)
    {
        var result = new List<(int ExtensionId, string Xml)>();
        foreach (var (report, ext, source) in PrecompiledReportExtensionsOf(reportId))
        {
            var xml = TryBuildPrecompiledReportExtensionDelta(report, ext, source, out var reason);
            if (xml != null) result.Add((ext.Id, xml));
            else if (reason != null && ext.DataItems is { Count: > 0 })
                Console.Error.WriteLine($"[RecordPatches] precompiled reportextension {ext.Id} of report {reportId} "
                    + $"is not merged into the report's metadata: {reason}");
        }
        return result;
    }

    /// <summary>
    /// Each precompiled reportextension of a precompiled report, in id order, with the report's
    /// symbol, the extension's and the extension's AL source (null when the package ships none).
    /// A report the runner compiled from source has none.
    /// </summary>
    internal static IEnumerable<(BcAppSymbolCache.ReportSymbol Report, BcAppSymbolCache.ReportExtensionSymbol Extension, string? Source)>
        PrecompiledReportExtensionsOf(int reportId)
    {
        if (_parsedReports.ContainsKey(reportId) || FindDependencyReportSymbol(reportId) is not { } found) yield break;
        foreach (var (appPath, ext) in DependencyReportExtensionsWithApp(found.Report.Name).OrderBy(e => e.Extension.Id))
            yield return (found.Report, ext, string.IsNullOrEmpty(ext.ReferenceSourceFileName)
                ? null : BcAppSymbolCache.TryReadSourceFile(appPath, ext.ReferenceSourceFileName!));
    }

    /// <summary>
    /// The delta document for <paramref name="ext"/>'s added data items, or null with the reason it
    /// cannot be stated: it adds none; it adds columns to an existing data item (unmeasured); its
    /// source is not shipped or does not anchor every root item; an anchor is not a data item of
    /// the base report; a table does not resolve.
    /// </summary>
    internal static string? TryBuildPrecompiledReportExtensionDelta(
        BcAppSymbolCache.ReportSymbol report, BcAppSymbolCache.ReportExtensionSymbol ext, string? source, out string? reason)
    {
        reason = null;
        var items = ext.DataItems;
        if (items == null || items.Count == 0) { reason = "its symbol file states no added data item"; return null; }
        if (ext.Columns is { Count: > 0 }) { reason = "it adds columns to an existing data item"; return null; }
        if (string.IsNullOrEmpty(source)) { reason = "the package ships no source stating where its data items are anchored"; return null; }

        var syntax = ParseAlObjects(source).OfType<NavSyntax.ReportExtensionSyntax>().FirstOrDefault(x => ObjectIdOf(x) == ext.Id);
        if (syntax == null) { reason = "its source file does not declare it"; return null; }
        var anchors = new Dictionary<string, (string Operation, string Anchor)>(StringComparer.OrdinalIgnoreCase);
        if (syntax.DataSet == null) { reason = "its source declares no data set"; return null; }
        foreach (var change in syntax.DataSet.Changes)
        {
            if (change is NavSyntax.ReportExtensionDataSetAddColumnSyntax) { reason = "it adds columns to an existing data item"; return null; }
            if (change is not NavSyntax.ReportExtensionDataSetAddDataItemSyntax add) continue;
            var operation = AddOperationName(add.ChangeKeyword.ToString().Trim());
            if (operation == null) { reason = $"its data set uses '{add.ChangeKeyword.ToString().Trim()}', which is not an add operation"; return null; }
            // The first data item of a block takes the block's operation and anchor; each further one
            // is AddAfter the one before it (measured on BC's emitter, the equivalence test).
            var anchor = IdentText(add.Anchor);
            foreach (var root in add.DataItems.OfType<NavSyntax.ReportDataItemSyntax>())
            {
                anchors[IdentText(root.Name)] = (operation, anchor);
                operation = "AddAfter";
                anchor = IdentText(root.Name);
            }
        }

        var extNames = new Dictionary<string, BcAppSymbolCache.ReportDataItemSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var di in items) extNames[di.Name] = di;
        if (anchors.Keys.Any(n => !extNames.ContainsKey(n))) { reason = "its source declares a data item its symbol file does not"; return null; }

        var baseIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var di in report.DataItems) baseIds[di.Name] = di.Id;
        var columnExpressions = ColumnSourceExpressionsFrom(source);

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("ReportExtension", MetaObjectsNamespace);
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            w.WriteAttributeString("ALNamespace", "");
            w.WriteElementString("MetadataVersion", "130000");
            w.WriteElementString("ID", ext.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteElementString("Name", ext.Name);
            foreach (var di in items)
            {
                string operation, anchor;
                int anchorId;
                if (anchors.TryGetValue(di.Name, out var root))
                {
                    (operation, anchor) = root;
                    if (extNames.TryGetValue(anchor, out var previous)) anchorId = previous.Id;
                    else if (!baseIds.TryGetValue(anchor, out anchorId)) anchorId = 0;
                    if (anchorId == 0)
                    { reason = $"its anchor '{anchor}' is not a data item of the report as its symbol file states it"; return null; }
                }
                else
                {
                    operation = "AddLast";
                    anchor = di.OwningDataItemName ?? "";
                    if (!extNames.TryGetValue(anchor, out var parent) || parent.Id == 0)
                    { reason = $"data item '{di.Name}' is nested under '{anchor}', which the extension does not declare"; return null; }
                    anchorId = parent.Id;
                }
                if (di.Id == 0) { reason = $"data item '{di.Name}' states no id"; return null; }
                if (ResolveTableIdByName(di.RelatedTable) <= 0) { reason = $"the table '{di.RelatedTable}' of data item '{di.Name}' does not resolve"; return null; }
                w.WriteStartElement("DataItemAdd");
                w.WriteElementString("AnchorName", anchor);
                w.WriteElementString("AnchorId", anchorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteElementString("Operation", operation);
                WriteDataItem(w, di, columnExpressions, di.OwningDataItemName, stateFieldNoOfComputedColumns: true);
                w.WriteEndElement();
            }
            w.WriteElementString("Labels", "");
            w.WriteElementString("Layouts", "");
            w.WriteEndElement();
        }
        return sb.ToString();
    }

    private static string? AddOperationName(string keyword) => keyword.ToLowerInvariant() switch
    {
        "addfirst" => "AddFirst",
        "addlast" => "AddLast",
        "addbefore" => "AddBefore",
        "addafter" => "AddAfter",
        _ => null,
    };
}
