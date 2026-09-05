// JUnitCountsTests — the aggregation --jobs depends on (issue #2280).

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class JUnitCountsTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("junitcounts-");

    public JUnitCountsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string xml)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(p, xml);
        return p;
    }

    /// <summary>Suites are summed, not read off a root attribute — several writers omit the
    /// root total, and a shard whose suites were ignored would contribute zero silently.</summary>
    [Fact]
    public void Read_SumsEveryTestsuite()
    {
        var p = Write("""
            <testsuites>
              <testsuite name="A" tests="10" failures="3" errors="1" skipped="0"/>
              <testsuite name="B" tests="5"  failures="1" errors="0" skipped="2"/>
            </testsuites>
            """);

        var t = JUnitCounts.Read(p);

        Assert.Equal(15, t.Tests);
        Assert.Equal(4, t.Failures);
        Assert.Equal(1, t.Errors);
        Assert.Equal(2, t.Skipped);
    }

    /// <summary>A single bare testsuite root, with no testsuites wrapper.</summary>
    [Fact]
    public void Read_HandlesASingleBareTestsuiteRoot()
    {
        var t = JUnitCounts.Read(Write("""<testsuite name="Only" tests="7" failures="2" errors="0"/>"""));

        Assert.Equal(7, t.Tests);
        Assert.Equal(2, t.Failures);
    }

    /// <summary>Missing attributes count as zero rather than throwing — one absent `skipped`
    /// must not discard the whole shard's tests, which is what an exception here would do.</summary>
    [Fact]
    public void Read_TreatsMissingAttributesAsZero_WithoutLosingTheRest()
    {
        var t = JUnitCounts.Read(Write("""<testsuite name="A" tests="4"/>"""));

        Assert.Equal(4, t.Tests);
        Assert.Equal(0, t.Failures);
        Assert.Equal(0, t.Skipped);
    }

    /// <summary>Negative: a missing file is all zeros, not an exception. The shard's EXIT CODE
    /// is what fails the run — see JUnitCounts' header — so this must not throw and take the
    /// parent's aggregate down with it.</summary>
    [Fact]
    public void Read_MissingFile_IsZero_NotAThrow()
    {
        var t = JUnitCounts.Read(Path.Combine(_dir, "does-not-exist.xml"));

        Assert.Equal(0, t.Tests);
    }

    /// <summary>Negative: same for a truncated file, which is what a worker killed mid-write
    /// leaves behind.</summary>
    [Fact]
    public void Read_MalformedXml_IsZero_NotAThrow()
    {
        var t = JUnitCounts.Read(Write("<testsuites><testsuite tests=\"9\""));

        Assert.Equal(0, t.Tests);
    }
}
