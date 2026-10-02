// JobsUnitSplitTests — the pure parts of `--jobs` handing a shared bundle's test codeunits to
// several workers first come, first served (#5130), and the merged JUnit report (#5129 item 1).
// The end-to-end half, which spawns real workers, is JobsUnitClaimEndToEndTests.

using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JobsUnitSplitTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("jobsunit-");

    public JobsUnitSplitTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── when a bundle may be shared ──────────────────────────────────────────────────────────

    [Fact]
    public void SplitRefusal_CodeunitAndTestIsolation_MaySplit()
    {
        Assert.Null(ParallelFanOut.SplitRefusal(TestIsolation.Codeunit, false, false));
        Assert.Null(ParallelFanOut.SplitRefusal(TestIsolation.Test, false, false));
    }

    /// <summary>Disabled isolation keeps state across every test, so a bundle keeps its order on one
    /// worker. Each refusal names its flag so the one who passed it can see why nothing split.</summary>
    [Fact]
    public void SplitRefusal_NamesTheReason_ForDisabledIsolationAndForTheBundleWideChecks()
    {
        Assert.Contains("disabled", ParallelFanOut.SplitRefusal(TestIsolation.Disabled, false, false));
        Assert.Contains("--count-baseline", ParallelFanOut.SplitRefusal(TestIsolation.Codeunit, true, false));
        Assert.Contains("--expectations-require-match",
            ParallelFanOut.SplitRefusal(TestIsolation.Codeunit, false, true));
    }

    [Fact]
    public void PlanBundles_RefusedRun_NeverSplits_EvenForAHeavyBundle()
    {
        var bundle = Path.Combine(_dir, "heavy");
        Directory.CreateDirectory(bundle);
        for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(bundle, $"f{i}.al"), "");

        Assert.NotEmpty(ParallelFanOut.PlanBundles(new[] { bundle }, 4, null).SplitBundles);
        var refused = ParallelFanOut.PlanBundles(new[] { bundle }, 4, "reason");
        Assert.Empty(refused.SplitBundles);
        Assert.Single(refused.Shards);
    }

    // ── what the workers share out ───────────────────────────────────────────────────────────

    private sealed class TestAttribute : Attribute { }

    public sealed class Codeunit100 { [Test] public void A() { } }
    public sealed class Codeunit101 { [Test] public void A() { } [Test] public void B() { } [Test] public void C() { } }
    public sealed class Codeunit099 { [Test] public void A() { } [Test] public void B() { } [Test] public void C() { } }
    public sealed class Codeunit102 { [Test] public void A() { } [Test] public void B() { } }
    public sealed class NotATestCodeunit { public void A() { } }

    /// <summary>Largest first, so the last claim is a small one and no worker is left holding the
    /// longest unit while the rest idle; ties by name so every worker derives the SAME order.</summary>
    [Fact]
    public void OrderLargestFirst_ByTestCount_TiesByName_NonTestTypesLast()
    {
        var input = new[]
        {
            typeof(NotATestCodeunit), typeof(Codeunit100), typeof(Codeunit101),
            typeof(Codeunit102), typeof(Codeunit099),
        };

        var ordered = TestExecutor.OrderLargestFirst(input).Select(t => t.Name).ToArray();

        Assert.Equal(new[] { "Codeunit099", "Codeunit101", "Codeunit102", "Codeunit100", "NotATestCodeunit" }, ordered);
        // the same answer from any input order
        var reversed = TestExecutor.OrderLargestFirst(input.Reverse().ToArray()).Select(t => t.Name).ToArray();
        Assert.Equal(ordered, reversed);
    }

    // ── one missing bundle is one missing bundle ─────────────────────────────────────────────

    [Fact]
    public void ExtraSightings_WhenEverySharingWorkerPrintedTheHeader_CountsOneBundleOnce()
    {
        var header = "=== erm — COMPILE FAIL ===";
        var outputs = new[] { header + "\n", header + "\n", "" };

        Assert.Equal(1, ParallelFanOut.ExtraSightings(outputs, new[] { 0, 1 }, header));
    }

    /// <summary>A worker that did not print it did not fail on it: taking its sighting back out
    /// would under-report a real loss.</summary>
    [Fact]
    public void ExtraSightings_WhenOneSharingWorkerDidNotPrintIt_TakesNothingBack()
    {
        var header = "=== erm — COMPILE FAIL ===";

        Assert.Equal(0, ParallelFanOut.ExtraSightings(new[] { header, "ok" }, new[] { 0, 1 }, header));
        Assert.Equal(0, ParallelFanOut.ExtraSightings(new[] { header }, new[] { 0 }, header));
    }

    // ── the merged JUnit report ──────────────────────────────────────────────────────────────

    private string Shard(string name, string xml)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, xml);
        return p;
    }

    [Fact]
    public void WriteMergedJUnit_HoldsEveryShardsSuites_WithSummedTotals()
    {
        var a = Shard("a.xml", "<testsuites><testsuite name=\"Codeunit1\" tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\" time=\"1.500\">"
            + "<testcase name=\"T1\" classname=\"Codeunit1\" time=\"1.000\" />"
            + "<testcase name=\"T2\" classname=\"Codeunit1\" time=\"0.500\"><failure message=\"boom\" /></testcase></testsuite></testsuites>");
        var b = Shard("b.xml", "<testsuites><testsuite name=\"Codeunit2\" tests=\"3\" failures=\"0\" errors=\"1\" skipped=\"1\" time=\"2.000\">"
            + "<testcase name=\"T1\" classname=\"Codeunit2\" time=\"2.000\" /></testsuite></testsuites>");
        var merged = Path.Combine(_dir, "nested", "merged.xml");

        JUnitReport.WriteMergedJUnit(merged, new[] { a, b });

        var root = XDocument.Load(merged).Root!;
        Assert.Equal("5", root.Attribute("tests")!.Value);
        Assert.Equal("1", root.Attribute("failures")!.Value);
        Assert.Equal("1", root.Attribute("errors")!.Value);
        Assert.Equal("1", root.Attribute("skipped")!.Value);
        Assert.Equal("3.500", root.Attribute("time")!.Value);
        Assert.Equal(new[] { "Codeunit1", "Codeunit2" }, root.Elements("testsuite").Select(e => e.Attribute("name")!.Value).ToArray());
        Assert.Equal("boom", root.Descendants("failure").Single().Attribute("message")!.Value);
    }

    /// <summary>Not a quiet skip: the shard's tests are missing from the totals, and the report has
    /// to say so, or a worker that died reads as a worker that ran nothing.</summary>
    [Fact]
    public void WriteMergedJUnit_NamesAShardThatWroteNothingReadable()
    {
        var a = Shard("a.xml", "<testsuites><testsuite name=\"Codeunit1\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" time=\"1.000\" /></testsuites>");
        var torn = Shard("torn.xml", "<testsuites><testsuite name=");
        var absent = Path.Combine(_dir, "absent.xml");
        var merged = Path.Combine(_dir, "merged.xml");

        JUnitReport.WriteMergedJUnit(merged, new[] { a, torn, absent });

        var text = File.ReadAllText(merged);
        Assert.Contains("shard 1 wrote no readable JUnit file", text);
        Assert.Contains("shard 2 wrote no readable JUnit file", text);
        Assert.DoesNotContain("shard 0 wrote no readable", text);
    }

    /// <summary>A shard's lost-suite comment is how a report says a bundle did not compile; dropping
    /// it in the merge would turn a partial run into a clean-looking one.</summary>
    [Fact]
    public void WriteMergedJUnit_KeepsEachShardsOwnComments()
    {
        var a = Shard("a.xml", "<testsuites><!-- 2 suite(s) in /b/erm did not compile and are MISSING --></testsuites>");

        JUnitReport.WriteMergedJUnit(Path.Combine(_dir, "m.xml"), new[] { a });

        Assert.Contains("did not compile and are MISSING", File.ReadAllText(Path.Combine(_dir, "m.xml")));
    }
}
