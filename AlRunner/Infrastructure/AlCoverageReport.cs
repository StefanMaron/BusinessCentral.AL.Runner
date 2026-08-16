// AlCoverageReport — turns AlCoverageTracker.Collect()'s flat statement list into
// Cobertura XML (consumed by VS Code Coverage Gutters, GitHub Actions annotations, most
// CI coverage tools) and a console summary table.
using System.Globalization;
using System.Text;
using System.Xml;

namespace AlRunner.Infrastructure;

public static class AlCoverageReport
{
    /// <summary>Per-file coverage totals, used by both the console table and as the
    /// return value so callers (tests, --output-json) can assert on numbers directly
    /// instead of parsing XML.</summary>
    public readonly record struct FileCoverage(string FilePath, int CoveredLines, int TotalLines);

    /// <summary>
    /// Groups statements by file and AL source line (summing hit counts of statements
    /// that share a line — e.g. two short statements on one line), then writes Cobertura
    /// XML to <paramref name="outputPath"/>. Returns the per-file totals actually written.
    /// </summary>
    public static List<FileCoverage> WriteCobertura(string outputPath, IReadOnlyList<AlCoverageStatement> statements)
    {
        var byFile = new Dictionary<string, SortedDictionary<int, int>>(); // file -> line -> summed hits
        foreach (var s in statements)
        {
            if (!byFile.TryGetValue(s.FilePath, out var lines))
                byFile[s.FilePath] = lines = new SortedDictionary<int, int>();
            lines.TryGetValue(s.Line, out var existing);
            lines[s.Line] = existing + s.HitCount;
        }

        var fileCoverages = new List<FileCoverage>();
        int totalLines = 0, coveredLines = 0;
        foreach (var (file, lines) in byFile)
        {
            int fCovered = lines.Count(kv => kv.Value > 0);
            fileCoverages.Add(new FileCoverage(file, fCovered, lines.Count));
            totalLines += lines.Count;
            coveredLines += fCovered;
        }
        double lineRate = totalLines > 0 ? (double)coveredLines / totalLines : 0;

        using (var writer = XmlWriter.Create(outputPath, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
        }))
        {
            writer.WriteStartDocument();
            writer.WriteDocType("coverage", null, "http://cobertura.sourceforge.net/xml/coverage-04.dtd", null);

            writer.WriteStartElement("coverage");
            writer.WriteAttributeString("line-rate", lineRate.ToString("F4", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("branch-rate", "0");
            writer.WriteAttributeString("lines-covered", coveredLines.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("lines-valid", totalLines.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("version", "1.0");
            writer.WriteAttributeString("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            writer.WriteStartElement("sources");
            writer.WriteStartElement("source");
            writer.WriteString(".");
            writer.WriteEndElement();
            writer.WriteEndElement(); // sources

            writer.WriteStartElement("packages");
            writer.WriteStartElement("package");
            writer.WriteAttributeString("name", "al-source");
            writer.WriteAttributeString("line-rate", lineRate.ToString("F4", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("branch-rate", "0");

            writer.WriteStartElement("classes");
            foreach (var (file, lines) in byFile.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(file);
                int fCovered = lines.Count(kv => kv.Value > 0);
                double fRate = lines.Count > 0 ? (double)fCovered / lines.Count : 0;

                writer.WriteStartElement("class");
                writer.WriteAttributeString("name", Path.GetFileNameWithoutExtension(fileName));
                writer.WriteAttributeString("filename", file);
                writer.WriteAttributeString("line-rate", fRate.ToString("F4", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("branch-rate", "0");

                writer.WriteStartElement("lines");
                foreach (var (lineNum, hits) in lines)
                {
                    writer.WriteStartElement("line");
                    writer.WriteAttributeString("number", lineNum.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("hits", hits.ToString(CultureInfo.InvariantCulture));
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

        return fileCoverages;
    }

    /// <summary>Console table rows: `  <file>  <covered>/<total>  <pct>%` plus a TOTAL row.
    /// Both the file-name and TOTAL rows start with two spaces then an uppercase letter,
    /// matching the grep pattern the coverage-demo workflow extracts for its job summary.</summary>
    public static string FormatConsoleTable(IReadOnlyList<FileCoverage> files)
    {
        var sb = new StringBuilder();
        int totalCovered = 0, totalLines = 0;
        foreach (var f in files.OrderBy(f => f.FilePath, StringComparer.Ordinal))
        {
            totalCovered += f.CoveredLines;
            totalLines += f.TotalLines;
            double pct = f.TotalLines > 0 ? 100.0 * f.CoveredLines / f.TotalLines : 0;
            sb.Append("  ").Append(Path.GetFileName(f.FilePath).PadRight(40))
              .Append(f.CoveredLines).Append('/').Append(f.TotalLines).Append("  ")
              .Append(pct.ToString("F1", CultureInfo.InvariantCulture)).Append('%').Append('\n');
        }
        double totalPct = totalLines > 0 ? 100.0 * totalCovered / totalLines : 0;
        sb.Append("  TOTAL").Append(new string(' ', 34))
          .Append(totalCovered).Append('/').Append(totalLines).Append("  ")
          .Append(totalPct.ToString("F1", CultureInfo.InvariantCulture)).Append('%');
        return sb.ToString();
    }
}
