// RecordPatches.ReportRowFromBcDocument — answer Report Metadata (2000000139) and
// Report Data Items (2000000203) from BC's own emitted metadata document, instead of
// re-deriving the same properties from AL source text (issue #3607, part of the
// conversion chain #3562 tracks). Follows RecordPatches.NclMetaTableFromBcDocument.cs,
// which did this for Table Metadata at #3584.
//
// WHERE THE DOCUMENT COMES FROM
//   AlReportMetadataRegistry holds the runtime metadata XML BC's own Compilation.Emit
//   produced for every report the runner compiled. It already feeds the report EXECUTION
//   path (NavReportSync.GetRealMetaReport). These two virtual tables read none of it before
//   this file — grep AlReportMetadataRegistry in RecordPatches.ReportMetadataVirtualTable.cs
//   returned nothing. A report from a precompiled dependency keeps the row derived from its
//   SymbolReference.json; see TryGetBcReportDocument for why that route is not taken here.
//
// WHY THE XML AND NOT THE MetaReport GetRealMetaReport ALREADY BUILDS
//   That was the first implementation and it is a trap, so it is written down rather than
//   rediscovered. GetRealMetaReport forces MetaReport.RequestFormMetadata as a deliberate
//   side effect — it builds the report's whole REQUEST PAGE so a failure is contained at
//   construction. Populating a virtual table enumerates EVERY known report, so that turned
//   one Report Metadata read into 28 request-page builds on the al-language corpus and blew
//   the 60s per-test watchdog on the first assertion. The properties below are stated
//   directly by the document, so reading them costs one XML parse and no page construction.
//
// WHY BC'S DOCUMENT AND NOT THE AL TEXT
//   BC's own providers for these two tables read BC's compiled metadata, so the document is
//   not merely another source for the same values — it is the source, and it differs from AL
//   source text in ways a caller can observe. Measured on the test fixture report 90311
//   (BC 28.1); docs/report-metadata-from-bc.md has the fixture and the measurement:
//
//     DataItemTableView   AL text  sorting("Entry No.") order(descending)
//                         document SORTING(1) ORDER(1)               <- BC's normal form
//     data item Id        ordinal  1
//                         document 1369927887                        <- BC's assigned id
//     WordMergeDataItem   AL text  never read at all -> ""
//                         document Src
//
//   Ncl's ReportDataItemsDataProvider.GetReportDataItems keys, sorts and range-filters on
//   MetaDataItem.Id, so the ordinal was not a harmless stand-in: a caller that reads an id
//   out of one row and filters this table by it gets nothing back.
//
// SCOPE — WHAT THIS DOES NOT CLAIM
//   Ncl's provider fills 29 Report Metadata columns off NCLMetaReport. This fills the ones
//   the document states directly and leaves the rest on BC's own GetDefaultNavValue, exactly
//   as before. Sorting Fields and Request Filter Fields both resolve through a table's
//   NCLMetaField numbering (GetSortingFieldsIfAny / GetRequestFilterFieldsIfAny), and
//   ReqFilterFields is stated on the request page's filter control rather than on the data
//   item at all — a separate resolution step this change does not take on; #3620 tracks it.
//   Nothing here answers a column with a guess: a report with no registered document keeps
//   the AL-derived row it had before.
//
// PRECOMPILED-DLL RESPECT
//   Reads an XML document BC's own emitter produced. No BC method body is rewritten, no BC
//   type is constructed, and no AL business logic is touched.
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// The subset of BC's report document the two virtual tables read, parsed once per report.
    /// <see cref="DataItems"/> is the document's own order, flattened depth-first, which is
    /// how <c>MetaReport.DataItems</c> presents it.
    /// </summary>
    private sealed record BcReportDocument(
        bool ProcessingOnly, bool UseRequestPage, string WordMergeDataItem,
        List<BcReportDocumentDataItem> DataItems);

    private sealed record BcReportDocumentDataItem(
        int Id, string VarName, int TableId, int Indent, string TableView);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, BcReportDocument?>
        _bcReportDocuments = new();

    internal static void ClearBcReportDocuments() => _bcReportDocuments.Clear();

    /// <summary>
    /// BC's document for this report, or null when the emitter never captured one. A null
    /// keeps the AL-derived row, which is exactly what the caller had before this file.
    ///
    /// <para><b>Emit capture only — deliberately NOT TryBuildDependencyReportMetadata.</b>
    /// That is the other producer of the same document, and it is the one
    /// <c>NavReportSync.GetRealMetaReport</c> falls back to, so reaching for it here looks
    /// like completeness. It is a trap on this path, because the two callers ask at
    /// different scales: report execution synthesizes a document for the ONE report being
    /// run, while populating a virtual table asks about EVERY report the runner knows —
    /// which, with Base Application registered, is 659 of them. Each miss walks every
    /// registered .app's symbol list and each hit additionally reads AL source out of the
    /// package to recover column expressions. Measured on the al-language corpus, that put
    /// the first Report Metadata read past the 60s per-test watchdog; the emit-captured
    /// lookup below is a dictionary hit and the same read is 26ms.</para>
    ///
    /// <para>Nothing is lost by the narrower source: a dependency report has no emitted
    /// document, so it keeps the SymbolReference.json-derived row — which is where its
    /// WordMergeDataItem, ProcessingOnly and data-item tree already came from.</para>
    /// </summary>
    private static BcReportDocument? TryGetBcReportDocument(int reportId)
        => _bcReportDocuments.GetOrAdd(reportId, static id =>
        {
            if (!AlReportMetadataRegistry.TryGet(id, out var xml) || string.IsNullOrEmpty(xml))
                return null;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                return doc.DocumentElement == null ? null : ParseBcReportDocument(doc.DocumentElement);
            }
            catch (XmlException ex)
            {
                // A malformed document is a capture bug, not a reason to serve nothing: fall
                // back to the AL-derived row and name the report once so it is diagnosable.
                Console.Error.WriteLine(
                    $"[report-metadata] report {id}: BC's metadata document did not parse "
                    + $"({ex.Message}) — falling back to the AL-derived row");
                return null;
            }
        });

    private static BcReportDocument ParseBcReportDocument(XmlElement root)
    {
        var items = new List<BcReportDocumentDataItem>();
        foreach (XmlNode child in root.ChildNodes)
            if (child is XmlElement e && e.Name == "DataItem")
                CollectBcDataItem(e, items);

        return new BcReportDocument(
            // AL's own defaults where the document is silent, which is what BC's emitter does
            // for a property left at its default: ProcessingOnly false, UseRequestPage true.
            ProcessingOnly: ReadBcFlag(root, "ProcessingOnly", defaultValue: false),
            UseRequestPage: ReadBcFlag(root, "UseRequestPage", defaultValue: true),
            WordMergeDataItem: ReadBcText(root, "WordMergeDataItem"),
            DataItems: items);
    }

    /// <summary>
    /// Depth-first, parent before children — the order <c>MetaReport.DataItems</c> reports and
    /// the order the AL parser already produced, so the two remain comparable by position for
    /// anything that still pairs them up. DataItemIndent is read from the document rather than
    /// counted, because the document states it and a counted depth would be a second opinion.
    /// </summary>
    private static void CollectBcDataItem(XmlElement item, List<BcReportDocumentDataItem> into)
    {
        into.Add(new BcReportDocumentDataItem(
            Id: ReadBcInt(item, "ID"),
            VarName: ReadBcText(item, "DataItemVarName"),
            TableId: ReadBcInt(item, "DataItemTable"),
            Indent: ReadBcInt(item, "DataItemIndent"),
            TableView: ReadBcText(item, "DataItemTableView")));

        foreach (XmlNode child in item.ChildNodes)
            if (child is XmlElement e && e.Name == "DataItem")
                CollectBcDataItem(e, into);
    }

    private static string ReadBcText(XmlElement parent, string name)
    {
        foreach (XmlNode child in parent.ChildNodes)
            if (child is XmlElement e && e.Name == name)
                return e.InnerText ?? string.Empty;
        return string.Empty;
    }

    private static int ReadBcInt(XmlElement parent, string name)
        => int.TryParse(ReadBcText(parent, name), out var v) ? v : 0;

    /// <summary>BC writes a boolean property as "1"/"0" in this document.</summary>
    private static bool ReadBcFlag(XmlElement parent, string name, bool defaultValue)
    {
        var text = ReadBcText(parent, name);
        return string.IsNullOrEmpty(text) ? defaultValue : text == "1";
    }

    /// <summary>
    /// Overlay BC's document onto a row derived from AL source. Every property BC states wins;
    /// anything BC does not state (the caption, and the columns neither source answers) keeps
    /// the AL-derived value. Returns <paramref name="row"/> unchanged when no document exists.
    ///
    /// The data-item list is REPLACED rather than merged, because the two sources disagree on
    /// the identity of a row — BC's Id against the AL parser's ordinal — so a merge would have
    /// to pair them up by position and would mis-pair silently the moment the orders differ.
    /// </summary>
    private static ReportRow ApplyBcReportDocument(ReportRow row)
    {
        var doc = TryGetBcReportDocument(row.Id);
        if (doc == null) return row;

        // A document with no DataItem element describes a report the AL-derived row may still
        // know the dataset of (a reportextension's base, say). Nothing to gain by replacing a
        // populated list with an empty one, so only the scalar properties are taken.
        if (doc.DataItems.Count == 0)
            return row with
            {
                ProcessingOnly = doc.ProcessingOnly,
                UseRequestPage = doc.UseRequestPage,
                WordMergeDataItem = doc.WordMergeDataItem,
            };

        var items = new List<ReportDataItemRow>();
        foreach (var di in doc.DataItems)
        {
            // FirstDataItemTableID = 0 is load-bearing: a caller reads it as "this report has
            // no dataset". A document that does not state a data item's table cannot be used
            // to claim one, so the whole row falls back rather than answering 0 — the same
            // refusal EnumerateKnownReports makes for a table it cannot resolve by name.
            if (di.TableId <= 0) return row;
            items.Add(new ReportDataItemRow(
                di.Id, di.VarName, di.TableId, di.Indent, di.TableView,
                // ReqFilterFields is stated on the request page's filter control, not on the
                // data item, and BC resolves it to field NUMBERS. Keeping the AL-derived text
                // is the honest half-answer until #3620 does that resolution; replacing it
                // with "" would lose information the row already carried.
                RequestFilterFieldsFor(row, di)));
        }

        return row with
        {
            ProcessingOnly = doc.ProcessingOnly,
            UseRequestPage = doc.UseRequestPage,
            WordMergeDataItem = doc.WordMergeDataItem,
            FirstDataItemTableId = FirstRootTableId(items),
            DataItems = items,
        };
    }

    /// <summary>
    /// The AL-derived Request Filter Fields text for the data item BC calls
    /// <paramref name="di"/>, matched by variable name — the one key both sources agree on,
    /// since BC's id and the parser's ordinal are different numbers.
    /// </summary>
    private static string RequestFilterFieldsFor(ReportRow row, BcReportDocumentDataItem di)
    {
        foreach (var existing in row.DataItems)
            if (string.Equals(existing.Name, di.VarName, StringComparison.OrdinalIgnoreCase))
                return existing.RequestFilterFields;
        return string.Empty;
    }
}
