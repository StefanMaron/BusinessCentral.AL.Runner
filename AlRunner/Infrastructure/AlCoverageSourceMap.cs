// AlCoverageSourceMap — maps (AL object type label, AL object id) back to the .al file
// that declares it AND to the line offset BC's [SourceSpans] need to become file lines,
// for --coverage's cobertura output, the server's statement tables and the DAP session.
// Coverage-only utility: it does not touch AL compilation, just parses the same .al files
// the bundle was already compiled from with BC's own parser.
using System.Collections;
using System.Collections.Concurrent;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavSyntax = Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;

namespace AlRunner.Infrastructure;

/// <summary>Something the scan could not read, and why. Never a parse failure or an object
/// kind the map does not carry — those are measurements, and this is the absence of one.</summary>
public readonly record struct SourceScanFailure(string Path, string Reason);

/// <summary>
/// (object label, object id) → the declaring file, readable as the plain
/// <c>IReadOnlyDictionary&lt;(Label, Id), string&gt;</c> the older consumers take, plus
/// <see cref="LineOffset"/>, the number of lines to add to a decoded [SourceSpans] line to
/// get the file line. BC's [SourceSpans] lines are relative to the object's own text with
/// the file's PREAMBLE counted in, so the offset is the object's FullSpan start minus the
/// preamble's length — 0 for the first object in a file.
/// <para>
/// The preamble is <b>the number of complete source lines before the earliest top-level
/// object's FullSpan</b>, which is not the same as "everything before the first
/// declaration keyword" (#3822). A FullSpan owns its own leading trivia, so a comment or a
/// blank line immediately in front of a declaration belongs to that object and is NOT
/// preamble; a <c>namespace</c> or <c>using</c> is a sibling syntax node, so it is. That is
/// why the first object of a file with a header measures 1 and 5 in the fixtures rather
/// than 0.
/// </para>
/// <para>
/// "Earliest top-level object" means every entry in <c>root.Objects</c>, including the
/// kinds <see cref="AlCoverageSourceMap"/> cannot map — an interface, a controladdin, a
/// permissionset, an enum extension. Those are objects, not preamble, and measuring the
/// origin over the mapped subset instead subtracted them as though they were, putting every
/// later object's lines exactly that far too low (#3822). Table, page and report extensions
/// ARE mapped since #3833; see LabelOf.
/// </para>
/// (#3713; CoverageMultiObjectFileTests pins the header shapes that settled both.)
/// </summary>
public sealed class AlSourceLocationMap : IReadOnlyDictionary<(string Label, int Id), string>
{
    private readonly Dictionary<(string Label, int Id), (string Path, int LineOffset)> _entries = new();
    private readonly List<SourceScanFailure> _scanFailures = new();

    /// <summary>A map with no objects; never mutated.</summary>
    public static AlSourceLocationMap Empty { get; } = new();

    /// <summary>
    /// What the scan could not read while building this map — a root that is not there, a
    /// directory it could not enter, a file it could not open. Empty is the ordinary answer.
    ///
    /// <para>This exists because the map alone cannot express it (#3847). A missing entry means
    /// "that object has no executable statements" to every consumer, and a bundle that
    /// genuinely declares nothing mappable is a real state — so <see cref="Count"/> being 0
    /// distinguishes nothing, and an unreadable source would otherwise be reported to the user
    /// as a fact about their AL. `.claude/rules/guards-need-a-third-state.md`.</para>
    ///
    /// <para>It is a REPORT, not an error: the map is still returned and is still correct about
    /// everything it did read. What a consumer owes it is to stop claiming certainty about the
    /// parts it did not.</para>
    /// </summary>
    public IReadOnlyList<SourceScanFailure> ScanFailures => _scanFailures;

    /// <summary>True when any source could not be read, so a missing entry may mean
    /// "unmeasured" rather than "no statements here".</summary>
    public bool IsIncomplete => _scanFailures.Count > 0;

    internal void Add(string label, int id, string path, int lineOffset) =>
        _entries[(label, id)] = (path, lineOffset);

    internal void AddScanFailure(string path, string reason)
    {
        // Deduplicated: one root can be named by two callers, and a locked file is reached
        // once per scan but a scan can run per bundle.
        foreach (var existing in _scanFailures)
            if (existing.Path == path) return;
        _scanFailures.Add(new SourceScanFailure(path, reason));
    }

    /// <summary>Lines to add to a decoded [SourceSpans] line of this object to get its file
    /// line; 0 for the first object in a file and for an object not in the map.</summary>
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
    /// <summary>One object as parsed from a file: label, id, and its line offset.</summary>
    private readonly record struct ParsedObject(string Label, int Id, int LineOffset);

