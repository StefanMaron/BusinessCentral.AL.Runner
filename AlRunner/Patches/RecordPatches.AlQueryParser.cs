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
            // QueryType feeds AllObjWithCaption's "Object Subtype" column (#2326), the same
            // way ParsedPage.PageType does for a page. AL's default when the property is
            // absent is Normal, and BC reports a query's QueryType by NAME — including
            // "Normal" — unlike a codeunit's Subtype, which BC blanks when it is Normal.
            var queryTypeText = Unquote(PropValue(q.PropertyList, "QueryType")?.ToString()?.Trim() ?? "");
            _parsedQueries[id] = new ParsedQuery(id, IdentText(q.Name), IsExtension: false,
                QueryType: queryTypeText.Length > 0 ? queryTypeText : "Normal");
        }
    }
}

/// <summary>
/// <paramref name="QueryType"/> is AL's <c>QueryType</c> property as written, defaulted to
/// <c>Normal</c> when the query declares none — see RecordPatches.AllObjWithCaptionVirtualTable.cs.
/// </summary>
internal record ParsedQuery(int Id, string Name, bool IsExtension, string QueryType = "Normal");
