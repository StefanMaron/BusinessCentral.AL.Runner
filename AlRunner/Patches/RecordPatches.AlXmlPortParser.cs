// RecordPatches.AlXmlPortParser — parses AL `xmlport` declarations into
// ParsedXmlPort records keyed by xmlport ID. AL has no `xmlportextension`.
//
// Only the (id, name) tuple is needed: the cache slot just has to be non-null
// so NCLMetadata.GetMetaApplicationObjectInternal finds an entry instead of
// throwing NavNCLApplicationObjectNotFoundException for xmlports.
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly Regex RxXmlPort = new(
        @"\bxmlport\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        foreach (Match m in RxXmlPort.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            _parsedXmlPorts[id] = new ParsedXmlPort(id, name);
        }
    }
}

internal record ParsedXmlPort(int Id, string Name);
