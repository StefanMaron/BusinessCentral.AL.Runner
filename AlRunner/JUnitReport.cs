// JUnitReport — writes AL Runner test results as JUnit XML, the format GitHub
// Actions, Azure DevOps, and GitLab CI natively render as test annotations,
// summaries, and trend graphs. Ported from v1 (AlRunner/JUnitReport.cs) onto
// v2's BucketResult/TestResult shape.
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AlRunner;

public static class JUnitReport
{
    /// <summary>Write a JUnit XML report to <paramref name="outputPath"/>.</summary>
    public static void WriteJUnit(string outputPath, IReadOnlyList<BucketResult> buckets)
        => WriteJUnit(outputPath, buckets, Array.Empty<string>());

    /// <summary>
    /// Write a JUnit XML report to <paramref name="outputPath"/> holding this process's
    /// <paramref name="buckets"/> AND every <c>testsuite</c> from <paramref name="carriedJUnitFiles"/>
    /// — the JUnit files earlier attempts of this same run left behind before a watchdog abort
    /// forced a resume (#2280, passed in as <c>--merge-counts</c>).
    ///
    /// Why the XML must carry them and not only the printed summary (#2716): under <c>--jobs</c>
    /// the parent learns what a worker ran from this file alone (<c>JUnitCounts.Read</c>), and a
    /// resumed worker's final attempt runs only the codeunits no earlier attempt reached. With the
    /// carried cases missing here, the aggregate silently dropped everything the earlier attempts
    /// ran — 26% of the tests on the full BaseApp surface at --jobs 12 — while each worker's own
    /// summary was right. Folding them in makes the XML a complete record of the run, so the
    /// parent needs no knowledge of resume at all, and a single-process <c>--output-junit</c>
    /// after a resume is complete for the same reason.
    ///
    /// No double counting by construction: a resume excludes every codeunit an earlier attempt
    /// ran (AbortResumePlan.NextExclusions), so the carried suites and <paramref name="buckets"/>
    /// are disjoint; and each carried file holds exactly ONE attempt's own results — Program.cs
    /// writes the carry file for an attempt from that attempt's results alone, never through this
    /// overload — so a chain of N resumes contributes each attempt once. Carried suites are copied
    /// verbatim (their failure messages and bodies survive), in attempt order, ahead of this
    /// process's own, each block preceded by an XML comment naming the file it came from.
    ///
    /// An unreadable carried file contributes nothing rather than failing the write, matching
    /// JUnitCounts.Read and ProgramSupport.CarriedFromEarlierAttempts, which already treat such a
    /// file as zero for the printed summary — the two stay in agreement either way.
    /// </summary>
    public static void WriteJUnit(string outputPath, IReadOnlyList<BucketResult> buckets,
        IReadOnlyList<string> carriedJUnitFiles)
    {
        var tests = buckets
            .Where(b => b.Stage == BucketStage.Ran)
            .SelectMany(b => b.Tests)
            .ToList();

        var suites = tests
            .GroupBy(t => t.Codeunit)
            .OrderBy(g => g.Key)
            .ToList();

        double totalSeconds = tests.Sum(t => t.Duration.TotalSeconds);
        long totalTests = tests.Count;
        long totalFailures = tests.Count(t => t.Outcome == TestOutcome.Fail);
        long totalErrors = tests.Count(t => t.Outcome == TestOutcome.Error);
        long totalSkipped = tests.Count(t => t.Outcome == TestOutcome.Skipped);

        var carried = LoadCarriedSuites(carriedJUnitFiles);
        foreach (var (_, carriedSuites) in carried)
            foreach (var cs in carriedSuites)
            {
                totalTests += Attr(cs, "tests");
                totalFailures += Attr(cs, "failures");
                totalErrors += Attr(cs, "errors");
                totalSkipped += Attr(cs, "skipped");
                totalSeconds += Seconds(cs);
            }

        using var writer = XmlWriter.Create(outputPath, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false)
        });

        writer.WriteStartDocument();
        writer.WriteStartElement("testsuites");
        writer.WriteAttributeString("tests", totalTests.ToString());
        writer.WriteAttributeString("failures", totalFailures.ToString());
        writer.WriteAttributeString("errors", totalErrors.ToString());
        writer.WriteAttributeString("skipped", totalSkipped.ToString());
        writer.WriteAttributeString("time", totalSeconds.ToString("F3", CultureInfo.InvariantCulture));

        foreach (var (file, carriedSuites) in carried)
        {
            // An XML comment rather than a non-standard attribute: every JUnit consumer tolerates
            // a comment, not every one tolerates an attribute its schema does not name. "--" is
            // illegal inside a comment, so a path containing it is softened.
            writer.WriteComment(" carried from an earlier attempt of this run (watchdog resume, #2280): "
                + file.Replace("--", "- -") + " ");
            foreach (var cs in carriedSuites) cs.WriteTo(writer);
        }

        foreach (var suite in suites)
        {
            var suiteTests = suite.ToList();
            double suiteSeconds = suiteTests.Sum(t => t.Duration.TotalSeconds);
            int suiteFailures = suiteTests.Count(t => t.Outcome == TestOutcome.Fail);
            int suiteErrors = suiteTests.Count(t => t.Outcome == TestOutcome.Error);
            int suiteSkipped = suiteTests.Count(t => t.Outcome == TestOutcome.Skipped);

            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", suite.Key);
            writer.WriteAttributeString("tests", suiteTests.Count.ToString());
            writer.WriteAttributeString("failures", suiteFailures.ToString());
            writer.WriteAttributeString("errors", suiteErrors.ToString());
            writer.WriteAttributeString("skipped", suiteSkipped.ToString());
            writer.WriteAttributeString("time", suiteSeconds.ToString("F3", CultureInfo.InvariantCulture));

            foreach (var test in suiteTests)
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("name", test.Method);
                writer.WriteAttributeString("classname", suite.Key);
                writer.WriteAttributeString("time", test.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));

                if (test.Outcome == TestOutcome.Fail)
                {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("message", test.Message ?? "Test failed");
                    writer.WriteString(BuildBody(test));
                    writer.WriteEndElement(); // failure
                }
                else if (test.Outcome == TestOutcome.Error)
                {
                    writer.WriteStartElement("error");
                    writer.WriteAttributeString("message", test.Message ?? "Runner error");
                    writer.WriteString(BuildBody(test));
                    writer.WriteEndElement(); // error
                }
                else if (test.Outcome == TestOutcome.Skipped)
                {
                    writer.WriteStartElement("skipped");
                    writer.WriteAttributeString("message", test.Message ?? "Skipped by expectations manifest");
                    writer.WriteEndElement(); // skipped
                }

                writer.WriteEndElement(); // testcase
            }

            writer.WriteEndElement(); // testsuite
        }

        writer.WriteEndElement(); // testsuites
    }

    /// <summary>
    /// The <c>testsuite</c> elements of each carried file, in the order the files were given
    /// (attempt order). A file that is missing, truncated — what an attempt killed mid-write
    /// leaves behind — or not JUnit-shaped yields no suites. Only DIRECT children of a
    /// <c>testsuites</c> root (or a bare <c>testsuite</c> root) count: a nested suite would be
    /// written twice, once inside its parent and once on its own.
    /// </summary>
    private static List<(string File, List<XElement> Suites)> LoadCarriedSuites(IReadOnlyList<string> files)
    {
        var result = new List<(string, List<XElement>)>();
        foreach (var f in files)
        {
            if (string.IsNullOrEmpty(f)) continue;
            try
            {
                if (!File.Exists(f)) continue;
                var root = XDocument.Load(f).Root;
                if (root == null) continue;
                var suites = root.Name.LocalName == "testsuite"
                    ? new List<XElement> { root }
                    : root.Elements("testsuite").ToList();
                if (suites.Count > 0) result.Add((f, suites));
            }
            catch
            {
                // Not a verdict on the run — the attempt's exit code and its own printed summary
                // are. Same stance as JUnitCounts.Read.
            }
        }
        return result;
    }

    private static long Attr(XElement el, string name)
        => long.TryParse(el.Attribute(name)?.Value, out var v) ? v : 0;

    private static double Seconds(XElement el)
        => double.TryParse(el.Attribute("time")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string BuildBody(TestResult test)
    {
        // #2240: the missing-test-data explanation goes in the BODY, never into the `message`
        // attribute above — that attribute is BC's own failure text and a CI dashboard groups
        // failures by it, so appending to it would both alter the reported failure and split one
        // cluster into two.
        var head = string.IsNullOrEmpty(test.Diagnosis)
            ? test.Message ?? ""
            : $"{test.Message}\n{test.Diagnosis}";
        var body = test.AlCallStack ?? test.FullException;
        if (body == null) return head;
        return $"{head}\n\n{body}";
    }
}
