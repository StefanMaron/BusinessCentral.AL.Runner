using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// #4055: the pure half of the --test no-match guard. The runner-spawning half is in
// TestFilterFlagTests.
public sealed class TestSelectionAuditTests
{
    [Fact]
    public void ReadWorkerLines_NoLine_IsNull_NotZero()
    {
        Assert.Null(TestSelectionAudit.ReadWorkerLines("PASS  Codeunit1.X\nTests: 0 total\n"));
        Assert.Null(TestSelectionAudit.ReadWorkerLines(""));
        Assert.Null(TestSelectionAudit.ReadWorkerLines(null));
    }

    [Fact]
    public void ReadWorkerLines_ReportedZero_IsZero()
    {
        Assert.Equal(0L, TestSelectionAudit.ReadWorkerLines(TestSelectionAudit.FormatWorkerLine(0) + "\r\n"));
    }

    [Fact]
    public void ReadWorkerLines_SumsEveryLine()
    {
        var output = $"x\n{TestSelectionAudit.FormatWorkerLine(3)}\ny\n{TestSelectionAudit.FormatWorkerLine(4)}\n";
        Assert.Equal(7L, TestSelectionAudit.ReadWorkerLines(output));
    }

    [Theory]
    [InlineData("Codeunit134335.*LogEntry", true)]
    [InlineData("Alpha*Check", true)]
    [InlineData("*Alpha*", false)]
    [InlineData("Nope", false)]
    public void Describe_NamesThePattern_AndExplainsInteriorStarOnlyWhenPresent(string pattern, bool hint)
    {
        var msg = TestSelectionAudit.Describe(pattern);
        Assert.Contains($"--test '{pattern}' selected no test in this run", msg);
        Assert.Equal(hint, msg.Contains("an interior '*' is matched literally"));
    }
}
