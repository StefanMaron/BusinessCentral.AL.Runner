using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Xml;
using AlRunner;

/// <summary>
/// Generates Cobertura XML coverage reports from AL Runner's statement-level
/// coverage data. Maps BC compiler SourceSpans back to AL source line numbers.
///
/// Output works with:
/// - VS Code "Coverage Gutters" extension (line highlighting in editor)
/// - GitHub Actions coverage annotations
/// - Most CI coverage tools (Codecov, Coveralls, etc.)
/// </summary>
public static class CoverageReport
{
    /// <summary>
    /// Parse [SourceSpans] attributes from the pre-rewrite generated C# to build
    /// a mapping of (scopeClassName, stmtIndex) -> AL source line number.
    ///
    /// SourceSpans encoding: each long value packs start and end positions as
    /// (end_line &lt;&lt; 48) | (end_col &lt;&lt; 32) | (start_line &lt;&lt; 16) | start_col.
    /// We extract start_line from the lower 32 bits: (value &amp; 0xFFFFFFFF) >> 16
    /// and start_col as the low 16 bits: value &amp; 0xFFFF.
    /// </summary>
    public static Dictionary<(string Scope, int StmtIndex), int> ParseSourceSpans(
        List<(string Name, string Code)> generatedCSharp)
    {
        var pairs = ParseSourceSpansWithColumns(generatedCSharp);
        var map = new Dictionary<(string, int), int>(pairs.Count);
        foreach (var kv in pairs)
            map[kv.Key] = kv.Value.Line;
        return map;
    }

