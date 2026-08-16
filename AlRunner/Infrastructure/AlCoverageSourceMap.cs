// AlCoverageSourceMap — maps (AL object type label, AL object id) back to the .al file
// that declares it, for --coverage's cobertura output. Coverage-only utility: it does
// not touch AL parsing/compilation, just scans header lines of the same .al files the
// bundle was already compiled from, the same way v1's SourceFileMapper did.
using System.Text.RegularExpressions;

namespace AlRunner.Infrastructure;

public static class AlCoverageSourceMap
{
    // Matches an AL object declaration header: `<keyword> <id> <name>`. Anchored to the
    // start of a (trimmed) line so it does not fire inside comments/strings later in the
    // file. Only the keyword + numeric id are needed; the label mapping below must match
    // AlCallStackCapture.ParseObjectTypeAndId's labels exactly, since that is the other
    // half of the (label, id) key this map is looked up by.
    private static readonly Regex ObjectHeaderPattern = new(
        @"^\s*(codeunit|table|page|report|xmlport|query|enum)\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Dictionary<string, string> KeywordToLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codeunit"] = "CodeUnit",
        ["table"] = "Table",
        ["page"] = "Page",
        ["report"] = "Report",
        ["query"] = "Query",
        ["xmlport"] = "XmlPort",
        ["enum"] = "Enum",
    };

    /// <summary>
    /// Scans every *.al file under <paramref name="roots"/> (recursively) and returns a
    /// map from (object label, object id) to the file's path, relative to
    /// <paramref name="relativeTo"/> when given (else absolute). Only the FIRST object
    /// header found per file is registered — AL files declare exactly one top-level
    /// object, matching the compiler's own constraint.
    /// </summary>
    public static Dictionary<(string Label, int Id), string> Build(
        IEnumerable<string> roots, string? relativeTo = null)
    {
        var map = new Dictionary<(string, int), string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.al", SearchOption.AllDirectories))
            {
                string content;
                try { content = File.ReadAllText(file); }
                catch (IOException) { continue; }

                var m = ObjectHeaderPattern.Match(content);
                if (!m.Success) continue;
                if (!KeywordToLabel.TryGetValue(m.Groups[1].Value, out var label)) continue;
                if (!int.TryParse(m.Groups[2].Value, out var id)) continue;

                var path = relativeTo != null
                    ? Path.GetRelativePath(relativeTo, file).Replace('\\', '/')
                    : file.Replace('\\', '/');
                map[(label, id)] = path;
            }
        }
        return map;
    }
}
