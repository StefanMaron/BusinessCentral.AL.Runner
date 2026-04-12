using System.Text;
using System.Text.RegularExpressions;
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
    /// Write a Cobertura XML coverage report.
    /// </summary>
    public static void WriteCobertura(
        string outputPath,
        Dictionary<(string Scope, int StmtIndex), int> sourceSpans,
        HashSet<(string Type, int Id)> hitStatements,
        HashSet<(string Type, int Id)> totalStatements,
        Dictionary<string, string>? scopeToObject = null)
    {
        // Group coverage data by source file
        var fileLines = new Dictionary<string, Dictionary<int, int>>(); // file -> line -> hitCount

        foreach (var ((scope, stmtIdx), line) in sourceSpans)
        {
            // Only include lines that correspond to actual executable statements
            // (i.e., statements with StmtHit/CStmtHit calls in the generated C#).
            // This filters out variable declarations, blank lines, and structural
            // keywords that the BC compiler includes in SourceSpans for error mapping.
            if (!totalStatements.Contains((scope, stmtIdx)))
                continue;

            // Find which file this scope belongs to using SourceFileMapper
            string? filePath = null;
            if (scopeToObject != null && scopeToObject.TryGetValue(scope, out var objectName))
            {
                filePath = SourceFileMapper.GetFile(objectName);
            }
            // If scope doesn't match any user file, skip it entirely.
            // This prevents library/stub scopes (Assert, etc.) from
            // bleeding into the user's coverage report.
            if (filePath == null) continue;

            if (!fileLines.TryGetValue(filePath, out var lines))
            {
                lines = new Dictionary<int, int>();
                fileLines[filePath] = lines;
            }

            bool hit = hitStatements.Contains((scope, stmtIdx));
            if (!lines.ContainsKey(line))
                lines[line] = hit ? 1 : 0;
            else if (hit)
                lines[line] = 1; // Any hit counts as covered
        }

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
}
