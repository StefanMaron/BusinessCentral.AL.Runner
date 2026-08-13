// RecordPatches.AlPageParser — parses AL `page` / `pageextension` declarations
// into ParsedPage records keyed by page ID. Mirror of AlSourceParser for tables.
//
// We only need the (id, name, base-id-for-extensions) tuple — the cache slot
// just has to be non-null so NCLMetadata.GetMetaApplicationObjectInternal
// finds an entry. Field/action/group layout is irrelevant: every page-level
// property getter on NCLMetaForm reads `metadataAppGroupPageDefinition.Item`
// which is a default struct on a hand-built skeleton; those getters aren't
// reached by the metadata lookup path itself.
// Parsed from BC's own AL syntax tree (#1696). The old implementation guessed each object's
// extent with SliceObjectText, which scanned forward for the next `page|table|codeunit|…`
// keyword — a list that omitted `enum`, `interface`, `controladdin`, `permissionset` and
// friends, so any of those following a page put the NEXT object's body inside this page's
// slice, where SourceTable / InsertAllowed / field(...) could all match against it. Object
// extent is now structural.
//
// PageType / per-control Visible/Editable/Enabled/SourceExpression are ALSO captured now
// (issues #1769 / #1779) — they feed the "Page Metadata" (2000000138) and "Page Control
// Field" (2000000192) virtual tables. See RecordPatches.PageMetadataVirtualTable.cs and
// RecordPatches.PageControlFieldVirtualTable.cs.
using Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static void ParseAllPageSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParsePageFile(File.ReadAllText(file));
        }
    }

    private static void TryParsePageFile(string text)
    {
        var objects = ParseAlObjects(text);

        // Pages and pageextensions go into SEPARATE dictionaries, mirroring
        // _parsedReports / _parsedReportExtensions. AL gives `page` and `pageextension`
        // separate id namespaces, so a page 50100 and a pageextension 50100 may both exist —
        // and while they shared one dictionary the extension (written second) won, bringing
        // SourceTableName = "" and an empty control map with it. The real page's source table
        // and every one of its control→field bindings vanished silently: GetSourceTableIdForPage
        // answered 0 and GetPageControlFieldMap answered empty (#1710). Write order therefore
        // no longer carries any meaning here.
        foreach (var obj in objects)
        {
            if (obj is not NavSyntax.PageSyntax p) continue;
            if (ObjectIdOf(p) is not int id) continue;
            var props = p.PropertyList;
            var (fieldMap, controls) = ParsePageControls(id, p.Layout);
            var pageTypeText = Unquote(PropValue(props, "PageType")?.ToString()?.Trim() ?? "");
            _parsedPages[id] = new ParsedPage(id, IdentText(p.Name), IsExtension: false,
                // Absent SourceTable is the empty string, not null — callers distinguish
                // "declares none" from "never parsed" via IsPageParsed.
                SourceTableName: Unquote(PropValue(props, "SourceTable")?.ToString()?.Trim() ?? ""),
                ControlIdToFieldName: fieldMap,
                // AL's default when the property is absent is TRUE, so only an explicit
                // `false` flips it. Drives ITestPage.Creatable via NavTestPageBase.New().
                InsertAllowed: !PropIs(props, "InsertAllowed", "false"),
                // AL's default is false — only an explicit `true` flips it. See issue #1719:
                // a page-variable's Rec must be built temporary when this is true, or its
                // own AL body's Rec.Copy(source, shareTable: true) refuses ("both records
                // must be temporary").
                SourceTableTemporary: PropIs(props, "SourceTableTemporary", "true"),
                // MS docs: PageType defaults to Card when the property is absent.
                PageType: pageTypeText.Length > 0 ? pageTypeText : "Card",
                Editable: !PropIs(props, "Editable", "false"),
                ModifyAllowed: !PropIs(props, "ModifyAllowed", "false"),
                DeleteAllowed: !PropIs(props, "DeleteAllowed", "false"),
                Controls: controls,
                // Page reference stated BY NAME, resolved against the run's own page
                // inventory at Page Metadata row-build time — same deferred-resolution rule
                // Table Metadata uses for LookupPageId/DrillDownPageId. Null means "declares
                // none", which Page Metadata reports as CardPageID = 0 (a real, meaningful
                // value: Base App "Page Management".GetDefaultCardPageID reads it to decide
                // whether a table has a card page at all).
                CardPageName: PageRefText(PropValue(props, "CardPageId")));
        }

        foreach (var obj in objects)
        {
            if (obj is not NavSyntax.PageExtensionSyntax pe) continue;
            if (ObjectIdOf(pe) is not int id) continue;
            // An extension has no source table of its own — it inherits the base page's — but it
            // DOES declare field controls, via addfirst/addlast. Those are PageFieldSyntax nodes
            // exactly like a base page's, just hanging off PageExtensionLayoutSyntax, and they
            // used to be dropped on the floor (#1711): every pageextension stored an empty map,
            // so a TestPage driven through an extension-added control could not resolve it.
            // GetPageControlFieldMap merges them into the BASE page's map, which is where a
            // TestPage looks.
            var (extFieldMap, extControls) = ParsePageControls(id, pe.Layout);
            _parsedPageExtensions[id] = new ParsedPage(id, IdentText(pe.Name), IsExtension: true,
                SourceTableName: string.Empty,
                ControlIdToFieldName: extFieldMap,
                InsertAllowed: !PropIs(pe.PropertyList, "InsertAllowed", "false"),
                BaseName: Unquote(pe.BaseObject?.ToString()?.Trim() ?? ""),
                Controls: extControls);
        }
    }

    /// <summary>
    /// Whether the page permits inserts (AL's <c>InsertAllowed</c>, default TRUE when the
    /// property is absent). Drives ITestPage.Creatable, which BC's NavTestPageBase.New()
    /// checks before inserting. Unknown pages default to true — same as AL.
    /// </summary>
    internal static bool GetInsertAllowedForPage(int pageId)
        => !_parsedPages.TryGetValue(pageId, out var page) || page.InsertAllowed;

    /// <summary>
    /// Whether the AL source parser has seen this PAGE at all. Lets callers tell
    /// "the page genuinely declares no SourceTable" (BC's SourceTable==0 case, a legal
    /// AL page) apart from "we never parsed this page", which is a runner gap and must
    /// be reported loudly rather than answered with a default.
    /// <para>A pageextension of the same number is deliberately NOT an answer here: it is a
    /// different object in a different id namespace, and letting one stand in for a page is
    /// what #1710 was.</para>
    /// </summary>
    internal static bool IsPageParsed(int pageId) => _parsedPages.ContainsKey(pageId);

    /// <summary>
    /// Whether a parsed page declares a SourceTable in AL. False for a parsed page with
    /// no SourceTable property (BC returns a null NCLMetaTable for those).
    /// </summary>
    internal static bool PageDeclaresSourceTable(int pageId)
        => _parsedPages.TryGetValue(pageId, out var page)
           && !string.IsNullOrWhiteSpace(page.SourceTableName);

    internal static int GetSourceTableIdForPage(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var page) || string.IsNullOrWhiteSpace(page.SourceTableName))
            return 0;

        foreach (var table in _parsedTables.Values)
            if (NamesEqual(table.TableName, page.SourceTableName))
                return table.TableId;

        return 0;
    }

    /// <summary>
    /// Control id → source-table field number for every field control on the page, INCLUDING
    /// the ones contributed by pageextensions that extend it.
    /// <para>An extension's controls are keyed in the EXTENSION's own id space, because BC's
    /// IdSpace.GetMemberId hashes the id of the object the member is DECLARED in. Verified,
    /// not assumed: a bundle with `page 64300 "PXP Card"` and `pageextension 64301` adding
    /// `field(NoteField; Rec."Note")` made BC ask LiveNavTestPage.GetField for control
    /// 788108655 == GetMemberId(64301, "NoteField"); GetMemberId(64300, "NoteField") is
    /// 321499490 and never appears.</para>
    /// </summary>
    internal static IReadOnlyDictionary<int, int> GetPageControlFieldMap(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var page) || string.IsNullOrWhiteSpace(page.SourceTableName))
            return new Dictionary<int, int>();

        var table = _parsedTables.Values.FirstOrDefault(t => NamesEqual(t.TableName, page.SourceTableName));
        if (table == null) return new Dictionary<int, int>();

        var result = new Dictionary<int, int>();
        BindControls(page.ControlIdToFieldName);
        // Only extensions of THIS page. Binding every extension's controls onto every page
        // would fabricate bindings that the AL never declared.
        foreach (var ext in _parsedPageExtensions.Values)
            if (NamesEqual(ext.BaseName, page.Name))
                BindControls(ext.ControlIdToFieldName);
        return result;

        void BindControls(IReadOnlyDictionary<int, string> controls)
        {
            foreach (var kvp in controls)
            {
                var field = table.Fields.FirstOrDefault(f => NamesEqual(f.FieldName, kvp.Value));
                if (field != null) result[kvp.Key] = field.FieldId;
            }
        }
    }

    /// <summary>
    /// Every field control of a SOURCE-PARSED page, base plus matching pageextensions,
    /// for the "Page Control Field" (2000000192) virtual table. Same base+extension merge
    /// rule as <see cref="GetPageControlFieldMap"/> (only extensions of THIS page), same
    /// Rec.-bound-only scope as <see cref="ParsePageControls"/> — see that method's remarks.
    /// <para>Sequence is assigned here, at merge time, 1-based in enumeration order (base
    /// page controls first, then each matching extension's, in registration order) — never
    /// trusted from the per-object parse pass, since a base page and an extension each start
    /// their own local layout walk at 1 and merging them naively would produce duplicate
    /// Sequence values.</para>
    /// </summary>
    internal static List<PageControlRow> GetSourceParsedPageControlRows(int pageId)
    {
        var result = new List<PageControlRow>();
        if (!_parsedPages.TryGetValue(pageId, out var page)) return result;

        int seq = 0;
        void AddAll(IReadOnlyList<PageControlRow> controls)
        {
            foreach (var c in controls)
                result.Add(c with { Sequence = ++seq });
        }

        AddAll(page.Controls);
        foreach (var ext in _parsedPageExtensions.Values)
            if (NamesEqual(ext.BaseName, page.Name))
                AddAll(ext.Controls);
        return result;
    }

    internal static int[] GetPrimaryKeyFieldIdsForTable(int tableId)
        => _parsedTables.TryGetValue(tableId, out var table)
            ? table.PkFieldIds.ToArray()
            : Array.Empty<int>();

    /// <summary>
    /// Every field control of one page layout (a base page's <c>layout</c> or a
    /// pageextension's <c>layout</c>/<c>addfirst</c>/<c>addlast</c> block), plus the
    /// Rec.-bound subset of them as a control-id → field-name map (for
    /// <see cref="GetPageControlFieldMap"/>, unchanged from before this method existed).
    /// <para>Field controls are collected from the whole layout subtree at once, which covers
    /// arbitrary <c>area</c> / <c>group</c> / <c>cuegroup</c> / <c>repeater</c> nesting. Scoping
    /// to <c>Layout</c> also means the <c>actions</c> section cannot contribute (an action is a
    /// structurally different node), and a <c>part(...)</c> is a leaf here — the page it
    /// references is a separate object with its own tree, so its fields can never leak in.</para>
    /// <para><b>Scope limitation, deliberate:</b> a control only becomes a
    /// <see cref="PageControlRow"/> when its source expression is exactly <c>Rec.Something</c>.
    /// A field control bound to anything else (a compound expression, a local/global variable)
    /// is omitted entirely rather than guessed at — same "omit, never fabricate" rule the
    /// Table/Report Metadata providers use for a page/table/report they cannot resolve.
    /// <c>modify(...)</c> property overrides on an inherited control (pageextension) are NOT
    /// applied here either: the row reflects the control's own declaring object, not any
    /// extension that later modifies its Visible/Editable. Real BC would show the overridden
    /// value; this is a known, narrower gap than what existed before (no rows at all).</para>
    /// <para><paramref name="declaringObjectId"/> is the object the controls are DECLARED in —
    /// the page for a base layout, the PAGEEXTENSION for controls it adds. That is what BC's
    /// IdSpace.GetMemberId hashes; see GetPageControlFieldMap for the live evidence.</para>
    /// </summary>
    private static (Dictionary<int, string> FieldMap, List<PageControlRow> Controls) ParsePageControls(
        int declaringObjectId, SyntaxNode? layout)
    {
        var fieldMap = new Dictionary<int, string>();
        var controls = new List<PageControlRow>();
        if (layout == null) return (fieldMap, controls);

        int seq = 0;
        foreach (var field in layout.DescendantNodes().OfType<NavSyntax.PageFieldSyntax>())
        {
            var controlName = IdentText(field.Name);
            if (controlName.Length == 0) continue;

            string fieldName = string.Empty;
            if (field.Expression is NavSyntax.MemberAccessExpressionSyntax access
                && access.Expression is NavSyntax.IdentifierNameSyntax receiver
                && string.Equals(Unquote(receiver.Identifier.ValueText ?? ""), "Rec",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Only a source expression that is exactly Rec.Something counts. The old regex
                // looked for the text "Rec." anywhere after the semicolon, so
                // `field(Total; Rec.Amount + 1)` bound the control to Amount — a control that
                // is not bound to that field at all. A compound expression yields no binding.
                fieldName = IdentText(access.Name as NavSyntax.IdentifierNameSyntax);
            }
            if (fieldName.Length == 0) continue;   // scope limitation — see remarks above

            var controlId = IdSpace.GetMemberId(declaringObjectId, controlName);
            fieldMap[controlId] = fieldName;

            seq++;
            controls.Add(new PageControlRow(
                controlId, controlName, fieldName,
                SourceExpressionText: field.Expression?.ToString()?.Trim() ?? string.Empty,
                VisibleExpr: PropValue(field.PropertyList, "Visible")?.ToString()?.Trim(),
                EditableExpr: PropValue(field.PropertyList, "Editable")?.ToString()?.Trim(),
                EnabledExpr: PropValue(field.PropertyList, "Enabled")?.ToString()?.Trim(),
                Sequence: seq));
        }

        return (fieldMap, controls);
    }

    private static bool NamesEqual(string left, string right)
        => string.Equals(left.Replace(" ", ""), right.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One field control resolved from a page's (or pageextension's) layout — the Rec.-bound
/// subset only, see <see cref="RecordPatches"/>.ParsePageControls remarks. Feeds the
/// "Page Control Field" (2000000192) virtual table.
/// </summary>
internal sealed record PageControlRow(
    int ControlId,
    string ControlName,
    string FieldName,
    string SourceExpressionText,
    string? VisibleExpr,
    string? EditableExpr,
    string? EnabledExpr,
    int Sequence);

internal record ParsedPage(
    int Id,
    string Name,
    bool IsExtension,
    string SourceTableName,
    IReadOnlyDictionary<int, string> ControlIdToFieldName,
    bool InsertAllowed = true,
    /// <summary>The object a pageextension extends; empty for a plain page.</summary>
    string BaseName = "",
    /// <summary>AL's <c>SourceTableTemporary</c> property; see issue #1719.</summary>
    bool SourceTableTemporary = false,
    /// <summary>AL's <c>PageType</c> property; MS docs default is "Card". Feeds the
    /// "Page Metadata" (2000000138) virtual table (#1769).</summary>
    string PageType = "Card",
    bool Editable = true,
    bool ModifyAllowed = true,
    bool DeleteAllowed = true,
    /// <summary>Rec.-bound field controls of this page's OWN layout (excludes extensions);
    /// see <see cref="RecordPatches"/>.GetSourceParsedPageControlRows for the merged view.</summary>
    IReadOnlyList<PageControlRow>? Controls = null,
    /// <summary>AL's <c>CardPageId</c> property, as the last name segment of the page
    /// reference (unresolved — see <see cref="RecordPatches"/>.PageMetadataVirtualTable.cs).
    /// Null when the page declares none.</summary>
    string? CardPageName = null)
{
    // Positional records can't give a collection parameter a literal default that isn't a
    // constant, so a null Controls (constructed via the shorter historical call sites/tests,
    // if any ever appear) is normalized to empty rather than NRE-ing every consumer.
    public IReadOnlyList<PageControlRow> Controls { get; init; } = Controls ?? Array.Empty<PageControlRow>();
}
