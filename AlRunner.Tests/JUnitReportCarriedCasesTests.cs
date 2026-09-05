// JUnitReportCarriedCasesTests — the JUnit a resumed run writes is the WHOLE run (issue #2716).
//
// A watchdog resume (#2280) re-runs a bundle in a fresh process with every attempted codeunit
// excluded, so the final attempt's own results are a slice. Its printed summary folded the
// earlier attempts' totals in (--merge-counts); its JUnit did not. Under --jobs the parent reads
// only that JUnit, so the aggregate silently lost everything the earlier attempts ran — 26% of
// the tests on the full BaseApp surface at --jobs 12. SuiteAbortOnTimeoutTests proves the
// end-to-end shape with a real hang and a real resume; these pin the writer's mechanism in
// milliseconds: carried suites are copied once, in attempt order, with their detail intact, and
// a run with nothing carried writes byte-identical XML to before.

using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JUnitReportCarriedCasesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "junit-carried-" + Guid.NewGuid().ToString("N"));

    public JUnitReportCarriedCasesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private static TestResult T(string cu, string method, TestOutcome outcome, string? message = null, double secs = 0.5)
        => new(cu, method, outcome, message, outcome == TestOutcome.Pass ? null : "at " + cu + "." + method,
               TimeSpan.FromSeconds(secs));

    private static BucketResult Bucket(params TestResult[] tests)
        => new("/bundle", BucketStage.Ran, Array.Empty<string>(), null, tests,
               TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    private static List<(string Suite, string Name)> Cases(string path)
        => XDocument.Load(path).Descendants("testcase")
            .Select(tc => ((string?)tc.Attribute("classname") ?? "", (string?)tc.Attribute("name") ?? ""))
            .ToList();

    /// <summary>
    /// Positive: a chain of two earlier attempts plus this process's own results. Every case
    /// appears exactly once, earlier attempts first, root totals cover all of it, and the parent's
    /// reader (JUnitCounts.Read — what ParallelFanOut.Run aggregates with) sees the whole worker.
    /// </summary>
    [Fact]
    public void WriteJUnit_FoldsEachCarriedAttemptOnce_InAttemptOrder()
    {
        var attempt1 = P("attempt1.xml");
        JUnitReport.WriteJUnit(attempt1, new[] { Bucket(
            T("CuA", "a1", TestOutcome.Pass),
            T("CuA", "a2", TestOutcome.Fail, "boom: expected 1, got 2")) });
        var attempt2 = P("attempt2.xml");
        JUnitReport.WriteJUnit(attempt2, new[] { Bucket(
            T("CuB", "b1", TestOutcome.Error, "Test exceeded 2s timeout."),
            T("CuB", "b2", TestOutcome.Skipped, "manifest")) });

        var final = P("final.xml");
        JUnitReport.WriteJUnit(final, new[] { Bucket(T("CuC", "c1", TestOutcome.Pass, secs: 2.0)) },
            new[] { attempt1, attempt2 });

        var doc = XDocument.Load(final);
        var root = doc.Root!;
        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal("5", (string?)root.Attribute("tests"));
        Assert.Equal("1", (string?)root.Attribute("failures"));
        Assert.Equal("1", (string?)root.Attribute("errors"));
        Assert.Equal("1", (string?)root.Attribute("skipped"));
        // 4 x 0.5s carried + 2.0s own.
        Assert.Equal("4.000", (string?)root.Attribute("time"));

        Assert.Equal(new[] { "CuA", "CuB", "CuC" },
            root.Elements("testsuite").Select(s => (string?)s.Attribute("name")).ToArray());
        Assert.Equal(new[] { ("CuA", "a1"), ("CuA", "a2"), ("CuB", "b1"), ("CuB", "b2"), ("CuC", "c1") },
            Cases(final).ToArray());

        // Detail survives the copy — a CI dashboard groups failures by this message.
        var a2 = doc.Descendants("testcase").Single(tc => (string?)tc.Attribute("name") == "a2");
        Assert.Equal("boom: expected 1, got 2", (string?)a2.Element("failure")!.Attribute("message"));
        Assert.Contains("at CuA.a2", a2.Element("failure")!.Value);

        // Each carried block is announced, so a reader can tell a resumed record from a clean one.
        var comments = root.Nodes().OfType<XComment>().Select(c => c.Value).ToList();
        Assert.Equal(2, comments.Count);
        Assert.Contains(attempt1, comments[0]);
        Assert.Contains(attempt2, comments[1]);

        var totals = JUnitCounts.Read(final);
        Assert.Equal(5, totals.Tests);
        Assert.Equal(1, totals.Failures);
        Assert.Equal(1, totals.Errors);
        Assert.Equal(1, totals.Skipped);
    }

    /// <summary>
    /// Negative: nothing carried, nothing added. The two-argument overload and an empty list
    /// write byte-identical XML, with only this process's case in it — a run that never resumed
    /// (every --jobs worker that did not hang, every ordinary run) is unchanged.
    /// </summary>
    [Fact]
    public void WriteJUnit_WithNothingCarried_IsByteIdenticalToBefore()
    {
        var buckets = new[] { Bucket(T("CuC", "c1", TestOutcome.Pass), T("CuC", "c2", TestOutcome.Fail, "x")) };
        JUnitReport.WriteJUnit(P("plain.xml"), buckets);
        JUnitReport.WriteJUnit(P("empty-carry.xml"), buckets, Array.Empty<string>());

        Assert.Equal(File.ReadAllText(P("plain.xml")), File.ReadAllText(P("empty-carry.xml")));
        Assert.Equal(new[] { ("CuC", "c1"), ("CuC", "c2") }, Cases(P("plain.xml")).ToArray());
        Assert.Equal("2", (string?)XDocument.Load(P("plain.xml")).Root!.Attribute("tests"));
        Assert.Empty(XDocument.Load(P("plain.xml")).Root!.Nodes().OfType<XComment>());
    }

    /// <summary>
    /// Negative: a missing or truncated carried file — what an attempt killed mid-write leaves —
    /// contributes nothing and does not take this process's own results down with it. Same
    /// stance as JUnitCounts.Read and CarriedFromEarlierAttempts, so the XML and the printed
    /// summary agree on such a file.
    /// </summary>
    [Fact]
    public void WriteJUnit_UnreadableCarriedFile_ContributesNothing_KeepsOwnResults()
    {
        var truncated = P("truncated.xml");
        File.WriteAllText(truncated, "<testsuites><testsuite name=\"Gone\" tests=\"9\"");
        var good = P("good.xml");
        JUnitReport.WriteJUnit(good, new[] { Bucket(T("CuA", "a1", TestOutcome.Pass)) });

        var final = P("final.xml");
        JUnitReport.WriteJUnit(final, new[] { Bucket(T("CuC", "c1", TestOutcome.Pass)) },
            new[] { P("does-not-exist.xml"), truncated, good });

        Assert.Equal(new[] { ("CuA", "a1"), ("CuC", "c1") }, Cases(final).ToArray());
        Assert.Equal(2, JUnitCounts.Read(final).Tests);
        Assert.Single(XDocument.Load(final).Root!.Nodes().OfType<XComment>());
    }

    /// <summary>
    /// A carried file whose root is a bare testsuite (no testsuites wrapper) is still one suite,
    /// copied once — the same shape JUnitCounts.Read accepts, so the two never disagree on it.
    /// </summary>
    [Fact]
    public void WriteJUnit_CarriedBareTestsuiteRoot_IsCopiedOnce()
    {
        var bare = P("bare.xml");
        File.WriteAllText(bare, """
            <testsuite name="Only" tests="2" failures="1" errors="0" skipped="0" time="1.5">
              <testcase name="t1" classname="Only" time="1.0"/>
              <testcase name="t2" classname="Only" time="0.5"><failure message="f"/></testcase>
            </testsuite>
            """);

        var final = P("final.xml");
        JUnitReport.WriteJUnit(final, Array.Empty<BucketResult>(), new[] { bare });

        var root = XDocument.Load(final).Root!;
        Assert.Equal("2", (string?)root.Attribute("tests"));
        Assert.Equal("1", (string?)root.Attribute("failures"));
        Assert.Equal("1.500", (string?)root.Attribute("time"));
        Assert.Equal(new[] { ("Only", "t1"), ("Only", "t2") }, Cases(final).ToArray());
    }
}
