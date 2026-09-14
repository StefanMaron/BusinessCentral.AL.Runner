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

    // (kind, id) → the app.json id of the app whose source declares it (#4000). Enums,
    // enumextensions, permission sets and permissionsetextensions emit no CLR type, so the
    // AllObj owner index cannot find their owner in an emitted assembly; this is where it
    // reads it instead. Absent when the file has no path or no app.json above it.
    private static readonly Dictionary<(string Kind, int Id), Guid> _parsedObjectDeclOwners = new();

    // Register()-time sweep folded into RecordPatches.ParseAllRegisteredSourceFiles (#1903)
    // — that shared loop calls TryParseObjectDeclFile alongside the other seven extractors,
    // one file read per file, instead of this file doing its own separate directory walk.

    private static void TryParseObjectDeclFile(string text, string? filePath = null)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            // Kind comes from the node type, so the old worry about `enum` matching the
            // prefix of `enumextension` is structurally gone: they are distinct node types.
            if (AlObjectKindName(obj) is not string kind) continue;
            if (!ObjectDeclKinds.Contains(kind)) continue;
            if (ObjectIdOf(obj) is not int id) continue;
            var name = IdentText((obj as NavSyntax.ObjectSyntax)?.Name);
            if (filePath != null && ResolveOwningApp(filePath) is { } owner)
                _parsedObjectDeclOwners[(kind, id)] = owner.AppId;
            // Codeunits additionally carry the three object-level properties the
            // CodeUnit Metadata virtual table (2000000137) reports as real columns —
            // see RecordPatches.CodeunitMetadataVirtualTable.cs. Every other kind here
            // is still only its (kind, id, name) tuple.
            if (obj is NavSyntax.CodeunitSyntax cu)
            {
                var props = cu.PropertyList;
                _parsedObjectDecls[(kind, id)] = new ParsedAlObjectDecl(
                    kind, id, name,
                    // As WRITTEN: a bare id, or a table name that may be namespace-
                    // qualified. Resolving it needs the run's table inventory, which is
                    // not built yet at parse time, so the consumer resolves it.
                    TableNo: PropValue(props, "TableNo") is { } t
                        ? LastNameSegment(t.ToString()?.Trim())
                        : null,
                    // AL's default is false; only an explicit `= true` sets it.
                    SingleInstance: PropIs(props, "SingleInstance", "true"),
                    // AL's default is Normal when the codeunit declares no Subtype.
                    // Unquoted: AL accepts `Subtype = "Install";` for `Subtype = Install;`,
                    // and the compiler erases the difference before anything downstream sees
                    // it — measured in the .app SymbolReference.json, which carries the bare
                    // name for both spellings, so leaving the quotes on would make this source
                    // path disagree with BcAppSymbolCache about the same AL text (#3536).
                    Subtype: DeclaredIdentifierText(PropValue(props, "Subtype")),
                    // #2547. BC's AL->C# emit does not put this property on the generated
                    // type at all (measured: the emitted assembly carries NavCodeunitOptions
                    // and NavTestAttribute, and no trace of TestHttpRequestPolicy), and
                    // NCLMetaCodeunit.LoadOptionsFromAttributeOrInstance reads only TableId
                    // and Subtype off NavCodeunitOptionsAttribute. On a real service tier it
                    // arrives through the XML object metadata the runner never loads for AL it
                    // compiles itself, so source is the only place it exists here.
                    // Null means "declares none", which the consumer reads as AL's default.
                    TestHttpRequestPolicy: DeclaredIdentifierText(PropValue(props, "TestHttpRequestPolicy")));
                continue;
            }
            // All five *extension kinds AL declares here derive from one syntax base carrying
            // `BaseObject`, so the target is read once rather than per kind — a new extension
            // kind is picked up without a further arm. AS WRITTEN: the id it names is resolved
            // against the run's object inventory, which does not exist yet at parse time.
            _parsedObjectDecls[(kind, id)] = new ParsedAlObjectDecl(kind, id, name,
                BaseObjectName: obj is NavSyntax.ApplicationObjectExtensionSyntax ext
                    ? LastNameSegment(ext.BaseObject?.ToString()?.Trim())
                    : null);
        }
    }

    /// <summary>
    /// The AL text of an identifier-valued object property, with any quoting removed —
    /// `Subtype = "Install";` and `Subtype = Install;` state the same subtype, so they must
    /// reach the consumer as the same string. `TableNo` needs no call here because
    /// <c>LastNameSegment</c> already unquotes; the page parser makes the same call for
    /// <c>PageType</c> and <c>SourceTable</c>.
    /// </summary>
    private static string? DeclaredIdentifierText(NavSyntax.PropertyValueSyntax? value)
        => value?.ToString()?.Trim() is { } text ? Unquote(text) : null;

    /// <summary>Snapshot of every non-table/page/report/query/xmlport AL object declaration parsed from source.</summary>
    internal static IReadOnlyCollection<ParsedAlObjectDecl> ParsedObjectDecls => _parsedObjectDecls.Values;

    /// <summary>The declaring app of each source-parsed declaration whose file has an app.json (#4000).</summary>
    internal static IReadOnlyDictionary<(string Kind, int Id), Guid> ParsedObjectDeclOwners => _parsedObjectDeclOwners;

    /// <summary>
    /// The codeunit's declared <c>TestHttpRequestPolicy</c> as AL text, or null when it declares
    /// none (or was never parsed). #2547 — see the parse site above for why source is the only
    /// place this property exists in this runner.
    /// </summary>
    internal static string? TryGetParsedTestHttpRequestPolicy(int codeunitId)
        => _parsedObjectDecls.TryGetValue(("Codeunit", codeunitId), out var d)
            ? (string.IsNullOrEmpty(d.TestHttpRequestPolicy) ? null : d.TestHttpRequestPolicy)
            : null;
}

/// <summary>
/// One AL object declaration parsed from source. <paramref name="TableNo"/>,
/// <paramref name="SingleInstance"/>, <paramref name="Subtype"/> and
/// <paramref name="TestHttpRequestPolicy"/> are only populated for <c>Codeunit</c>; every
/// other kind leaves them at their defaults, which is also what a codeunit declaring none of
/// them means. <paramref name="TableNo"/> is the reference AS WRITTEN (a bare id in text form,
/// or a table name) — resolving it to an id needs the run's table inventory, which does not
/// exist yet at parse time.
/// <para><paramref name="BaseObjectName"/> is the `extends` target of an *extension kind, also
/// as WRITTEN and for the same reason; null for a non-extension. See
/// docs/virtual-tables-allobj.md#object-subtype for what reads it.</para>
/// </summary>
internal record ParsedAlObjectDecl(
    string Kind, int Id, string Name,
    string? TableNo = null, bool SingleInstance = false, string? Subtype = null,
    string? TestHttpRequestPolicy = null, string? BaseObjectName = null);
