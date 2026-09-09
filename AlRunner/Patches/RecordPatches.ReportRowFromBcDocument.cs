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
//   as before. Nothing here answers a column with a guess: a report with no registered
//   document keeps the AL-derived row it had before.
//
// SORTING FIELDS AND REQUEST FILTER FIELDS — NO NCLMetaTable LOOKUP IS NEEDED (#3620)
//   Ncl resolves both through a table's field numbering — GetSortingFieldsIfAny runs
//   TableViewParser over DataItemTableView, GetRequestFilterFieldsIfAny calls
//   NCLMetaTable.FindFieldMatch per name — so #3607 deferred them as "a different mechanism".
//   Measured against the emitted document, that resolution has ALREADY HAPPENED at compile
//   time: BC writes both as the Field<N> token form, and FindFieldMatch's first branch strips
//   a "Field" prefix and parses the rest as a field number. So reading the numbers out of the
//   document is not an approximation of BC's resolution — it lands on the identical value by
//   the identical rule, without a table lookup and without a request-page build.
//   docs/report-metadata-from-bc.md#sorting-and-request-filter-fields has the measurement.
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

    /// <summary>
    /// <see cref="SortingFields"/> and <see cref="RequestFilterFields"/> are already the
    /// comma-separated FIELD NUMBERS Ncl's provider hands out, derived from the document by
    /// <see cref="FieldNumbersFrom"/>. <see cref="ViewName"/> is the document's own
    /// <c>DataItemViewName</c> — the key Ncl matches a data item to its request-page filter
    /// control on, and the reason a filter control is reachable without building the page.
    /// </summary>
    private sealed record BcReportDocumentDataItem(
        int Id, string VarName, int TableId, int Indent, string TableView,
        string ViewName, string SortingFields, string RequestFilterFields);

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
        var reqFilterFieldsByViewName = CollectBcRequestPageFilterFields(root);

        var items = new List<BcReportDocumentDataItem>();
        foreach (XmlNode child in root.ChildNodes)
            if (child is XmlElement e && e.Name == "DataItem")
                CollectBcDataItem(e, reqFilterFieldsByViewName, items);

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
    private static void CollectBcDataItem(
        XmlElement item, Dictionary<string, string> reqFilterFieldsByViewName,
        List<BcReportDocumentDataItem> into)
    {
        var viewName = ReadBcText(item, "DataItemViewName");
        var tableView = ReadBcText(item, "DataItemTableView");

        into.Add(new BcReportDocumentDataItem(
            Id: ReadBcInt(item, "ID"),
            VarName: ReadBcText(item, "DataItemVarName"),
            TableId: ReadBcInt(item, "DataItemTable"),
            Indent: ReadBcInt(item, "DataItemIndent"),
            TableView: tableView,
            ViewName: viewName,
            SortingFields: FieldNumbersFrom(SortingClauseOf(tableView)),
            RequestFilterFields: FieldNumbersFrom(
                viewName.Length > 0 && reqFilterFieldsByViewName.TryGetValue(viewName, out var rff)
                    ? rff : string.Empty)));

        foreach (XmlNode child in item.ChildNodes)
            if (child is XmlElement e && e.Name == "DataItem")
                CollectBcDataItem(e, reqFilterFieldsByViewName, into);
    }

    /// <summary>
    /// Every request-page filter control's <c>ReqFilterFields</c>, keyed by the
    /// <c>DataColumnName</c> that matches a data item's <c>DataItemViewName</c> — the exact
    /// pairing <c>ReportDataItemsDataProvider.GetReportDataItems</c> performs with
    /// <c>filterControls.SingleOrDefault(x =&gt; x.DataColumnName == dataColumnName)</c>.
    ///
    /// <para>Walks the whole subtree rather than the documented nesting
    /// (<c>RequestPage/PageDefinition/Content/Containers/Controls/Controls</c>): Ncl reaches
    /// these through <c>RequestPageDefinition.FindAll</c>, which is also depth-agnostic, and a
    /// control group nested one level deeper for an extra data item would otherwise be
    /// silently dropped. The element is identified by its <c>xsi:type</c> attribute, because
    /// every control in that tree is spelled <c>&lt;Controls&gt;</c>.</para>
    ///
    /// <para>A control with no <c>ReqFilterFields</c> attribute — the shape a data item that
    /// declares none produces — is skipped rather than stored empty, so the two are
    /// indistinguishable downstream, which is what BC does with a null: an absent entry and an
    /// empty string both yield <c>string.Empty</c> from <c>GetRequestFilterFieldsIfAny</c>.</para>
    ///
    /// <para>A duplicate <c>DataColumnName</c> is dropped, not overwritten. Ncl's own
    /// <c>SingleOrDefault</c> THROWS on that shape, so a document containing one is malformed;
    /// answering nothing for the ambiguous data item is the closer of the two available
    /// approximations to "no answer", and it cannot silently pick the wrong control.</para>
    /// </summary>
    private static Dictionary<string, string> CollectBcRequestPageFilterFields(XmlElement root)
    {
        var byViewName = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (XmlNode node in root.GetElementsByTagName("Controls"))
        {
            if (node is not XmlElement control) continue;
            var type = control.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
            if (type != "FilterControlDefinition") continue;

            var dataColumnName = control.GetAttribute("DataColumnName");
            if (dataColumnName.Length == 0) continue;
            if (!control.HasAttribute("ReqFilterFields")) continue;

            if (!byViewName.TryAdd(dataColumnName, control.GetAttribute("ReqFilterFields")))
                ambiguous.Add(dataColumnName);
        }

        foreach (var name in ambiguous) byViewName.Remove(name);
        return byViewName;
    }

    /// <summary>
    /// The contents of the compiled view's <c>SORTING(...)</c> clause, or empty when it states
    /// none. The view is BC's own normal form — <c>SORTING(Field5,Field2) ORDER(1)
    /// WHERE(Field5=1(&gt;A))</c> — so the clause is delimited by the first <c>)</c>: a field
    /// token cannot contain one, and the parenthesised filter expressions that can all sit
    /// inside WHERE, after this clause.
    /// </summary>
    private static string SortingClauseOf(string tableView)
    {
        const string marker = "SORTING(";
        var start = tableView.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += marker.Length;
        var end = tableView.IndexOf(')', start);
        return end < 0 ? string.Empty : tableView.Substring(start, end - start);
    }

    /// <summary>
    /// A comma-separated list of the document's <c>Field&lt;N&gt;</c> tokens, rendered as the
    /// comma-separated field NUMBERS both columns carry.
    ///
    /// <para>This is BC's own resolution rule, not an approximation of it.
    /// <c>NCLMetaTable.FindFieldMatch</c> — which is what both
    /// <c>GetRequestFilterFieldsIfAny</c> and, through <c>TableViewResolver.AddSortingField</c>,
    /// <c>GetSortingFieldsIfAny</c> call — begins by stripping a leading <c>Field</c> (or
    /// <c>#</c>) and returning <c>GetFieldByNo</c> of the remainder when it parses as an
    /// integer. Every token BC's emitter writes into these two places is in that form, so the
    /// name-lookup and prefix-fallback branches below it are never reached from here and no
    /// <c>NCLMetaTable</c> is required. Verified on BC 28.1; the fixture measurement is in
    /// docs/report-metadata-from-bc.md#sorting-and-request-filter-fields.</para>
    ///
    /// <para>A token in any other shape is DROPPED, which is what BC does with one it cannot
    /// resolve: <c>GetRequestFilterFieldsIfAny</c> skips a null <c>FindFieldMatch</c> result
    /// (it passes <c>trapError: true</c>) and <c>AddSortingField</c> traces and skips. Dropping
    /// keeps the answer a list of field numbers in every case — never a name leaking into a
    /// column whose contract is numbers, which is exactly the wrong answer this replaces.</para>
    /// </summary>
    private static string FieldNumbersFrom(string tokens)
    {
        if (string.IsNullOrEmpty(tokens)) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var raw in tokens.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.StartsWith("Field", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(5);
            else if (token.StartsWith('#'))
                token = token.Substring(1);
            if (!int.TryParse(token, out var fieldNo)) continue;

            if (sb.Length > 0) sb.Append(',');
            sb.Append(fieldNo);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The comma-separated field NUMBERS for a data item whose <c>DataItemTableView</c> is AL
    /// SOURCE TEXT rather than BC's compiled normal form — the shape a precompiled
    /// dependency's SymbolReference.json states (#3627). <paramref name="fieldNoByIdentifier"/>
    /// resolves an AL field name; a null one means the data item's table could not be
    /// resolved, and then only the <c>Field&lt;N&gt;</c> tokens survive, exactly as before.
    ///
    /// <para><b>Why this exists at all.</b> <see cref="FieldNumbersFrom"/> alone is right for
    /// BC's document, where the emitter has already resolved every token to
    /// <c>Field&lt;N&gt;</c>. A symbol file has NOT: measured on Base Application
    /// 28.1.49838.53910, its 659 reports carry 1927 data items, 1759 of which state a
    /// <c>SORTING</c> clause, and all 3201 of those tokens are AL identifiers — 988 bare and
    /// 2213 double-quoted, none in <c>Field&lt;N&gt;</c> form. So the column answered empty for
    /// every one of them.</para>
    ///
    /// <para><b>The resolution order is BC's, not an approximation.</b> A sorting token reaches
    /// <c>TableViewResolver.AddSortingField</c>, which calls
    /// <c>TableFilterResolver.ResolveField(field, trapError: true)</c> —
    /// <c>NCLMetaTable.FindFieldMatch(field, prefixFallback: true, trapError: true)</c>. That
    /// method, decompiled from BC 28.1: strip a leading <c>Field</c> or <c>#</c> and use
    /// <c>GetFieldByNo</c> when the remainder parses as an integer; else exact name, then exact
    /// caption; else, because <c>prefixFallback</c> is true here, a name prefix then a caption
    /// prefix; else null, which <c>AddSortingField</c> traces and SKIPS. The steps below are
    /// that list in that order, so a token resolves to the same field BC would pick or is
    /// dropped the same way. See docs/report-metadata-from-bc.md#sorting-fields-on-the-dependency-path.</para>
    ///
    /// <para><b>Cost.</b> Paid once per run, not per read: the only caller is
    /// <c>EnumerateKnownReports</c>, whose result is cached per (registration epoch, parsed
    /// report count), and the map it passes is memoized per table for the whole build. See
    /// that method for why paying it per population would reproduce the #3607 watchdog
    /// timeout.</para>
    /// </summary>
    private static string SortingFieldNumbersFromAlView(
        string tableView, Func<string, int>? fieldNoByIdentifier)
    {
        var clause = AlSortingClauseOf(tableView);
        if (clause.Length == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var raw in SplitSortingTokens(clause))
        {
            var token = UnquoteAlIdentifier(raw.Trim());
            if (token.Length == 0) continue;

            // Step 1 — FindFieldMatch's own first branch, before any name is consulted. A view
            // already in BC's normal form (SORTING(Field5,Field2)) therefore still resolves
            // here without a lookup, which is the path #3620 fixed and must not regress.
            var stripped = token;
            if (stripped.StartsWith("Field", StringComparison.OrdinalIgnoreCase))
                stripped = stripped.Substring(5);
            else if (stripped.StartsWith('#'))
                stripped = stripped.Substring(1);

            int fieldNo;
            if (int.TryParse(stripped, out var byNumber)) fieldNo = byNumber;
            else if (fieldNoByIdentifier == null) continue;   // no table: nothing to look up
            else fieldNo = fieldNoByIdentifier(token);

            // A token BC's FindFieldMatch would answer null for is DROPPED, not emitted as a
            // placeholder and not allowed to abandon the rest of the clause — AddSortingField
            // traces and moves on to the next field.
            if (fieldNo <= 0) continue;

            if (sb.Length > 0) sb.Append(',');
            sb.Append(fieldNo);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The <c>sorting(...)</c> body of a view written as AL SOURCE TEXT, which differs from
    /// <see cref="SortingClauseOf"/> in the two ways that matter, both measured on Base
    /// Application 28.1's SymbolReference.json:
    ///
    /// <para>1. <b>The keyword is lowercase.</b> AL writes <c>sorting(</c>; BC's compiled
    /// normal form writes <c>SORTING(</c>. <see cref="SortingClauseOf"/> matches
    /// <c>StringComparison.Ordinal</c>, correctly, because the document it reads is always in
    /// BC's form — pointing it at AL text answers empty for every one of the 1759 data items
    /// that state a clause, which is exactly the #3627 symptom.</para>
    ///
    /// <para>2. <b>The body can contain a <c>)</c>.</b> The document's form cannot, so
    /// <see cref="SortingClauseOf"/> ends the clause at the first one. AL text can:
    /// <c>sorting("Amount (LCY)")</c> is a legal field name, and a quoted identifier is where
    /// a parenthesis hides. So the close is matched by depth, ignoring anything inside AL
    /// quotes.</para>
    ///
    /// <para>Kept separate rather than widening <see cref="SortingClauseOf"/>: that method is
    /// on the document path, where a case-insensitive match would also accept a WHERE-clause
    /// filter value that happens to spell "sorting(", and where the first-<c>)</c> rule is a
    /// stated property of the input rather than a shortcut.</para>
    /// </summary>
    private static string AlSortingClauseOf(string tableView)
    {
        const string marker = "sorting";
        int start = -1;
        for (int i = 0; i + marker.Length <= tableView.Length; i++)
        {
            // Skip over a quoted identifier so a field named "Sorting Code" cannot be read as
            // the keyword.
            if (tableView[i] == '"')
            {
                i = SkipAlQuoted(tableView, i) - 1;
                continue;
            }
            if (string.Compare(tableView, i, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            // A keyword, not the tail of a longer identifier.
            if (i > 0 && (char.IsLetterOrDigit(tableView[i - 1]) || tableView[i - 1] == '_')) continue;
            int j = i + marker.Length;
            while (j < tableView.Length && char.IsWhiteSpace(tableView[j])) j++;
            if (j < tableView.Length && tableView[j] == '(') { start = j + 1; break; }
        }
        if (start < 0) return string.Empty;

        int depth = 1;
        for (int i = start; i < tableView.Length; i++)
        {
            var c = tableView[i];
            if (c == '"') { i = SkipAlQuoted(tableView, i) - 1; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return tableView.Substring(start, i - start);
        }
        // Unbalanced: the AL compiler would not have produced this, so there is no clause to
        // read rather than a truncated guess.
        return string.Empty;
    }

    /// <summary>
    /// A <c>SORTING(...)</c> body split on the commas that separate fields, ignoring commas
    /// inside an AL quoted identifier. <c>sorting("Posting Date", "Document No.")</c> is the
    /// ordinary case, but a field may legitimately be named <c>"Amount, LCY"</c>, and splitting
    /// that on a bare <c>,</c> yields two tokens that resolve to nothing — dropping a field
    /// BC would have sorted by.
    /// </summary>
    private static List<string> SplitSortingTokens(string clause)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < clause.Length; i++)
        {
            var c = clause[i];
            if (c == '"')
            {
                // AL doubles a literal quote inside an identifier; both halves stay in the
                // token so UnquoteAlIdentifier can collapse them.
                if (inQuotes && i + 1 < clause.Length && clause[i + 1] == '"')
                {
                    current.Append('"').Append('"');
                    i++;
                    continue;
                }
                inQuotes = !inQuotes;
                current.Append(c);
                continue;
            }
            if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// An AL quoted identifier reduced to the name BC matches on: the surrounding quotes
    /// removed and a doubled <c>""</c> collapsed to one. A bare identifier is returned
    /// unchanged — 988 of Base Application's 3201 sorting tokens are bare.
    /// </summary>
    private static string UnquoteAlIdentifier(string token)
    {
        if (token.Length < 2 || token[0] != '"' || token[^1] != '"') return token;
        return token.Substring(1, token.Length - 2).Replace("\"\"", "\"");
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
            // Both field-number columns come from the document (#3620). Neither falls back to
            // the AL-derived text: the columns' contract is field NUMBERS, so the AL names the
            // row used to carry were never a partial answer — they were a different answer.
            items.Add(new ReportDataItemRow(
                di.Id, di.VarName, di.TableId, di.Indent, di.TableView,
                di.RequestFilterFields, di.SortingFields));
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
}
