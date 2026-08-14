// RecordPatches.AlQueryParser — parses AL `query` / `queryextension` declarations
// into ParsedQuery records keyed by query ID. Mirror of AlPageParser; same
// minimal shape (id + name).
//
// Only the (id, name) tuple is needed: the cache slot just has to be non-null
// so NCLMetadata.GetMetaApplicationObjectInternal finds an entry instead of
// throwing NavNCLApplicationObjectNotFoundException for queries.
// Parsed from BC's own AL syntax tree (#1696), so a `query` mentioned in prose or a
// commented-out declaration is not a query. AL HAS NO `queryextension` — the compiler's own
// object-keyword list excludes it, and text that says `queryextension 50100 X extends Y` is a
// parse error, not an object. The old regex matched that text anyway and stored an
// IsExtension: true entry; nothing valid can reach that path, so the flag is now always false.
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Register()-time sweep folded into RecordPatches.ParseAllRegisteredSourceFiles (#1903)
    // — that shared loop calls TryParseQueryFile alongside the other seven extractors, one
    // file read per file, instead of this file doing its own separate directory walk.

    private static void TryParseQueryFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.QuerySyntax q) continue;
            if (ObjectIdOf(q) is not int id) continue;
            _parsedQueries[id] = new ParsedQuery(id, IdentText(q.Name), IsExtension: false);
        }
    }
}

internal record ParsedQuery(int Id, string Name, bool IsExtension);
