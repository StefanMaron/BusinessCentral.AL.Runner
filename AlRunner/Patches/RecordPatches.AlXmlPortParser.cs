// RecordPatches.AlXmlPortParser — parses AL `xmlport` declarations into
// ParsedXmlPort records keyed by xmlport ID. AL has no `xmlportextension`.
//
// Only the (id, name) tuple is needed: the cache slot just has to be non-null
// so NCLMetadata.GetMetaApplicationObjectInternal finds an entry instead of
// throwing NavNCLApplicationObjectNotFoundException for xmlports.
// Parsed from BC's own AL syntax tree (#1696).
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    // Register()-time sweep folded into RecordPatches.ParseAllRegisteredSourceFiles (#1903)
    // — that shared loop calls TryParseXmlPortFile alongside the other seven extractors, one
    // file read per file, instead of this file doing its own separate directory walk.

    private static void TryParseXmlPortFile(string text)
    {
        foreach (var obj in ParseAlObjects(text))
        {
            if (obj is not NavSyntax.XmlPortSyntax x) continue;
            if (ObjectIdOf(x) is not int id) continue;
            _parsedXmlPorts[id] = new ParsedXmlPort(id, IdentText(x.Name));
        }
    }
}

internal record ParsedXmlPort(int Id, string Name);
