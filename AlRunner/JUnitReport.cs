using System.Text;
using System.Xml;
using AlRunner;

/// <summary>
/// Generates JUnit XML test reports from AL Runner test results.
///
/// JUnit XML is the industry standard for CI test reporting. GitHub Actions,
/// Azure DevOps, and GitLab CI all natively render JUnit XML as test
/// annotations, summaries, and trend graphs.
///
/// Tests are grouped by codeunit name as &lt;testsuite&gt; elements.
/// Real assertion failures use &lt;failure&gt;; runner limitations use &lt;error&gt;.
/// </summary>
public static class JUnitReport
{
    /// <summary>
    /// Write a JUnit XML report to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="outputPath">File path to write the XML to.</param>
    /// <param name="tests">Test results from the pipeline.</param>
    public static void WriteJUnit(string outputPath, IEnumerable<TestResult> tests)
    {
        var testList = tests.ToList();

        // Group tests by codeunit name (suite)
        var suites = testList
            .GroupBy(t => t.CodeunitName ?? t.Name)
            .OrderBy(g => g.Key)
            .ToList();

        double totalSeconds = testList.Sum(t => t.DurationMs) / 1000.0;
        int totalTests = testList.Count;
        int totalFailures = testList.Count(t => t.Status == TestStatus.Fail);
        int totalErrors = testList.Count(t => t.Status == TestStatus.Error);

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
        writer.WriteAttributeString("time", totalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

        foreach (var suite in suites)
        {
            var suiteTests = suite.ToList();
            double suiteSeconds = suiteTests.Sum(t => t.DurationMs) / 1000.0;
            int suiteFailures = suiteTests.Count(t => t.Status == TestStatus.Fail);
            int suiteErrors = suiteTests.Count(t => t.Status == TestStatus.Error);

            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", suite.Key);
            writer.WriteAttributeString("tests", suiteTests.Count.ToString());
            writer.WriteAttributeString("failures", suiteFailures.ToString());
            writer.WriteAttributeString("errors", suiteErrors.ToString());
            writer.WriteAttributeString("time", suiteSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

            foreach (var test in suiteTests)
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("name", test.Name);
                writer.WriteAttributeString("classname", suite.Key);
                writer.WriteAttributeString("time", (test.DurationMs / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

                if (test.Status == TestStatus.Fail)
                {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("message", test.Message ?? "Test failed");
                    if (test.Message != null || test.StackTrace != null)
                        writer.WriteString(BuildBody(test));
                    writer.WriteEndElement(); // failure
                }
                else if (test.Status == TestStatus.Error)
                {
                    writer.WriteStartElement("error");
                    writer.WriteAttributeString("message", test.Message ?? "Runner error");
                    if (test.Message != null || test.StackTrace != null)
                        writer.WriteString(BuildBody(test));
                    writer.WriteEndElement(); // error
                }

                writer.WriteEndElement(); // testcase
            }

            writer.WriteEndElement(); // testsuite
        }

        writer.WriteEndElement(); // testsuites
    }

    private static string BuildBody(TestResult test)
    {
        if (test.StackTrace == null)
            return test.Message ?? "";
        return $"{test.Message}\n\n{test.StackTrace}";
    }
}
