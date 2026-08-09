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
    private static void ParseAllXmlPortSources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParseXmlPortFile(File.ReadAllText(file));
        }
    }

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
