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

    // #5439: the selection text and the three causes of an empty selection each say which one it was.
    [Fact]
    public void SelectionText_NamesEverySelector()
    {
        Assert.Equal("--test 'A'", TestSelectionAudit.SelectionText("A", Array.Empty<string>()));
        Assert.Equal("--test-exact 'X.Y', 'X.Z'", TestSelectionAudit.SelectionText(null, new[] { "X.Y", "X.Z" }));
        Assert.Equal("--test 'A' with --test-exact 'X.Y'", TestSelectionAudit.SelectionText("A", new[] { "X.Y" }));
    }

    [Fact]
    public void Describe_PatternOnly_KeepsTheOriginalText()
        => Assert.Equal(TestSelectionAudit.Describe("Nope"),
            TestSelectionAudit.Describe("Nope", Array.Empty<string>(), excludedAll: false, excludesInEffect: false));

    [Fact]
    public void Describe_Exact_SaysWholeNameNotSubstring()
    {
        var msg = TestSelectionAudit.Describe(null, new[] { "Codeunit1.X" }, excludedAll: false, excludesInEffect: false);
        Assert.Contains("--test-exact 'Codeunit1.X' selected no test in this run", msg);
        Assert.Contains("WHOLE qualified name", msg);
        Assert.DoesNotContain("--exclude-test", msg);
    }

    [Fact]
    public void Describe_ExcludedAll_BlamesTheExclusionNotTheName()
    {
        var msg = TestSelectionAudit.Describe("Alpha", Array.Empty<string>(), excludedAll: true, excludesInEffect: false);
        Assert.Contains("--exclude-test names every one of them, so nothing is left to run", msg);
        Assert.DoesNotContain("selected no test in this run", msg);
    }

    [Fact]
    public void Describe_ExcludesInEffect_HedgesAboutTheCause()
    {
        var msg = TestSelectionAudit.Describe("Alpha", Array.Empty<string>(), excludedAll: false, excludesInEffect: true);
        Assert.Contains("--test 'Alpha' selected no test in this run", msg);
        Assert.Contains("--exclude-test is also in effect", msg);
    }
}
