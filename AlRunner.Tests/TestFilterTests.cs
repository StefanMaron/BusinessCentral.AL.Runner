using Xunit;

namespace AlRunner.Tests;

// Pins the shape ported from protocol-v2 (#1607/#1641): both fields optional/null
// = no constraint, value equality on the record. Not yet wired into the runTests
// request/executor — see #1641 follow-up.
public class TestFilterTests
{
    [Fact]
    public void DefaultConstruction_BothFieldsNull_MeansNoConstraint()
    {
        var filter = new TestFilter(null, null);
        Assert.Null(filter.CodeunitNames);
        Assert.Null(filter.ProcNames);
    }

    [Fact]
    public void RecordEquality_SameSets_AreEqual()
    {
        var a = new TestFilter(new HashSet<string> { "Foo" }, new HashSet<string> { "Bar" });
        var b = new TestFilter(new HashSet<string> { "Foo" }, new HashSet<string> { "Bar" });
        // Records compare by reference for non-record members (IReadOnlySet<string> is
        // a HashSet<string> instance here) — this pins that behaviour rather than
        // asserting a value-equality guarantee the record does NOT provide.
        Assert.NotEqual(a, b);
        Assert.Equal(a.CodeunitNames, b.CodeunitNames);
    }

    [Fact]
    public void CanConstrainByCodeunitNamesOnly()
    {
        var filter = new TestFilter(new HashSet<string> { "Sales-Post" }, null);
        Assert.Contains("Sales-Post", filter.CodeunitNames!);
        Assert.Null(filter.ProcNames);
    }
}
