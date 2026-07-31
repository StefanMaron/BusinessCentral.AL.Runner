// LogUserFacingTagsTests — Log's [Component] filter must not eat user-facing output.
//
// Root cause being tested
// -----------------------
// Log.Install() suppresses any line starting with a `[Tag]` unless --verbose, to hide
// internal diagnostics. The BC-version selection lines are written unconditionally and
// were plainly meant to be seen:
//   [bc] no --bc-version given — selecting latest cached BC 28.x ...
//   [bc] selected BC <version> (<path>)
// but `[bc]` matched the generic tag pattern and was NOT exempted, so both vanished at
// default verbosity.
//
// Measured cost, 2026-07-29: the same Pageworks suite scores 1041P/35F/0E with
// `--bc-version 28.1` and 996P/77F/3E with the default selection — 42 tests and 3
// errors on a choice the runner made silently, then declined to mention. An agent
// investigating the difference had no way to see which version was in play and
// attributed the failure to a nonexistent missing-native-method gap.
//
// Selecting a BC version and naming the winning dependency package are results, not
// diagnostics. They stay visible.

using Xunit;

namespace AlRunner.Tests;

public sealed class LogUserFacingTagsTests
{
    private static string FilterOnce(string line, bool verbose)
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var sink = new StringWriter();
        var savedVerbose = Log.Verbose;
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();          // wraps the sink in the filtering writer
            Log.Verbose = verbose;
            Console.Out.WriteLine(line);
            return sink.ToString();
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    [Theory]
    [InlineData("[bc] selected BC 28.1.49838.50794 (/artifacts/28.1)")]
    [InlineData("[bc] no --bc-version given — selecting latest cached BC 28.x")]
    [InlineData("[dep] note: symbols-only package won over a code-bearing one")]
    [InlineData("[layered] building")]   // already exempt; pinned so it stays that way
    [InlineData("[provision] downloading")]
    [InlineData("[watch] waiting")]
    public void UserFacingTags_SurviveTheDefaultFilter(string line)
    {
        Assert.Contains(line, FilterOnce(line, verbose: false));
    }

    /// <summary>
    /// Negative control: genuine internal diagnostics must still be suppressed by
    /// default, or the exemption list means nothing.
    /// </summary>
    [Theory]
    [InlineData("[BcRuntime] applying patch")]
    [InlineData("[Cecil] rewriting NavDialog")]
    [InlineData("[cache] WROTE key=abc")]
    public void InternalTags_AreStillSuppressedByDefault(string line)
    {
        Assert.DoesNotContain(line, FilterOnce(line, verbose: false));
        Assert.Contains(line, FilterOnce(line, verbose: true));
    }
}
