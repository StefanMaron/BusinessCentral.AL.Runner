// RecordPatches.PageControlFieldVirtualTable — managed provider for the
// "Page Control Field" (2000000192) system virtual table.
//
// WHY THIS EXISTS (issue #1779)
//   Page Control Field is virtual on the service tier: one row per field control declared
//   on a page (PageNo, ControlId, ControlName, TableNo, FieldNo, Enabled, Editable,
//   Visible, SourceExpression, OptionString, Sequence), INCLUDING controls declared
//   Visible = false — that is what personalization-availability checks read. It routed to
//   the same empty in-memory store as every other table here, so a query filtered on
//   PageNo/ControlName silently found nothing: FindFirst() returned false, no error, both
//   cold (no page ever opened) and after a TestPage had opened the page. Because the
//   failure mode is an empty result set rather than an exception, a test asserting a
//   control is *absent* would have passed against this broken provider just as easily as
//   a correct one.
//
// Enabled / Editable / Visible ARE TEXT, NOT BOOLEAN
//   Real BC stores the raw declared property EXPRESSION as text — verified against Base
//   Application 28.1's Customer Card: its "No." field control carries
//   Visible = "NoFieldVisible" (a global Boolean variable name, not a literal), and other
//   controls carry a literal "false"/"true". So this provider stores exactly the property
//   text the AL source (or the dependency's SymbolReference.json) states, or "true" when
//   the property is absent (AL's own default for all three). A caller reading a literal
//   boolean round-trips it through Evaluate(); a caller reading a variable-driven one gets
//   the variable's name, same as real BC — Evaluate would fail on that too, which is
//   faithful, not a bug.
//
// WHERE THE ROWS COME FROM (two sources, neither invented)
//   1. Pages the runner compiles itself — parsed field controls from AL source
//      (RecordPatches.AlPageParser.cs / ParsePageControls). SCOPE LIMITATION: only a
//      control whose source expression is exactly `Rec.Something` becomes a row here; a
//      control bound to anything else (compound expression, local/global variable) is
//      omitted rather than guessed at — see that method's doc comment. `modify(...)`
//      property overrides from a pageextension are not applied either (a narrower gap than
//      "no rows at all"). Rows contributed by pageextensions that extend the page are
//      merged in, keyed in the EXTENSION's own id space (BC's own IdSpace.GetMemberId
//      rule — see GetPageControlFieldMap).
//   2. Pages living in a PRECOMPILED dependency (Base Application, System Application, ISV
//      apps) — read from that .app's SymbolReference.json, which states EVERY field
//      control's SourceExpression verbatim (Rec.-bound or not) plus its compiler-assigned
//      control Id, so TableNo/FieldNo are resolved by parsing that text the same way the
//      source-parsed path does, without the Rec.-bound restriction.
//   Source-compiled pages win over symbol-derived ones for the same page id.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only, reached through the same helpers the AllObj / Table
//   Metadata / Page Metadata providers resolve. No AL business-logic body is touched.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2): one store-wiring refusal, on a table this file populates.
    /// </remarks>
    internal static RunnerOutOfScopeException PageControlFieldShapeGap(string detail)
        => VirtualTableShapeGap("Page Control Field (virtual table 2000000192)", "page-control-field-virtual-table", detail);

    internal const int PageControlFieldVirtualTableId = 2000000192;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(int PageNo, int ControlId), byte>> _pcfvPopulatedByProvider = new();

    private static bool IsPageControlFieldVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == PageControlFieldVirtualTableId;

    /// <summary>One field control as Page Control Field exposes it.</summary>
    private sealed record PageControlFieldRow(
        int PageNo, int ControlId, string ControlName, int TableNo, int FieldNo,
        string Enabled, string Editable, string Visible, string SourceExpression,
        string OptionString, int Sequence);

    private static List<PageControlFieldRow>? _pageControlFieldRows;
    // The .app term is RecordPatches' registration EPOCH, never _bcAppPaths.Count (#2888):
    // the registered set can SHRINK since #2755 / PR #2873, so a count cannot tell a set that
    // lost N entries and gained N different ones from the one it was built against — and in
    // --watch mode (same bundle, one edited file) that is the NORMAL case, not a corner. The
    // remaining terms stay counts and are sound as counts, because the dictionaries they count
    // are only ever cleared by ResetForReload, which bumps the epoch in the same breath.
    private static (int Epoch, int Parsed) _pageControlFieldRowsBuiltFrom = (-1, -1);
    private static readonly object _pageControlFieldRowsLock = new();

    private static void PopulatePageControlFieldVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw PageControlFieldShapeGap("data access has no in-memory provider");

        var done = _pcfvPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, int), byte>());

        foreach (var row in EnumerateKnownPageControlFields())
        {
            if (!done.TryAdd((row.PageNo, row.ControlId), 0)) continue;
            InsertVirtualRow(provider, metaTable,
                new object[] { PageControlFieldVirtualTableId, row.PageNo, row.ControlId, 0 },
                field => BuildPageControlFieldValue(field, row));
        }
    }

    private static object? BuildPageControlFieldValue(NCLMetaField field, PageControlFieldRow row)
    {
        object? Text(string s) => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, s ?? string.Empty });
        object? Int(int v) => _aovNavIntegerCreate!.Invoke(null, new object?[] { v });

        return NormalizeObjectTypeName(field.FieldName ?? string.Empty) switch
        {
            "pageno" => Int(row.PageNo),
            "controlid" => Int(row.ControlId),
            "controlname" => Text(row.ControlName),
            "tableno" => Int(row.TableNo),
            "fieldno" => Int(row.FieldNo),
            "enabled" => Text(row.Enabled),
            "editable" => Text(row.Editable),
            "visible" => Text(row.Visible),
            "sourceexpression" => Text(row.SourceExpression),
            "optionstring" => Text(row.OptionString),
            "sequence" => Int(row.Sequence),
            _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
        };
    }

    /// <summary>
    /// <c>Editable</c> for a control whose source is AL text or a dependency's symbol file,
    /// rather than BC's emitted document. Same rules as
    /// <c>SolveDocumentControlEditable</c> — see that method for the mechanism and the tier
    /// verdict behind it (#3653) — reduced to the two inputs these paths have.
    ///
    /// <para><paramref name="declared"/> is null when the control declares no Editable, which
    /// is the state BC's solver resolves; <paramref name="fieldEditable"/> is the bound
    /// field's own declared Editable, itself null when the FIELD declares none, and null on
    /// both counts means true.</para>
    ///
    /// <para>Neither of BC's other two rules is reachable from here: these paths carry no
    /// <c>SourceExpressionIsAssignable</c> and no page-level <c>Editable</c>, so a control that BC
    /// would force to False through either can only be reached on the document path. That is
    /// a narrower answer rather than a wrong one — a page served from these paths has no
    /// document, so there is nothing to read it from — and it is why this is a separate
    /// method instead of a call into the document one.</para>
    /// </summary>
    private static string SolveParsedControlEditable(string? declared, bool? fieldEditable)
        => declared ?? (fieldEditable ?? true).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static List<PageControlFieldRow> EnumerateKnownPageControlFields()
    {
        var generation = (BcAppRegistrationEpoch, _parsedPages.Count);
        if (_pageControlFieldRows != null && _pageControlFieldRowsBuiltFrom == generation) return _pageControlFieldRows;
        lock (_pageControlFieldRowsLock)
        {
            generation = (BcAppRegistrationEpoch, _parsedPages.Count);
            if (_pageControlFieldRows != null && _pageControlFieldRowsBuiltFrom == generation) return _pageControlFieldRows;

            var rows = new List<PageControlFieldRow>();
            var sourceParsedPageIds = new HashSet<int>();

            // 1. Pages the runner source-compiled. BC's own emitted document wins whenever
            //    the emitter captured one (#3604) — it is what BC's provider reads, and it
            //    differs from the AL text on four columns; see
            //    RecordPatches.PageControlFieldFromBcDocument.cs. The AL derivation below
            //    remains reachable and is NOT dead: a page compiled before the object-metadata
            //    registry existed, or served from a cache written without it, has no document.
            foreach (var page in _parsedPages.Values)
            {
                sourceParsedPageIds.Add(page.Id);

                if (HasBcPageMetadataDocument(page.Id)
                    && GetPageControlFieldRowsFromBcDocument(page.Id) is { } fromDocument)
                {
                    rows.AddRange(fromDocument);
                    // #3750 — emitted here, after the document actually produced rows and
                    // before they are handed on, so a page whose document did not parse falls
                    // through to the derivation below and is traced as `derived` rather than
                    // claiming a route it did not take.
                    TracePageMetadataSource(page.Id, "bc-document");
                    continue;
                }

                // #3750 — the fall-through arm: no document was available for this page, OR
                // one was and did not parse. Those two are indistinguishable from here and from
                // the trace, because TryGetBcPageControlDocument memoises a null on a parse
                // failure; TracePageMetadataSource says so at length. (The table-side swallow
                // #3590 named is a different mechanism, narrowed by that issue's fix.)
                TracePageMetadataSource(page.Id, "derived");

                var tableId = GetSourceTableIdForPage(page.Id);
                var table = tableId != 0 && _parsedTables.TryGetValue(tableId, out var t) ? t : null;

                foreach (var c in GetSourceParsedPageControlRows(page.Id))
                {
                    var pField = table?.Fields.FirstOrDefault(f => NamesEqual(f.FieldName, c.FieldName));
                    rows.Add(new PageControlFieldRow(
                        page.Id, c.ControlId, c.ControlName,
                        pField != null ? tableId : 0, pField?.FieldId ?? 0,
                        c.EnabledExpr ?? "true",
                        // #3653 — the same solver the document path runs, for the same reason
                        // #3631 gave for Sequence: one column may not mean two different
                        // things depending on which path served the page, because AL cannot
                        // see which it got. `?? "true"` here was the lower-case attribute
                        // default; BC's SolveEditable produces "True"/"False".
                        SolveParsedControlEditable(c.EditableExpr, pField?.Editable),
                        c.VisibleExpr ?? "true",
                        c.SourceExpressionText, string.Empty, c.Sequence));
                }
            }

            // 2. Pages declared by precompiled dependency .app packages (source-compiled
            //    wins for the same page id — a symbol-derived page is skipped entirely).
            foreach (var symbol in EnumerateBcAppPageSymbols())
            {
                if (sourceParsedPageIds.Contains(symbol.Id)) continue;
                if (symbol.Controls == null || symbol.Controls.Count == 0) continue;

                // #3750 — a THIRD route value, which the table trace has no analogue for: a
                // precompiled dependency's page is served from its SymbolReference.json, not
                // from AL text and not from a document. Folding it into `derived` would make
                // the compiled-vs-precompiled split the trace exists to measure unreadable.
                TracePageMetadataSource(symbol.Id, "symbol");

                var symTable = symbol.SourceTableId != 0 && _parsedTables.TryGetValue(symbol.SourceTableId, out var st)
                    ? st : null;

                foreach (var c in symbol.Controls)
                {
                    var (tableNo, fieldNo) = ResolveDependencyControlField(c.SourceExpression, symbol.SourceTableId, symTable);
                    rows.Add(new PageControlFieldRow(
                        symbol.Id, c.Id, c.Name, tableNo, fieldNo,
                        c.EnabledExpr ?? "true",
                        // #3653, same statement as the source-parsed path above. The bound
                        // field comes from the dependency's own parsed table when
                        // ResolveDependencyControlField resolved one; an unresolved control
                        // takes SolveEditable's no-field arm and answers True, which is BC's
                        // `field?.Editable ?? true`.
                        SolveParsedControlEditable(c.EditableExpr,
                            fieldNo != 0
                                ? symTable?.Fields.FirstOrDefault(f => f.FieldId == fieldNo)?.Editable
                                : null),
                        c.VisibleExpr ?? "true",
                        // A dependency page's SymbolReference.json does not state the source
                        // field's option members, and DependencyPageMetadataXml.EmitPageXml
                        // reconstructs no <Controls> element to read them from, so this stays
                        // the empty string it answered before rather than becoming a guess.
                        c.SourceExpression, string.Empty, c.Sequence));
                }
            }

            _pageControlFieldRows = rows;
            _pageControlFieldRowsBuiltFrom = generation;
            return _pageControlFieldRows;
        }
    }

    /// <summary>
    /// Resolve a dependency page control's raw <c>SourceExpression</c> text
    /// (<c>Rec."No."</c>, <c>Rec.Name</c>, or anything else) to (TableNo, FieldNo). Only an
    /// expression of the exact shape <c>Rec.Field</c> / <c>Rec."Field Name"</c> resolves —
    /// same restriction the source-parsed path applies, for the same reason (a compound or
    /// non-Rec expression is not "bound to that field", so guessing would be a wrong
    /// answer). Field lookup uses the SOURCE-PARSED table when the runner compiled it
    /// itself (fields carry real ids there); a table known only from ANOTHER dependency's
    /// symbol is not consulted here since <c>_parsedTables</c> does not hold those, and
    /// inventing a field id from a name with no id-bearing source would be a guess.
    /// </summary>
    private static (int TableNo, int FieldNo) ResolveDependencyControlField(string sourceExpression, int sourceTableId, ParsedTable? table)
    {
        if (table == null || sourceTableId == 0) return (0, 0);
        var expr = sourceExpression?.Trim() ?? string.Empty;
        if (!expr.StartsWith("Rec.", StringComparison.OrdinalIgnoreCase)) return (0, 0);

        var fieldRef = expr.Substring(4).Trim();
        if (fieldRef.Length == 0) return (0, 0);
        // Reject anything beyond a bare field reference (an operator, another dot, a call) —
        // `Rec.Amount + 1` or `Rec.GetX()` is not "bound to that field".
        if (fieldRef.IndexOfAny(new[] { ' ', '+', '-', '*', '/', '(', '.' }) >= 0
            && !(fieldRef[0] == '"' && fieldRef[^1] == '"' && fieldRef.IndexOf('"', 1) == fieldRef.Length - 1))
            return (0, 0);

        var fieldName = Unquote(fieldRef);
        // GetAllFieldsIncludingExtensions, not table.Fields alone: a dependency page's control
        // can be bound to a field a tableextension added to this table (source-parsed here, in
        // a sibling app, or precompiled in another dependency .app) — see #2490.
        var field = GetAllFieldsIncludingExtensions(table).FirstOrDefault(f => NamesEqual(f.FieldName, fieldName));
        return field != null ? (sourceTableId, field.FieldId) : (0, 0);
    }
}
