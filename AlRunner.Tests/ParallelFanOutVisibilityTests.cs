// ParallelFanOutVisibilityTests — the --jobs plan must not be written as a [Component] tag.
//
// Log's FilteredWriter drops lines matching a leading [Tag] at default verbosity. That has
// already hidden information this repo needed: the "[bc] selected BC" line disappeared and cost
// 42 tests before anyone noticed, and a whole class of diagnostics has been written and never
// seen. The --jobs plan — which bundles went to which worker — is the run's own configuration,
// not debug chatter: a reader who cannot see it cannot tell a slow shard from a mis-split one.
//
// Asserted against the source because the alternative is spawning real worker processes to
// observe a log line, and the claim here is about how the line is WRITTEN.

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class ParallelFanOutVisibilityTests
{
    private static string Source()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir, "AlRunner", "Infrastructure", "ParallelFanOut.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate AlRunner/Infrastructure/ParallelFanOut.cs");
    }

    [Fact]
    public void ThePlanLinesAreNotWrittenWithABracketedComponentTag()
    {
        var src = Source();

        // Console.WriteLine($"[anything] ...") — the shape FilteredWriter suppresses.
        var tagged = Regex.Matches(src, @"Console\.(Write|WriteLine)\(\$?""\[[A-Za-z][^\]]*\]")
            .Select(m => m.Value)
            .ToList();

        Assert.True(tagged.Count == 0,
            "ParallelFanOut writes [Component]-tagged lines, which Log's FilteredWriter drops at "
            + "default verbosity — the --jobs plan would be invisible in a normal run: "
            + string.Join(" | ", tagged));
    }

    /// <summary>Positive: the plan is actually printed, so the test above cannot be satisfied by
    /// simply deleting the output.</summary>
    [Fact]
    public void ThePlanIsPrinted()
    {
        var src = Source();

        Assert.Contains("worker process(es)", src, StringComparison.Ordinal);
        Assert.Matches(@"shard \{i\}", src);
    }
}
