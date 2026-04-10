using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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
    /// We extract start_line from the lower 32 bits: (value &amp; 0xFFFFFFFF) >> 16.
    /// </summary>
    public static Dictionary<(string Scope, int StmtIndex), int> ParseSourceSpans(
        List<(string Name, string Code)> generatedCSharp)
    {
        var map = new Dictionary<(string, int), int>();

        var spanPattern = new Regex(@"\[SourceSpans\(([^)]+)\)\]");
        var scopePattern = new Regex(@"class\s+(\w+_Scope_\w+)");

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
                            // Extract start_line from lower 32 bits
                            int startLine = (int)((pendingSpans[i] & 0xFFFFFFFF) >> 16);
                            if (startLine > 0)
                                map[(scopeName, i)] = startLine;
                        }
                    }
                    pendingSpans = null;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Determine which AL source file a scope class belongs to, using the
    /// object name from the generated C# (e.g., "Discount Calculator" ->
    /// find the .al file containing that codeunit/table).
    /// </summary>
    public static Dictionary<string, string> MapObjectsToFiles(
        List<(string Name, string Code)> generatedCSharp,
        List<string> alSourcePaths)
    {
        // Map generated object name -> AL source file path
        var result = new Dictionary<string, string>();
        foreach (var (name, _) in generatedCSharp)
        {
            // Try to find a source file whose content declares this object
            foreach (var path in alSourcePaths)
            {
                if (File.Exists(path))
                {
                    var content = File.ReadAllText(path);
                    // Match codeunit/table name in quotes
                    if (content.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase))
                    {
                        result[name] = Path.GetFullPath(path);
                        break;
                    }
                }
            }
            // Fallback: use the object name as a synthetic path
            if (!result.ContainsKey(name))
                result[name] = name + ".al";
        }
        return result;
    }

    /// <summary>
    /// Write a Cobertura XML coverage report.
    /// </summary>
    public static void WriteCobertura(
        string outputPath,
        Dictionary<(string Scope, int StmtIndex), int> sourceSpans,
        HashSet<(string Type, int Id)> hitStatements,
        HashSet<(string Type, int Id)> totalStatements,
        Dictionary<string, string> objectToFile)
    {
        // Group coverage data by source file
        // scope name format: MethodName_Scope_NNNN — extract the parent object
        var fileLines = new Dictionary<string, Dictionary<int, int>>(); // file -> line -> hitCount

        foreach (var ((scope, stmtIdx), line) in sourceSpans)
        {
            // Find which file this scope belongs to
            string? filePath = null;
            foreach (var (objName, path) in objectToFile)
            {
                // Scope classes are nested in Codeunit/Record classes
                // Try matching by checking if the generated code contains this scope
                if (scope.Contains(objName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    filePath = path;
                    break;
                }
            }
            if (filePath == null)
            {
                // Fall back: use the scope name to guess the file
                foreach (var (objName, path) in objectToFile)
                    filePath ??= path;
            }
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
        writer.WriteAttributeString("line-rate", lineRate.ToString("F4"));
        writer.WriteAttributeString("branch-rate", "0");
        writer.WriteAttributeString("lines-covered", coveredLines.ToString());
        writer.WriteAttributeString("lines-valid", totalLines.ToString());
        writer.WriteAttributeString("version", "1.0");
        writer.WriteAttributeString("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

        writer.WriteStartElement("packages");
        writer.WriteStartElement("package");
        writer.WriteAttributeString("name", "al-source");
        writer.WriteAttributeString("line-rate", lineRate.ToString("F4"));

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
            writer.WriteAttributeString("line-rate", fRate.ToString("F4"));

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
