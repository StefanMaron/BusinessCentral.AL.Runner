// AlCoverageSourceMap — maps (AL object type label, AL object id) back to the .al file
// that declares it AND to where in that file the object's text begins, for --coverage's
// cobertura output, the server's statement tables and the DAP session. Coverage-only
// utility: it does not touch AL compilation, just parses the same .al files the bundle was
// already compiled from with BC's own parser.
using System.Collections;
using System.Text.RegularExpressions;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Infrastructure;

/// <summary>
/// (object label, object id) → the declaring file, readable as the plain
/// <c>IReadOnlyDictionary&lt;(Label, Id), string&gt;</c> every consumer already takes, plus
/// <see cref="LineOffset"/>: the 0-based file line on which the object's text begins.
/// <para>
/// BC's [SourceSpans] lines are relative to the object's own text, not to the file. For the
/// first object in a file the two coincide, which is why one-object files never showed the
/// difference. For a later object in the same file (#3713) every statement line must have
/// the object's start line added, or the report places the second object's statements
/// inside the first's range — measured: <c>exit(22)</c> on file line 13 reported as line 6,
/// because its object's text starts on line 8 (0-based 7) and BC counted from there.
/// </para>
/// </summary>
public sealed class AlSourceLocationMap : IReadOnlyDictionary<(string Label, int Id), string>
{
    private readonly Dictionary<(string Label, int Id), (string Path, int LineOffset)> _entries = new();

    /// <summary>A map with no objects; never mutated.</summary>
    public static AlSourceLocationMap Empty { get; } = new();

    internal void Add(string label, int id, string path, int lineOffset) =>
        _entries[(label, id)] = (path, lineOffset);

    /// <summary>
    /// The 0-based file line the object's text (leading trivia included, as BC's syntax
    /// tree counts it) starts on; 0 for the first object in a file and for an unknown
    /// object. Add it to a decoded [SourceSpans] line to get the file line.
    /// </summary>
    public int LineOffset(string label, int id) =>
        _entries.TryGetValue((label, id), out var e) ? e.LineOffset : 0;

    public string this[(string Label, int Id) key] => _entries[key].Path;
    public IEnumerable<(string Label, int Id)> Keys => _entries.Keys;
    public IEnumerable<string> Values => _entries.Values.Select(e => e.Path);
    public int Count => _entries.Count;
    public bool ContainsKey((string Label, int Id) key) => _entries.ContainsKey(key);

    public bool TryGetValue((string Label, int Id) key, out string value)
    {
        if (_entries.TryGetValue(key, out var e)) { value = e.Path; return true; }
        value = null!;
        return false;
    }

    public IEnumerator<KeyValuePair<(string Label, int Id), string>> GetEnumerator() =>
        _entries.Select(kv => new KeyValuePair<(string Label, int Id), string>(kv.Key, kv.Value.Path)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class AlCoverageSourceMap
{
    // Text fallback for a file BC's parser refuses: an AL object declaration header,
    // `<keyword> <id> <name>`, anchored to the start of a (trimmed) line so it does not fire
    // inside comments/strings later in the file. Labels must match
    // AlCallStackCapture.ParseObjectTypeAndId's exactly — that is the other half of the
    // (label, id) key this map is looked up by.
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
    /// Scans every *.al file under <paramref name="roots"/> (recursively) and returns a map
    /// from (object label, object id) to the file's path — relative to
    /// <paramref name="relativeTo"/> when given, else absolute — and the object's start line.
    /// EVERY object declared in a file is registered: AL lets one file declare several
    /// objects and alc compiles such a file at 0 errors. Registering only the first (#3713)
    /// made <see cref="AlCoverageTracker.Collect"/> skip every later object's scopes, so
    /// their executed lines were absent from the report, indistinguishable from never run.
    /// <para>
    /// Objects and their start lines come from BC's own parser (the same
    /// <c>SyntaxTree.ParseObjectText</c> <see cref="AlMemberSyntaxIndex"/> uses, with the
    /// root's app.json preprocessor symbols). A file the parser throws on falls back to the
    /// text headers with start line 0 and says so on stdout: for such a file the lines of
    /// any object after the first are wrong, and silence here is the defect this map exists
    /// to remove.
    /// </para>
    /// </summary>
    public static AlSourceLocationMap Build(IEnumerable<string> roots, string? relativeTo = null)
    {
        var map = new AlSourceLocationMap();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var appJson = Path.Combine(root, "app.json");
            var symbols = AlMemberSyntaxIndex.PreprocessorSymbols(File.Exists(appJson) ? appJson : null);
            foreach (var file in SafeDirectoryScan.Files(root, "*.al"))
            {
                string content;
                try { content = File.ReadAllText(file); }
                catch (IOException) { continue; }

                var path = relativeTo != null
                    ? Path.GetRelativePath(relativeTo, file).Replace('\\', '/')
                    : file.Replace('\\', '/');
                if (!TryAddFromSyntax(map, content, file, path, symbols))
                    AddFromHeaders(map, content, path);
            }
        }
        return map;
    }

    private static bool TryAddFromSyntax(
        AlSourceLocationMap map, string content, string file, string path, IReadOnlyList<string> symbols)
    {
        try
        {
            var parseOpts = new NavCA.ParseOptions(
                runtimeVersion: null!,
                preprocessorSymbols: symbols,
                documentationMode: NavCA.DocumentationMode.None);
            var tree = NavSyntax.SyntaxTree.ParseObjectText(content, path: file, encoding: null!, parseOpts, default);
            var root = tree.GetCompilationUnitRoot();
            foreach (var obj in root.Objects)
            {
                var label = LabelOf(obj);
                if (label == null) continue;
                if (obj is not NavSyntax.ApplicationObjectSyntax ao || ao.ObjectId?.Value.Value is not int id) continue;
                // FullSpan, not Span: BC counts an object's lines from the start of its leading
                // trivia — the blank line and comments between it and the previous object —
                // which is where the emitted [SourceSpans] lines are measured from.
                var startLine = tree.GetLineSpan(obj.FullSpan).StartLinePosition.Line;
                map.Add(label, id, path, startLine);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[coverage] warn: {path}: BC's parser failed ({ex.GetType().Name}: {ex.Message}). Registering its "
                + "object headers by text with start line 0, so the reported lines of any object after the first in "
                + "this file are wrong.");
            return false;
        }
    }

    private static void AddFromHeaders(AlSourceLocationMap map, string content, string path)
    {
        foreach (Match m in ObjectHeaderPattern.Matches(content))
        {
            if (!KeywordToLabel.TryGetValue(m.Groups[1].Value, out var label)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var id)) continue;
            map.Add(label, id, path, 0);
        }
    }

    // The coverable object kinds, labelled the way AlCallStackCapture.ParseObjectTypeAndId
    // labels the emitted scope classes. Extensions, interfaces, controladdins, profiles and
    // permission sets carry no [SourceSpans] scopes of their own and stay out, as before.
    private static string? LabelOf(NavCA.SyntaxNode obj) => obj switch
    {
        NavSyntax.CodeunitSyntax => "CodeUnit",
        NavSyntax.TableSyntax => "Table",
        NavSyntax.PageSyntax => "Page",
        NavSyntax.ReportSyntax => "Report",
        NavSyntax.QuerySyntax => "Query",
        NavSyntax.XmlPortSyntax => "XmlPort",
        NavSyntax.EnumTypeSyntax => "Enum",
        _ => null,
    };
}
