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
//   dirs for every AL object declaration and reads its top-level Caption. Doing it in
//   one place also means a kind added to AllObj later gets its caption for free.
//
//   REPORTS ARE THE ONE EXCEPTION (#1714). AlReportParser already holds the report's own
//   PropertyList — it reads ProcessingOnly and UseRequestPage off it — and takes Caption
//   from there into ParsedReport.Caption, which the Report Metadata virtual table consumes
//   directly. Recording a second, independently-walked copy here produced a duplicated fact
//   with nothing enforcing that the two agreed, and this copy was read by nobody.
//   SourceCaptionFor below answers "Report" out of ParsedReport instead: one accessor for
//   every kind, over exactly one read of the fact.
//
// WHAT "NO CAPTION" MEANS
//   A null entry here is "the AL source declares no Caption property", which is NOT the
//   same as "the caption is empty". AL's own default caption for an object is the object
//   name, and that is what a real service tier reports in AllObjWithCaption. The default
//   is applied by the consumer, not here, so the two cases stay distinguishable.
//
// Parsed from BC's own AL syntax tree (#1696), so an object declaration is a node: neither a
// variable declaration, nor prose inside a doc comment or a Label literal, can register an
// object — the trap that made the report parser fabricate a report 1306 named "against".
// Caption comes off the object's own property list, so a caption on a nested field / control /
// column cannot masquerade as the object's.
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Every AL object kind that carries an object id, and therefore can appear in
    // AllObjWithCaption. Kinds with no id (interface, controladdin, profile, dotnet,
    // entitlement) are excluded — AllObj is keyed on (Object Type, Object ID) and a synthetic
    // id would be a fabrication. They are also, independently, the exact set with no ObjectId
    // on the syntax node, so the generic walk skips them by construction.
    //
    // Handled generically off the syntax
    // tree (see AlObjectKindName). Two notes on what changed with the tree:
    //   * `QueryExtension` is gone from the set. AL has no `queryextension` keyword — text
    //     saying so is a parse error, not an object — so the old entry could only ever have
    //     produced a caption for source that does not compile.
    //   * The old brace-walk (ExtractObjectBody/DepthAt) counted `{` and `}` WITHOUT string
    //     awareness, so a caption containing a literal brace (`Caption = 'Config {Beta}';`)
    //     desynchronised the depth counter for every object after it in the file. The tree
    //     has no such failure mode. This does mean captions that were previously corrupted
    //     by a stray brace now read correctly — a fix, not a regression, but a real
    //     behaviour change worth knowing about.

    /// <summary>
    /// (kind, id) → the object's declared Caption, or null when it declares none. Keyed
    /// per kind because AL id namespaces are per-object-type (table 50100 and codeunit
    /// 50100 may coexist and carry different captions).
    /// </summary>
    private static readonly Dictionary<(string Kind, int Id), string?> _parsedObjectCaptions = new();

    // Register()-time sweep folded into RecordPatches.ParseAllRegisteredSourceFiles (#1903)
    // — that shared loop calls TryParseObjectCaptionFile alongside the other seven
    // extractors, one file read per file, instead of this file doing its own separate
    // directory walk.

    private static void TryParseObjectCaptionFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (AlObjectKindName(obj) is not string kind) continue;
            // Reports are AlReportParser's (#1714) — see the header. Skipping the kind here
            // rather than filtering the walk keeps every OTHER kind generic: a kind added to
            // AlObjectKindName later still gets its caption for free.
            if (kind == "Report") continue;
            // id <= 0 stays rejected, as before.
            if (ObjectIdOf(obj) is not int id || id <= 0) continue;
            // The object's OWN Caption: a caption on a nested field/control/column belongs to
            // that node's property list, not this one, so the old brace-depth gate is now
            // structural rather than arithmetic.
            var props = (obj as NavSyntax.ObjectSyntax)?.PropertyList;
            _parsedObjectCaptions[(kind, id)] = PropertyTextFrom(PropValue(props, "Caption"));
        }
    }

    /// <summary>
    /// The Caption the AL source declares for this object, or null when it declares none
    /// (or the runner never saw its source). Callers apply AL's default — the object name
    /// — themselves.
    ///
    /// Reports are answered out of <see cref="ParsedReport.Caption"/> rather than
    /// <see cref="_parsedObjectCaptions"/>: AlReportParser is the single pass that reads a
    /// report's Caption, off the same PropertyList it reads the report's ProcessingOnly and
    /// UseRequestPage from (#1714). Routing through here keeps ONE accessor for every
    /// AllObj/AllObjWithCaption kind while there is still only ONE read of the fact — and it
    /// is ordering-safe regardless of parse order, because this is a LAZY read of
    /// _parsedReports at consumption time (some later virtual-table query), well after every
    /// source dir has finished parsing (#1903 changed HOW parsing is batched — per-file
    /// across all eight extractors, rather than one global pass per extractor kind — but
    /// every report is still fully parsed before any caller reaches this accessor).
    /// </summary>
    private static string? SourceCaptionFor(string kind, int id)
        => string.Equals(kind, "Report", StringComparison.OrdinalIgnoreCase)
            ? (_parsedReports.TryGetValue(id, out var report) ? report.Caption : null)
            : _parsedObjectCaptions.TryGetValue((kind, id), out var caption) ? caption : null;
}
