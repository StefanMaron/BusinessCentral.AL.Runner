// AbortResumeArgsTests — the retry command line (issue #2280).
//
// The termination bug this prevents is quiet and expensive: if a previous --resume-aborts pair
// is appended to rather than replaced, the child sees two and the parser takes the last, so the
// budget never counts down and a genuinely stuck run retries until something kills it — each
// attempt paying a full BC boot.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AbortResumeArgsTests
{
    [Fact]
    public void BuildChildArgs_CarriesTheOriginalArgumentsThrough()
    {
        var child = AbortResume.BuildChildArgs(
            new[] { "/b/one", "--test-data", "--jobs", "4" }, new[] { "Codeunit134228" }, 4);

        Assert.Contains("/b/one", child);
        Assert.Contains("--test-data", child);
        Assert.Contains("--jobs", child);
        Assert.Contains("4", child);
    }

    [Fact]
    public void BuildChildArgs_AppendsEveryExclusion()
    {
        var child = AbortResume.BuildChildArgs(
            new[] { "/b/one" }, new[] { "Codeunit134228", "Codeunit134043" }, 3);

        Assert.Equal(2, child.Count(a => a == "--exclude-test"));
        Assert.Contains("Codeunit134228", child);
        Assert.Contains("Codeunit134043", child);
    }

    /// <summary>The termination guarantee: exactly one --resume-aborts, carrying the decremented
    /// budget. Two would leave the parser taking the last and the budget never counting down.</summary>
    [Fact]
    public void BuildChildArgs_LeavesExactlyOneResumeBudget_Decremented()
    {
        var child = AbortResume.BuildChildArgs(
            new[] { "/b/one", "--resume-aborts", "5" }, new[] { "Codeunit1" }, 4);

        Assert.Single(child, a => a == "--resume-aborts");
        var idx = child.IndexOf("--resume-aborts");
        Assert.Equal("4", child[idx + 1]);
        Assert.DoesNotContain("5", child);
    }

    /// <summary>A previous attempt's exclusions are replaced by the accumulated set, not
    /// duplicated — AbortResumePlan already folds the old ones in.</summary>
    [Fact]
    public void BuildChildArgs_ReplacesPreviousExclusionsRatherThanDuplicatingThem()
    {
        var child = AbortResume.BuildChildArgs(
            new[] { "/b/one", "--exclude-test", "Codeunit134228" },
            new[] { "Codeunit134228", "Codeunit134043" }, 3);

        Assert.Equal(2, child.Count(a => a == "--exclude-test"));
        Assert.Equal(1, child.Count(a => a == "Codeunit134228"));
    }

    /// <summary>Negative: a budget of zero is still passed explicitly, so the child knows not to
    /// resume again rather than falling back to the default and starting a fresh chain.</summary>
    [Fact]
    public void BuildChildArgs_PassesAZeroBudgetExplicitly()
    {
        var child = AbortResume.BuildChildArgs(new[] { "/b/one" }, new[] { "Codeunit1" }, 0);

        var idx = child.IndexOf("--resume-aborts");
        Assert.True(idx >= 0);
        Assert.Equal("0", child[idx + 1]);
    }

    /// <summary>Documents the exit-code contract the implementation must hold, and why: a run
    /// that hung and then resumed successfully must NOT report clean success, because the
    /// excluded codeunit's tests never ran. Asserted against the source, since the behaviour
    /// lives in a process-spawning method that a unit test cannot drive; the behavioural cover
    /// is SuiteAbortOnTimeoutTests, which caught this exact defect while resume was written.</summary>
    [Fact]
    public void Rerun_NeverReportsCleanSuccessAfterAnExclusion()
    {
        var dir = AppContext.BaseDirectory;
        string? src = null;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir, "AlRunner", "Infrastructure", "AbortResume.cs");
            if (File.Exists(p)) { src = File.ReadAllText(p); break; }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(src);
        Assert.Contains("p.ExitCode != 0 ? p.ExitCode : 1", src!, StringComparison.Ordinal);
    }
}