    /// <summary>
    /// Same as <see cref="ParseSourceSpans"/> but preserves column info.
    /// </summary>
    public static Dictionary<(string Scope, int StmtIndex), (int Line, int Column)> ParseSourceSpansWithColumns(
        List<(string Name, string Code)> generatedCSharp)
    {
        var map = new Dictionary<(string, int), (int Line, int Column)>();

        var spanPattern = new Regex(@"\[SourceSpans\(([^)]+)\)\]");
        var scopePattern = new Regex(@"class\s+(\w+_Scope(?:_\w+)?)");

        foreach (var (name, code) in generatedCSharp)
        {
            long[]? pendingSpans = null;

            foreach (var line in code.Split('\n'))
            {
                // Check for [SourceSpans(...)] attribute
                var spanMatch = spanPattern.Match(line);
                if (spanMatch.Success)
                {
                    var values = spanMatch.Groups[1].Value;
                    pendingSpans = values.Split(',')
                        .Select(v => long.Parse(v.Trim().TrimEnd('L', 'l')))
                        .ToArray();
                    continue;
                }

                // If we have pending spans and hit a scope class declaration,
                // register the line mappings
                if (pendingSpans != null)
                {
                    var scopeMatch = scopePattern.Match(line);
                    if (scopeMatch.Success)
                    {
                        var scopeName = scopeMatch.Groups[1].Value;
                        for (int i = 0; i < pendingSpans.Length; i++)
                        {
                            long packed = pendingSpans[i];
                            // SourceSpans encode 0-based line/col; convert to 1-based
                            int rawLine = (int)((packed & 0xFFFFFFFF) >> 16);
                            int startLine = rawLine + 1;
                            int startCol = (int)(packed & 0xFFFF) + 1;
                            if (rawLine >= 0)
                                map[(scopeName, i)] = (startLine, startCol);
                        }
                    }
                    pendingSpans = null;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Build a mapping from scope class name to AL object name.
    /// Each (Name, Code) pair in generatedCSharp has Name = AL object name.
    /// We find all scope classes in each Code block and map them to that Name.
    /// </summary>
    public static Dictionary<string, string> BuildScopeToObjectMap(
        List<(string Name, string Code)> generatedCSharp)
    {
        var map = new Dictionary<string, string>();
        var scopePattern = new Regex(@"class\s+(\w+_Scope(?:_\w+)?)");

        foreach (var (name, code) in generatedCSharp)
        {
            foreach (Match m in scopePattern.Matches(code))
            {
                map[m.Groups[1].Value] = name;
            }
        }
        return map;
    }

    /// <summary>
    /// Walks the (scope, stmtIdx) statement set, resolves each to a file path, and
    /// builds a per-file dictionary mapping AL line number → hit count.
    ///
    /// Filters out statements not present in <paramref name="totalStatements"/>
    /// (var decls / structural spans) and scopes that don't resolve to a user file
    /// via <paramref name="scopeToObject"/> + SourceFileMapper.GetFile.
    /// </summary>
    /// <param name="sumHits">
    /// true → per-line hits are SUMMED across statements (structured JSON shape).
    /// false → per-line hits are clamped to 1 if any statement on that line was hit
    /// (cobertura shape).
    /// </param>
    /// <returns>
    /// A tuple of:
    /// <list type="bullet">
    ///   <item>fileLines — per-file map of line → hit count.</item>
    ///   <item>fileTotals — per-file (totalStatements, hitStatements) counts.</item>
    /// </list>
    /// </returns>
    private static (Dictionary<string, Dictionary<int, int>> FileLines,
                    Dictionary<string, (int Total, int Hit)> FileTotals)
        AggregatePerFileLines(
            Dictionary<(string Scope, int StmtIndex), int> sourceSpans,
            HashSet<(string Type, int Id)> hitStatements,
            HashSet<(string Type, int Id)> totalStatements,
            Dictionary<string, string>? scopeToObject,
            bool sumHits)
    {
        var fileLines = new Dictionary<string, Dictionary<int, int>>();
        var fileTotals = new Dictionary<string, (int Total, int Hit)>();

        foreach (var ((scope, stmtIdx), line) in sourceSpans)
        {
            if (!totalStatements.Contains((scope, stmtIdx))) continue;

            string? filePath = null;
            if (scopeToObject != null && scopeToObject.TryGetValue(scope, out var objectName))
            {
                filePath = SourceFileMapper.GetFile(objectName);
            }
            if (filePath == null) continue;

            bool hit = hitStatements.Contains((scope, stmtIdx));

            if (!fileLines.TryGetValue(filePath, out var lines))
            {
                lines = new Dictionary<int, int>();
                fileLines[filePath] = lines;
            }

            if (sumHits)
            {
                lines[line] = lines.GetValueOrDefault(line, 0) + (hit ? 1 : 0);
            }
            else
            {
                // Cobertura: any hit counts as covered (clamp to 1).
                if (!lines.ContainsKey(line))
                    lines[line] = hit ? 1 : 0;
                else if (hit)
                    lines[line] = 1;
            }

            fileTotals.TryGetValue(filePath, out var totals);
            fileTotals[filePath] = (totals.Total + 1, totals.Hit + (hit ? 1 : 0));
        }

        return (fileLines, fileTotals);
    }

    /// <summary>
    /// Write a Cobertura XML coverage report.
    /// </summary>
    public static void WriteCobertura(
        string outputPath,
        Dictionary<(string Scope, int StmtIndex), int> sourceSpans,
        HashSet<(string Type, int Id)> hitStatements,
        HashSet<(string Type, int Id)> totalStatements,
        Dictionary<string, string>? scopeToObject = null)
    {
        var (fileLines, _) = AggregatePerFileLines(
            sourceSpans, hitStatements, totalStatements, scopeToObject, sumHits: false);

        // Write Cobertura XML
        using var writer = XmlWriter.Create(outputPath, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false)
        });

        writer.WriteStartDocument();
        writer.WriteDocType("coverage", null, "http://cobertura.sourceforge.net/xml/coverage-04.dtd", null);

        int totalLines = 0, coveredLines = 0;
        foreach (var lines in fileLines.Values)
        {
            totalLines += lines.Count;
            coveredLines += lines.Count(kv => kv.Value > 0);
        }
        double lineRate = totalLines > 0 ? (double)coveredLines / totalLines : 0;

        writer.WriteStartElement("coverage");
        writer.WriteAttributeString("line-rate", lineRate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteAttributeString("branch-rate", "0");
        writer.WriteAttributeString("lines-covered", coveredLines.ToString());
        writer.WriteAttributeString("lines-valid", totalLines.ToString());
        writer.WriteAttributeString("version", "1.0");
        writer.WriteAttributeString("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

        writer.WriteStartElement("sources");
        writer.WriteStartElement("source");
        writer.WriteString(".");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("packages");
        writer.WriteStartElement("package");
        writer.WriteAttributeString("name", "al-source");
        writer.WriteAttributeString("line-rate", lineRate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

        writer.WriteStartElement("classes");

        foreach (var (filePath, lines) in fileLines.OrderBy(kv => kv.Key))
        {
            var fileName = Path.GetFileName(filePath);
            int fTotal = lines.Count;
            int fCovered = lines.Count(kv => kv.Value > 0);
            double fRate = fTotal > 0 ? (double)fCovered / fTotal : 0;

            writer.WriteStartElement("class");
            writer.WriteAttributeString("name", Path.GetFileNameWithoutExtension(fileName));
            writer.WriteAttributeString("filename", filePath);
            writer.WriteAttributeString("line-rate", fRate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

            writer.WriteStartElement("lines");
            foreach (var (lineNum, hits) in lines.OrderBy(kv => kv.Key))
            {
                writer.WriteStartElement("line");
                writer.WriteAttributeString("number", lineNum.ToString());
                writer.WriteAttributeString("hits", hits.ToString());
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // lines

            writer.WriteEndElement(); // class
        }

        writer.WriteEndElement(); // classes
        writer.WriteEndElement(); // package
        writer.WriteEndElement(); // packages
        writer.WriteEndElement(); // coverage
    }

    /// <summary>
    /// Build structured coverage data as a list of <see cref="FileCoverage"/> records,
    /// one per AL source file that contains at least one executable statement.
    ///
    /// Mirrors the grouping logic of <see cref="WriteCobertura"/> but returns structured
    /// data instead of writing XML. Key differences from Cobertura output:
    /// <list type="bullet">
    ///   <item>Hit counts per line are <b>summed</b> across all statements on that line
    ///         (not clamped to max 1), preserving multi-statement detail for callers.</item>
    ///   <item>Every reachable line (i.e., every line that maps to a totalStatement with
    ///         a resolvable file) is included, even when its hit count is zero, so callers
    ///         can tell which lines are executable but uncovered.</item>
    ///   <item>Output is deterministic: files ordered by path, lines within each file
    ///         ordered by line number.</item>
    /// </list>
    ///
    /// Library and stub scopes (those with no entry in <paramref name="scopeToObject"/>
    /// or whose mapped object has no registered file) are silently excluded.
    /// </summary>
    /// <param name="sourceSpans">Maps (scope class name, statement index) to 1-based AL line number.</param>
    /// <param name="hitStatements">Set of (scope, stmtIdx) tuples that executed during the test run.</param>
    /// <param name="totalStatements">Set of (scope, stmtIdx) tuples that are real executable statements.
    /// Entries absent from this set (e.g. var-decls, blank-line spans) are skipped.</param>
    /// <param name="scopeToObject">Scope class name → AL object name. Pass <c>null</c> when no
    /// file-mapping information is available; in that case the result will always be empty.</param>
    /// <returns>Sorted list of <see cref="FileCoverage"/> records.</returns>
    public static List<FileCoverage> ToJson(
        Dictionary<(string Scope, int StmtIndex), int> sourceSpans,
        HashSet<(string Type, int Id)> hitStatements,
        HashSet<(string Type, int Id)> totalStatements,
        Dictionary<string, string>? scopeToObject = null)
    {
        var (fileLines, fileTotals) = AggregatePerFileLines(
            sourceSpans, hitStatements, totalStatements, scopeToObject, sumHits: true);

        // Build sorted result
        return fileLines
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var (total, hitCount) = fileTotals[kv.Key];
                var sortedLines = kv.Value
                    .OrderBy(lv => lv.Key)
                    .Select(lv => new LineCoverage(lv.Key, lv.Value))
                    .ToList();
                return new FileCoverage(kv.Key, sortedLines, total, hitCount);
            })
            .ToList();
    }
}

/// <summary>
/// Structured coverage data for a single AL source file.
/// </summary>
/// <param name="File">Relative path to the AL source file.</param>
/// <param name="Lines">Per-line coverage entries, ordered by line number.
/// Every executable line is included even if its hit count is zero.</param>
/// <param name="TotalStatements">Number of executable statements in this file.</param>
/// <param name="HitStatements">Number of those statements that were executed.</param>
/// <remarks>
/// Record value-equality compares <paramref name="Lines"/> by reference (since
/// <see cref="List{T}"/> does not override <see cref="object.Equals(object)"/>), so two
/// structurally-identical instances with distinct list instances will compare unequal;
/// compare line-by-line for structural equality.
/// </remarks>
public record FileCoverage(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("lines")] List<LineCoverage> Lines,
    [property: JsonPropertyName("totalStatements")] int TotalStatements,
    [property: JsonPropertyName("hitStatements")] int HitStatements
);

/// <summary>
/// Hit count for a single AL source line.
/// </summary>
/// <param name="Line">1-based AL source line number.</param>
/// <param name="Hits">Number of statements on this line that were executed during the run.
/// Multiple statements on the same line sum their individual 0/1 hits.</param>
public record LineCoverage(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("hits")] int Hits
);
