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

        // Pages first, then pageextensions — deliberately, because `_parsedPages` is keyed on
        // the object id ALONE. AL gives `page` and `pageextension` separate id namespaces, so
        // a page 50100 and a pageextension 50100 may both exist and the second one written
        // wins. That is pre-existing behaviour (the report parser hit the same problem and was
        // given two dictionaries; this one never was), and preserving the write order keeps
        // this migration behaviour-preserving rather than quietly changing which entry
        // survives. Splitting the dictionary is the real fix and belongs in its own change.
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
            // Extensions carry no source table and no control map, exactly as before. Their
            // addfirst/addlast field controls ARE reachable now (same PageFieldSyntax type
            // under PageExtensionLayoutSyntax), so populating the map is newly possible — but
            // it would change what GetPageControlFieldMap returns for every pageextension-backed
            // TestPage, which needs its own test rather than riding along on a parser swap.
            _parsedPages[id] = new ParsedPage(id, IdentText(pe.Name), IsExtension: true,
                SourceTableName: string.Empty, ControlIdToFieldName: new Dictionary<int, string>(),
                InsertAllowed: !PropIs(pe.PropertyList, "InsertAllowed", "false"));
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
    /// Whether the AL source parser has seen this page at all. Lets callers tell
    /// "the page genuinely declares no SourceTable" (BC's SourceTable==0 case, a legal
    /// AL page) apart from "we never parsed this page", which is a runner gap and must
    /// be reported loudly rather than answered with a default.
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

    internal static IReadOnlyDictionary<int, int> GetPageControlFieldMap(int pageId)
    {
        if (!_parsedPages.TryGetValue(pageId, out var page) || string.IsNullOrWhiteSpace(page.SourceTableName))
            return new Dictionary<int, int>();

        var table = _parsedTables.Values.FirstOrDefault(t => NamesEqual(t.TableName, page.SourceTableName));
        if (table == null) return new Dictionary<int, int>();

        var result = new Dictionary<int, int>();
        foreach (var kvp in page.ControlIdToFieldName)
        {
            var field = table.Fields.FirstOrDefault(f => NamesEqual(f.FieldName, kvp.Value));
            if (field != null) result[kvp.Key] = field.FieldId;
        }
        return result;
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
    /// </summary>
    private static Dictionary<int, string> ParsePageFieldBindings(
        int pageId, NavSyntax.PageLayoutSyntax? layout)
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
            result[IdSpace.GetMemberId(pageId, controlName)] = fieldName;
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
    bool InsertAllowed = true);
