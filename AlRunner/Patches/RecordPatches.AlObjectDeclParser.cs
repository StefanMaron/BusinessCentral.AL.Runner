// RecordPatches.AlObjectDeclParser — parses the AL object declarations that the
// existing per-kind parsers (table / page / report / query / xmlport) do NOT
// cover, purely for their (kind, id, name) tuple.
//
// WHY THIS EXISTS
//   The AllObj system virtual table (2000000038) must report every object the
//   runner knows about — including codeunits, enums and the *extension object
//   kinds, none of which had an (id, name) registry anywhere in the runner
//   (codeunits were only ever discovered lazily by CLR type-name convention
//   `Codeunit{id}`, which carries the id but not the AL name).
//
//   This parser is deliberately source-based rather than compiler-symbol based:
//   the emit pipeline's CaptureOutputter only fires on a compile-cache MISS, so
//   a registry fed from there would be empty on every warm run. `_sourceDirs`
//   is registered on every run, warm or cold.
//
//   Parsed from BC's own AL syntax tree (#1696). The old implementation anchored
//   each declaration regex to the start of a line so that a `Codeunit "X"` variable
//   declaration or a `Codeunit.Run(...)` call site could not be mistaken for an
//   object declaration; an object declaration is now a node, so that whole class of
//   confusion — along with declarations inside comments — cannot arise.
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Object kinds handled here — the ones NOT covered by the table/page/report/query/
    // xmlport parsers. AL kinds with no object id (interface, controladdin, profile) are
    // absent for the original reason: AllObj is keyed by (Object Type, Object ID) and a
    // synthetic id would be a fabrication. They are also, independently, the exact set that
    // does not derive from ApplicationObjectSyntax and so has no ObjectId to read.
    private static readonly HashSet<string> ObjectDeclKinds = new(StringComparer.Ordinal)
    {
        "Codeunit", "Enum", "EnumExtension", "PageExtension",
        "TableExtension", "PermissionSet", "PermissionSetExtension",
    };

    // (kind, id) → declaration. Keyed per kind because AL id namespaces are
    // per-object-type (codeunit 50100 and enum 50100 may coexist).
    private static readonly Dictionary<(string Kind, int Id), ParsedAlObjectDecl> _parsedObjectDecls = new();

    // Register()-time sweep folded into RecordPatches.ParseAllRegisteredSourceFiles (#1903)
    // — that shared loop calls TryParseObjectDeclFile alongside the other seven extractors,
    // one file read per file, instead of this file doing its own separate directory walk.

    private static void TryParseObjectDeclFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            // Kind comes from the node type, so the old worry about `enum` matching the
            // prefix of `enumextension` is structurally gone: they are distinct node types.
            if (AlObjectKindName(obj) is not string kind) continue;
            if (!ObjectDeclKinds.Contains(kind)) continue;
            if (ObjectIdOf(obj) is not int id) continue;
            var name = IdentText((obj as NavSyntax.ObjectSyntax)?.Name);
            _parsedObjectDecls[(kind, id)] = new ParsedAlObjectDecl(kind, id, name);
        }
    }

    /// <summary>Snapshot of every non-table/page/report/query/xmlport AL object declaration parsed from source.</summary>
    internal static IReadOnlyCollection<ParsedAlObjectDecl> ParsedObjectDecls => _parsedObjectDecls.Values;
}

internal record ParsedAlObjectDecl(string Kind, int Id, string Name);
