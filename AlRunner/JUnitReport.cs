// JUnitReport — writes AL Runner test results as JUnit XML, the format GitHub
// Actions, Azure DevOps, and GitLab CI natively render as test annotations,
// summaries, and trend graphs. Ported from v1 (AlRunner/JUnitReport.cs) onto
// v2's BucketResult/TestResult shape.
using System.Text;
using System.Xml;

namespace AlRunner;

public static class JUnitReport
{
    /// <summary>Write a JUnit XML report to <paramref name="outputPath"/>.</summary>
    public static void WriteJUnit(string outputPath, IReadOnlyList<BucketResult> buckets)
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
        int totalTests = tests.Count;
        int totalFailures = tests.Count(t => t.Outcome == TestOutcome.Fail);
        int totalErrors = tests.Count(t => t.Outcome == TestOutcome.Error);
        int totalSkipped = tests.Count(t => t.Outcome == TestOutcome.Skipped);

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
        writer.WriteAttributeString("time", totalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

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
            writer.WriteAttributeString("time", suiteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

            foreach (var test in suiteTests)
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("name", test.Method);
                writer.WriteAttributeString("classname", suite.Key);
                writer.WriteAttributeString("time", test.Duration.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

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

    private static string BuildBody(TestResult test)
    {
        var body = test.AlCallStack ?? test.FullException;
        if (body == null) return test.Message ?? "";
        return $"{test.Message}\n\n{body}";
    }
}
