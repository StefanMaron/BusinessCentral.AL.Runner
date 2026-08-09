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
            _parsedPages[id] = new ParsedPage(id, IdentText(p.Name), IsExtension: false,
                // Absent SourceTable is the empty string, not null — callers distinguish
                // "declares none" from "never parsed" via IsPageParsed.
                SourceTableName: Unquote(PropValue(props, "SourceTable")?.ToString()?.Trim() ?? ""),
                ControlIdToFieldName: ParsePageFieldBindings(id, p.Layout),
                // AL's default when the property is absent is TRUE, so only an explicit
                // `false` flips it. Drives ITestPage.Creatable via NavTestPageBase.New().
                InsertAllowed: !PropIs(props, "InsertAllowed", "false"));
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
            _parsedPageExtensions[id] = new ParsedPage(id, IdentText(pe.Name), IsExtension: true,
                SourceTableName: string.Empty,
                ControlIdToFieldName: ParsePageFieldBindings(id, pe.Layout),
                InsertAllowed: !PropIs(pe.PropertyList, "InsertAllowed", "false"),
                BaseName: Unquote(pe.BaseObject?.ToString()?.Trim() ?? ""));
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

    internal static int[] GetPrimaryKeyFieldIdsForTable(int tableId)
        => _parsedTables.TryGetValue(tableId, out var table)
            ? table.PkFieldIds.ToArray()
            : Array.Empty<int>();

    /// <summary>
    /// Maps each <c>field(Control; Rec.Field)</c> control of a page's layout to the table field
    /// it binds, keyed by the control's member id.
    /// <para>Field controls are collected from the whole layout subtree at once, which covers
    /// arbitrary <c>area</c> / <c>group</c> / <c>cuegroup</c> / <c>repeater</c> nesting. Scoping
    /// to <c>Layout</c> also means the <c>actions</c> section cannot contribute (an action is a
    /// structurally different node), and a <c>part(...)</c> is a leaf here — the page it
    /// references is a separate object with its own tree, so its fields can never leak in.</para>
    /// <para>Only a source expression that is exactly <c>Rec.Something</c> counts. The old regex
    /// looked for the text <c>Rec.</c> anywhere after the semicolon, so
    /// <c>field(Total; Rec.Amount + 1)</c> bound the control to <c>Amount</c> — a control that is
    /// not bound to that field at all. A compound expression now yields no binding.</para>
    /// <para><paramref name="layout"/> is a <c>PageLayoutSyntax</c> for a base page and a
    /// <c>PageExtensionLayoutSyntax</c> for a pageextension — two unrelated node types (the
    /// latter derives straight from SyntaxNode and holds ControlChangeBaseSyntax entries), but
    /// the field controls under both are the SAME <c>PageFieldSyntax</c>, so one subtree walk
    /// serves both. <c>modify(...)</c> declares no control and contributes nothing;
    /// <c>movefirst</c>/<c>moveafter</c> only reference controls by name.</para>
    /// <para><paramref name="declaringObjectId"/> is the object the controls are DECLARED in —
    /// the page for a base layout, the PAGEEXTENSION for controls it adds. That is what BC's
    /// IdSpace.GetMemberId hashes; see GetPageControlFieldMap for the live evidence.</para>
    /// </summary>
    private static Dictionary<int, string> ParsePageFieldBindings(
        int declaringObjectId, SyntaxNode? layout)
    {
        var result = new Dictionary<int, string>();
        if (layout == null) return result;

        foreach (var field in layout.DescendantNodes().OfType<NavSyntax.PageFieldSyntax>())
        {
            if (field.Expression is not NavSyntax.MemberAccessExpressionSyntax access) continue;
            if (access.Expression is not NavSyntax.IdentifierNameSyntax receiver) continue;
            if (!string.Equals(Unquote(receiver.Identifier.ValueText ?? ""), "Rec",
                    StringComparison.OrdinalIgnoreCase)) continue;

            var controlName = IdentText(field.Name);
            var fieldName = IdentText(access.Name as NavSyntax.IdentifierNameSyntax);
            if (controlName.Length == 0 || fieldName.Length == 0) continue;
            result[IdSpace.GetMemberId(declaringObjectId, controlName)] = fieldName;
        }

        return result;
    }

    private static bool NamesEqual(string left, string right)
        => string.Equals(left.Replace(" ", ""), right.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}

internal record ParsedPage(
    int Id,
    string Name,
    bool IsExtension,
    string SourceTableName,
    IReadOnlyDictionary<int, string> ControlIdToFieldName,
    bool InsertAllowed = true,
    /// <summary>The object a pageextension extends; empty for a plain page.</summary>
    string BaseName = "");
