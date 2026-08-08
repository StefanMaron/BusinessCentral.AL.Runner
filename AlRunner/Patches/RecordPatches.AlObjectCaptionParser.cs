// RecordPatches.AlObjectCaptionParser — the Caption property of every AL object the
// runner compiles from source, keyed by (object kind, object id).
//
// WHY THIS EXISTS
//   The AllObjWithCaption system virtual table (2000000058) is AllObj plus one column:
//   Object Caption. Nothing else in the runner had it. The per-kind parsers each read
//   only what their own subsystem needs — the report parser picked up Caption, the
//   table / page / query / xmlport parsers did not, and the object-declaration parser
//   (which covers codeunits, enums and the *extension kinds) reads nothing but the
//   (kind, id, name) triple.
//
//   Rather than teach five parsers the same property, this one sweeps the same source
//   dirs for every AL object declaration of ANY kind and reads its top-level Caption.
//   Doing it in one place also means a kind added to AllObj later gets its caption for
//   free.
//
// WHAT "NO CAPTION" MEANS
//   A null entry here is "the AL source declares no Caption property", which is NOT the
//   same as "the caption is empty". AL's own default caption for an object is the object
//   name, and that is what a real service tier reports in AllObjWithCaption. The default
//   is applied by the consumer, not here, so the two cases stay distinguishable.
//
// LIMITS (same as every sibling parser)
//   Regex over raw text, not a parse tree. The declaration is anchored to the start of a
//   line and must be followed by its opening brace with nothing but the header between,
//   so neither a variable declaration nor prose inside a doc comment or a Label literal
//   can register an object — the trap that made the report parser fabricate a report 1306
//   named "against". Caption is read only at the object's OWN brace depth, so a caption
//   on a nested field / control / column never masquerades as the object's.
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Every AL object kind that carries an object id, and therefore can appear in
    // AllObjWithCaption. Kinds with no id (interface, controladdin, profile,
    // dotnet, entitlement) are excluded — AllObj is keyed on (Object Type, Object ID)
    // and a synthetic id would be a fabrication.
    private static readonly (string Kind, string Keyword)[] CaptionObjectKinds =
    {
        ("Table", "table"),
        ("TableExtension", "tableextension"),
        ("Page", "page"),
        ("PageExtension", "pageextension"),
        ("Report", "report"),
        ("ReportExtension", "reportextension"),
        ("Codeunit", "codeunit"),
        ("Query", "query"),
        ("QueryExtension", "queryextension"),
        ("XMLport", "xmlport"),
        ("Enum", "enum"),
        ("EnumExtension", "enumextension"),
        ("PermissionSet", "permissionset"),
        ("PermissionSetExtension", "permissionsetextension"),
    };

    // `<keyword> <id> <name>` followed by an optional `extends`/`implements` header and
    // then the object's opening brace. The header may span lines, so the tail allows
    // whitespace and bare identifiers/quoted names/commas/dots only — never arbitrary
    // text, which is what let prose match.
    private static readonly (string Kind, Regex Rx)[] RxCaptionObjectDecls =
        CaptionObjectKinds.Select(k => (k.Kind, new Regex(
            @"^\s*" + k.Keyword + @"\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))"
            + @"(?:\s+(?:extends|implements)\s+(?:""[^""]+""|[A-Za-z_][\w.]*)(?:\s*,\s*(?:""[^""]+""|[A-Za-z_][\w.]*))*)*"
            + @"\s*\{",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline))).ToArray();

    /// <summary>
    /// (kind, id) → the object's declared Caption, or null when it declares none. Keyed
    /// per kind because AL id namespaces are per-object-type (table 50100 and codeunit
    /// 50100 may coexist and carry different captions).
    /// </summary>
    private static readonly Dictionary<(string Kind, int Id), string?> _parsedObjectCaptions = new();

    private static void ParseAllObjectCaptionSources()
    {
        foreach (var dir in _sourceDirs)
            foreach (var file in Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories))
                TryParseObjectCaptionFile(File.ReadAllText(file));
    }

    private static void TryParseObjectCaptionFile(string text)
    {
        text = AlCommentBlanker.Blank(text); // see AlPageParser — same reason (#1690/#1697)

        foreach (var (kind, rx) in RxCaptionObjectDecls)
        {
            foreach (Match m in rx.Matches(text))
            {
                if (!int.TryParse(m.Groups[1].Value, out int id) || id <= 0) continue;
                // No keyword aliasing to worry about: `enum` cannot match the head of
                // `enumextension` because the pattern demands whitespace before the id
                // (same for table/page/report/query/permissionset and their *extension
                // counterparts).
                var body = ExtractObjectBody(text, m.Index + m.Length - 1);
                _parsedObjectCaptions[(kind, id)] = ReadTopLevelProperty(body, "Caption");
            }
        }
    }

    /// <summary>
    /// The Caption the AL source declares for this object, or null when it declares none
    /// (or the runner never saw its source). Callers apply AL's default — the object name
    /// — themselves.
    /// </summary>
    private static string? SourceCaptionFor(string kind, int id)
        => _parsedObjectCaptions.TryGetValue((kind, id), out var caption) ? caption : null;
}