    // Parse results per file, keyed by (path, length, last write, symbols). A --server process
    // builds this map on every coverage request over the same tree; re-parsing thousands of
    // unchanged files per request was the review's cost finding on the #3713 PR.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ParsedObject>> _parsed = new(StringComparer.Ordinal);

    /// <summary>
    /// Scans every *.al file under <paramref name="roots"/> (recursively) and returns a map
    /// from (object label, object id) to the file's path — relative to
    /// <paramref name="relativeTo"/> when given, else absolute — and the object's line
    /// offset. EVERY coverable object declared in a file is registered: AL lets one file
    /// declare several objects and alc compiles such a file at 0 errors; registering only the
    /// first (#3713) dropped every later object's executed lines from the report.
    /// <para>
    /// Objects come from BC's own parser (<c>SyntaxTree.ParseObjectText</c>, as
    /// <see cref="AlMemberSyntaxIndex"/> uses it), with the preprocessor symbols of the nearest
    /// app.json above each file. A file the parser throws on is an error, not a fallback: a
    /// map with a guessed offset reports wrong lines with exit 0, the shape this method exists
    /// to remove (loud-failures.md).
    /// </para>
    /// </summary>
    public static AlSourceLocationMap Build(IEnumerable<string> roots, string? relativeTo = null)
    {
        var map = new AlSourceLocationMap();
        var symbolsByAppJson = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var appJsonByDir = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                // Not the same as a root with nothing in it: the caller named this path, so
                // its absence is an unmeasured scan rather than an empty one (#3847).
                map.AddScanFailure(root, "the source root does not exist");
                continue;
            }
            // SafeDirectoryScan has always been able to say which directories it could not
            // enter; this scan discarded the answer with `out _`, which is the shape #3847 is
            // about — the mechanism existed and the caller threw it away.
            var files = SafeDirectoryScan.Files(root, "*.al", out var inaccessible);
            foreach (var dir in inaccessible)
                map.AddScanFailure(dir, "the directory could not be read, so any AL under it was not scanned");
            foreach (var file in files)
            {
                var appJson = NearestAppJson(Path.GetDirectoryName(file)!, appJsonByDir);
                if (!symbolsByAppJson.TryGetValue(appJson ?? "", out var symbols))
                    symbolsByAppJson[appJson ?? ""] = symbols = AlMemberSyntaxIndex.PreprocessorSymbols(appJson);

                var path = relativeTo != null
                    ? Path.GetRelativePath(relativeTo, file).Replace('\\', '/')
                    : file.Replace('\\', '/');
                var parsed = ParseObjects(file, symbols, out var readFailure);
                foreach (var o in parsed)
                    map.Add(o.Label, o.Id, path, o.LineOffset);
                if (readFailure != null) map.AddScanFailure(file, readFailure);
            }
        }
        WarnOnce(map);
        return map;
    }

    private static readonly HashSet<string> _warned = new();

    /// <summary>
    /// Says out loud, once per path per process, that part of the scan did not happen. Here
    /// rather than at each of the five call sites, because every one of them would otherwise
    /// have to remember — and the consumer that forgets is the one that reports a confident
    /// negative about AL nobody read (#3847).
    ///
    /// Console.Error, not Console.Out: under --dap stdio, stdout IS the protocol channel.
    /// </summary>
    private static void WarnOnce(AlSourceLocationMap map)
    {
        foreach (var failure in map.ScanFailures)
        {
            lock (_warned)
                if (!_warned.Add(failure.Path)) continue;
            Console.Error.WriteLine(
                $"[source-map] {failure.Path}: {failure.Reason}. Objects it declares are absent "
                + "from the source map, so coverage and the debugger cannot attribute them.");
        }
    }

    /// <summary>The nearest app.json in <paramref name="dir"/> or an ancestor, or null. The
    /// bundle root is not always the app root: a server request may name <c>app/src</c>, and a
    /// bundle can be a parent of several apps with their own manifests and symbols.</summary>
    private static string? NearestAppJson(string dir, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(dir, out var known)) return known;
        string? found = null;
        for (var d = dir; d != null; d = Path.GetDirectoryName(d))
        {
            var candidate = Path.Combine(d, "app.json");
            if (File.Exists(candidate)) { found = candidate; break; }
        }
        cache[dir] = found;
        return found;
    }

    /// <summary><paramref name="readFailure"/> is non-null when the file exists and could not
    /// be READ — which used to return an empty list, indistinguishable from a file that
    /// declares no objects (#3847). A parse that fails is a different thing and stays where it
    /// was: the file was measured, and what it holds is not something this map carries.</summary>
    private static IReadOnlyList<ParsedObject> ParseObjects(
        string file, IReadOnlyList<string> symbols, out string? readFailure)
    {
        readFailure = null;
        var info = new FileInfo(file);
        var key = $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{string.Join(",", symbols)}";
        if (_parsed.TryGetValue(key, out var cached)) return cached;

        string content;
        try { content = File.ReadAllText(file); }
        catch (IOException ex)
        {
            readFailure = $"the file could not be read ({ex.GetType().Name}: {ex.Message})";
            return Array.Empty<ParsedObject>();
        }
        catch (UnauthorizedAccessException ex)
        {
            readFailure = $"the file could not be read ({ex.GetType().Name}: {ex.Message})";
            return Array.Empty<ParsedObject>();
        }

        List<ParsedObject> result;
        try
        {
            var parseOpts = new NavCA.ParseOptions(
                runtimeVersion: null!,
                preprocessorSymbols: symbols,
                documentationMode: NavCA.DocumentationMode.None);
            var tree = NavSyntax.SyntaxTree.ParseObjectText(content, path: file, encoding: null!, parseOpts, default);
            var root = tree.GetCompilationUnitRoot();

            var objects = new List<(string Label, int Id, int Start)>();
            var allStarts = new List<int>();
            foreach (var obj in root.Objects)
            {
                allStarts.Add(tree.GetLineSpan(obj.FullSpan).StartLinePosition.Line);
                var label = LabelOf(obj);
                if (label == null) continue;
                if (obj is not NavSyntax.ApplicationObjectSyntax ao || ao.ObjectId?.Value.Value is not int id) continue;
                objects.Add((label, id, tree.GetLineSpan(obj.FullSpan).StartLinePosition.Line));
            }
            // Offset = this object's FullSpan start minus the FIRST object's: BC measures every
            // object from its own text but keeps the file's preamble in front of each of them,
            // so a namespace/using header shifts the later objects by its length and the first
            // object by nothing (#3713, CoverageMultiObjectFileTests.Build_ObjectsAfterAFileHeader_*).
            // firstStart is the FILE PREAMBLE's length: BC's text for every object is
            // [preamble][that object], while the parser's FullSpan starts after the preamble.
            // Measured over EVERY object, not just the mapped ones (#3822) — an unmapped
            // object in front is not preamble, and taking the first MAPPED object's start
            // subtracts it as though it were.
            var firstStart = allStarts.Count > 0 ? allStarts.Min() : 0;
            result = objects.Select(o => new ParsedObject(o.Label, o.Id, o.Start - firstStart)).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[coverage] {file}: BC's parser failed ({ex.GetType().Name}: {ex.Message}), so the objects it declares "
                + "cannot be placed on their lines. Refusing to write a coverage report that would put them elsewhere.", ex);
        }
        _parsed[key] = result;
        return result;
    }

    // The coverable object kinds, labelled the way AlCallStackCapture.ParseObjectTypeAndId
    // labels the emitted scope classes: the seven the header regex accepted before #3713,
    // plus the three code-bearing extension kinds since #3833. What stays unregistered is
    // the kinds that carry no executable code — an interface, a controladdin, a
    // permissionset, an enum extension — for which BC emits no scope class at all, so a map
    // entry would have nothing to join to.
    private static string? LabelOf(NavCA.SyntaxNode obj) => obj switch
    {
        NavSyntax.CodeunitSyntax => "CodeUnit",
        NavSyntax.TableSyntax => "Table",
        NavSyntax.PageSyntax => "Page",
        NavSyntax.ReportSyntax => "Report",
        NavSyntax.QuerySyntax => "Query",
        NavSyntax.XmlPortSyntax => "XmlPort",
        NavSyntax.EnumTypeSyntax => "Enum",
        // #3833: extension objects carry executable procedures and triggers, and BC emits
        // scope classes for them — so without these their statements had a runtime identity
        // and no file, and were dropped. The label must match AlCallStackCapture's prefix map,
        // and both match the spelling RecordPatches.AlSourceParser already uses.
        //
        // An extension is keyed by its OWN id, not the base object's — measured, BC emits
        // TableExtension63701 for an extension of table 63700. What keeps entries apart is
        // the (label, id) PAIR rather than the id alone: a tableextension and a pageextension
        // may legally share a number, so it is the distinct label that separates them.
        //
        // Enum extensions are deliberately absent: they declare values, not code, and BC
        // emits no scope class for one (measured — nothing for `enumextension` appears among
        // the [SourceSpans]-carrying types of a bundle that declares one).
        NavSyntax.TableExtensionSyntax => "TableExtension",
        NavSyntax.PageExtensionSyntax => "PageExtension",
        NavSyntax.ReportExtensionSyntax => "ReportExtension",
        _ => null,
    };
}
