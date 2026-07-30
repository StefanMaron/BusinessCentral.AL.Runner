// RecordPatches.AlQueryParser — parses AL `query` / `queryextension` declarations
// into ParsedQuery records keyed by query ID. Mirror of AlPageParser; same
// minimal shape (id + name).
//
// Only the (id, name) tuple is needed: the cache slot just has to be non-null
// so NCLMetadata.GetMetaApplicationObjectInternal finds an entry instead of
// throwing NavNCLApplicationObjectNotFoundException for queries.
using System.Text.RegularExpressions;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly Regex RxQuery = new(
        @"\bquery\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxQueryExtension = new(
        @"\bqueryextension\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_]\w*))\s+extends\s+(?:""([^""]+)""|([A-Za-z_]\w*))[^{]*?\{",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void ParseAllQuerySources()
    {
        foreach (var dir in _sourceDirs)
        {
            var files = Directory.GetFiles(dir, "*.al", SearchOption.AllDirectories);
            foreach (var file in files)
                TryParseQueryFile(File.ReadAllText(file));
        }
    }

    private static void TryParseQueryFile(string text)
    {
        foreach (Match m in RxQuery.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            _parsedQueries[id] = new ParsedQuery(id, name, IsExtension: false);
        }

        foreach (Match m in RxQueryExtension.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            _parsedQueries[id] = new ParsedQuery(id, name, IsExtension: true);
        }
    }
}

internal record ParsedQuery(int Id, string Name, bool IsExtension);
