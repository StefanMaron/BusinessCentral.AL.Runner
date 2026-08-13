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
    internal const int PageControlFieldVirtualTableId = 2000000192;

    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(int PageNo, int ControlId), byte>> _pcfvPopulatedByProvider = new();

    private static bool IsPageControlFieldVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == PageControlFieldVirtualTableId;

    /// <summary>One field control as Page Control Field exposes it.</summary>
    private sealed record PageControlFieldRow(
        int PageNo, int ControlId, string ControlName, int TableNo, int FieldNo,
        string Enabled, string Editable, string Visible, string SourceExpression, int Sequence);

    private static List<PageControlFieldRow>? _pageControlFieldRows;
    private static (int Apps, int Parsed) _pageControlFieldRowsBuiltFrom = (-1, -1);
    private static readonly object _pageControlFieldRowsLock = new();

    private static void PopulatePageControlFieldVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Page Control Field (virtual table 2000000192)",
                "page-control-field-virtual-table — data access has no in-memory provider; see docs/scope.md");

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
            "sequence" => Int(row.Sequence),
            // "OptionString" is derived from the source field's OptionMembers on a real
            // tier; the runner does not resolve that here, so it gets the type default
            // rather than a guess.
            _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
        };
    }

    private static List<PageControlFieldRow> EnumerateKnownPageControlFields()
    {
        var generation = (_bcAppPaths.Count, _parsedPages.Count);
        if (_pageControlFieldRows != null && _pageControlFieldRowsBuiltFrom == generation) return _pageControlFieldRows;
        lock (_pageControlFieldRowsLock)
        {
            generation = (_bcAppPaths.Count, _parsedPages.Count);
            if (_pageControlFieldRows != null && _pageControlFieldRowsBuiltFrom == generation) return _pageControlFieldRows;

            var rows = new List<PageControlFieldRow>();
            var sourceParsedPageIds = new HashSet<int>();

            // 1. Pages the runner source-compiled (base page controls merged with matching
            //    pageextensions' — see GetSourceParsedPageControlRows).
            foreach (var page in _parsedPages.Values)
            {
                sourceParsedPageIds.Add(page.Id);
                var tableId = GetSourceTableIdForPage(page.Id);
                var table = tableId != 0 && _parsedTables.TryGetValue(tableId, out var t) ? t : null;

                foreach (var c in GetSourceParsedPageControlRows(page.Id))
                {
                    var pField = table?.Fields.FirstOrDefault(f => NamesEqual(f.FieldName, c.FieldName));
                    rows.Add(new PageControlFieldRow(
                        page.Id, c.ControlId, c.ControlName,
                        pField != null ? tableId : 0, pField?.FieldId ?? 0,
                        c.EnabledExpr ?? "true", c.EditableExpr ?? "true", c.VisibleExpr ?? "true",
                        c.SourceExpressionText, c.Sequence));
                }
            }

            // 2. Pages declared by precompiled dependency .app packages (source-compiled
            //    wins for the same page id — a symbol-derived page is skipped entirely).
            foreach (var symbol in EnumerateBcAppPageSymbols())
            {
                if (sourceParsedPageIds.Contains(symbol.Id)) continue;
                if (symbol.Controls == null || symbol.Controls.Count == 0) continue;

                var symTable = symbol.SourceTableId != 0 && _parsedTables.TryGetValue(symbol.SourceTableId, out var st)
                    ? st : null;

                foreach (var c in symbol.Controls)
                {
                    var (tableNo, fieldNo) = ResolveDependencyControlField(c.SourceExpression, symbol.SourceTableId, symTable);
                    rows.Add(new PageControlFieldRow(
                        symbol.Id, c.Id, c.Name, tableNo, fieldNo,
                        c.EnabledExpr ?? "true", c.EditableExpr ?? "true", c.VisibleExpr ?? "true",
                        c.SourceExpression, c.Sequence));
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
        var field = table.Fields.FirstOrDefault(f => NamesEqual(f.FieldName, fieldName));
        return field != null ? (sourceTableId, field.FieldId) : (0, 0);
    }
}
