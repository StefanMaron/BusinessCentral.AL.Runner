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
        // Paired with its bucket rather than flattened (#2919). `suspect` is a property of the
        // (bucket, test) PAIR, and the grouping below is by codeunit, so two bundles that share
        // a codeunit name land in one <testsuite> — asking the question per suite would then
        // answer it for whichever bucket happened to come first. Same change #2898 made to
        // SerializeJsonOutput and PrintFailureClassification, for the same reason.
        var tests = buckets
            .Where(b => b.Stage == BucketStage.Ran)
            .SelectMany(b => b.Tests.Select(t => (Bucket: b, Test: t)))
            .ToList();

        var suites = tests
            .GroupBy(x => x.Test.Codeunit)
            .OrderBy(g => g.Key)
            .ToList();

        double totalSeconds = tests.Sum(x => x.Test.Duration.TotalSeconds);
        long totalTests = tests.Count;
        long totalFailures = tests.Count(x => x.Test.Outcome == TestOutcome.Fail);
        long totalErrors = tests.Count(x => x.Test.Outcome == TestOutcome.Error);
        long totalSkipped = tests.Count(x => x.Test.Outcome == TestOutcome.Skipped);

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

        WriteCompanyInitComments(writer, buckets);

        WriteLostSuiteComments(writer, buckets);

        foreach (var suite in suites)
        {
            var suiteTests = suite.ToList();
            double suiteSeconds = suiteTests.Sum(x => x.Test.Duration.TotalSeconds);
            int suiteFailures = suiteTests.Count(x => x.Test.Outcome == TestOutcome.Fail);
            int suiteErrors = suiteTests.Count(x => x.Test.Outcome == TestOutcome.Error);
            int suiteSkipped = suiteTests.Count(x => x.Test.Outcome == TestOutcome.Skipped);

            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", suite.Key);
            writer.WriteAttributeString("tests", suiteTests.Count.ToString());
            writer.WriteAttributeString("failures", suiteFailures.ToString());
            writer.WriteAttributeString("errors", suiteErrors.ToString());
            writer.WriteAttributeString("skipped", suiteSkipped.ToString());
            writer.WriteAttributeString("time", suiteSeconds.ToString("F3", CultureInfo.InvariantCulture));

            foreach (var (bucket, test) in suiteTests)
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("name", test.Method);
                writer.WriteAttributeString("classname", suite.Key);
                writer.WriteAttributeString("time", test.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));

                if (test.Outcome == TestOutcome.Fail)
                {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("message", test.Message ?? "Test failed");
                    writer.WriteString(BuildBody(bucket, test));
                    writer.WriteEndElement(); // failure
                }
                else if (test.Outcome == TestOutcome.Error)
                {
                    writer.WriteStartElement("error");
                    writer.WriteAttributeString("message", test.Message ?? "Runner error");
                    writer.WriteString(BuildBody(bucket, test));
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

    /// <summary>
    /// An XML comment per bucket that lost one or more suites (#2919), the JUnit counterpart of
    /// the `suiteErrors` array --output-json gained in #2762.
    ///
    /// <para>A lost suite contributes NO <c>testsuite</c> element, so without this the reader of
    /// the XML cannot tell a suite that did not run from one that never existed — and in the
    /// total-loss case the document is a wholly green, wholly empty report. That is the silent
    /// failure `.claude/rules/loud-failures.md` forbids, one surface over.</para>
    ///
    /// <para>A comment rather than a synthetic <c>testsuite</c>, for the same reason the
    /// carried-attempt provenance above is one: every JUnit consumer tolerates a comment, while a
    /// synthetic suite would have to invent an <c>errors</c> count and so would move the very
    /// numbers a dashboard plots. It reports what did not run; it does not restate the run.</para>
    /// </summary>
    private static void WriteLostSuiteComments(XmlWriter writer, IReadOnlyList<BucketResult> buckets)
    {
        foreach (var b in buckets)
        {
            if (b.CompileErrors.Count == 0) continue;

            // Both a Ran bucket that lost SOME suites and a CompileFailed one that lost all of
            // them: neither contributes an element naming what is missing, and Stage is not a
            // distinction the reader of this document can act on.
            var text = new StringBuilder();
            text.Append($" {b.CompileErrors.Count} suite(s) in {b.BucketPath} did not compile "
                + "and are MISSING from this run — the counts above describe only what survived; ");
            // Capped, with a count of what it hid: a silent truncation would be a smaller instance
            // of the defect this comment exists to fix (#2898's argument for BundleProgressLine).
            foreach (var e in b.CompileErrors.Take(3)) text.Append($"[{e}] ");
            if (b.CompileErrors.Count > 3)
                text.Append($"... and {b.CompileErrors.Count - 3} more ");

            // "--" cannot appear inside an XML comment at all, so a compiler message carrying one
            // — `--package-cache`, a rule of dashes in a banner — would make the document
            // malformed rather than merely ugly. Same softening as the carried-file comment.
            writer.WriteComment(text.ToString().Replace("--", "- -"));
        }
    }

    // #3538 — see docs/partial-company-initialization.md for why a comment rather than a
    // synthetic testsuite, which is WriteLostSuiteComments' reasoning above.
    private static void WriteCompanyInitComments(XmlWriter writer, IReadOnlyList<BucketResult> buckets)
    {
        foreach (var b in buckets)
        {
            var failures = b.CompanyInitFailures ?? Array.Empty<CompanyInitFailure>();
            if (failures.Count == 0) continue;

            var text = new StringBuilder();
            text.Append($" company initialization did NOT complete for {b.BucketPath}: the tests "
                + "in this report ran against a PARTIALLY initialized company, so setup rows are "
                + "missing and a failure reading one is caused by this; ");
            foreach (var f in failures)
                text.Append($"[{Reporter.DescribeCompanyInitFailure(f)}] ");

            // "--" cannot appear inside an XML comment, and a BC exception message can carry one.
            writer.WriteComment(text.ToString().Replace("--", "- -"));
        }
    }

    private static string BuildBody(BucketResult bucket, TestResult test)
    {
        // #2240: the missing-test-data explanation goes in the BODY, never into the `message`
        // attribute above — that attribute is BC's own failure text and a CI dashboard groups
        // failures by it, so appending to it would both alter the reported failure and split one
        // cluster into two.
        //
        // #2919 marks a collateral-suspect result the same way and for the same reason, and it is
        // why this needs the bucket: the marker belongs to the (bucket, test) pair. Deliberately
        // NOT a `suspect` attribute and NOT a reclassification — a `<failure>` moved to
        // `<skipped>` to keep `failures` down would make a CI trend line drop for a reporting
        // change, and an attribute outside the schema is what a validating consumer rejects. The
        // counters stay exactly as they were: the marker says this failure MAY be collateral, not
        // that it did not happen.
        var marker = Reporter.IsSuspect(bucket, test)
            ? Reporter.SuspectMarker(bucket)
              + $" {bucket.CompileErrors.Count} suite(s) in this bundle did not compile, so objects "
              + "this test needs may be missing — re-run with the bundle intact before treating "
              + "this as a regression.\n\n"
            : "";

        var head = string.IsNullOrEmpty(test.Diagnosis)
            ? test.Message ?? ""
            : $"{test.Message}\n{test.Diagnosis}";
        var body = test.AlCallStack ?? test.FullException;
        if (body == null) return marker + head;
        return $"{marker}{head}\n\n{body}";
    }
}
