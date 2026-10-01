using Xunit;

namespace AlRunner.Tests;

/// <summary>#5110: the reversed-fact-order mode a scheduled leak-detection run relies on.</summary>
public class ReversibleTestCaseOrdererTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData(" Default ")]
    public void DefaultMode_KeepsXunitsOrder(string? mode)
        => Assert.Equal(new[] { "a", "b", "c" }, ReversibleTestCaseOrderer.Apply(new List<string> { "a", "b", "c" }, mode));

    [Theory]
    [InlineData("reverse")]
    [InlineData("REVERSE")]
    public void ReverseMode_ReversesXunitsOrder(string mode)
        => Assert.Equal(new[] { "c", "b", "a" }, ReversibleTestCaseOrderer.Apply(new List<string> { "a", "b", "c" }, mode));

    [Fact]
    public void UnknownMode_Throws_NamingTheVariable()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ReversibleTestCaseOrderer.Apply(new List<string> { "a" }, "reversed"));
        Assert.Contains(ReversibleTestCaseOrderer.EnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Contains("'reversed'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// xUnit catches an orderer exception, logs it as a diagnostic and runs the default order,
    /// so a mistyped value would otherwise run the job it configures in the wrong order and
    /// pass. This fact is the loud half: it fails the run that carries the typo.
    /// </summary>
    [Fact]
    public void ConfiguredMode_ForThisRun_IsKnown()
    {
        var mode = Environment.GetEnvironmentVariable(ReversibleTestCaseOrderer.EnvVar);
        var ex = Record.Exception(() => ReversibleTestCaseOrderer.Apply(new List<int> { 1, 2 }, mode));
        Assert.Null(ex);
    }
}
