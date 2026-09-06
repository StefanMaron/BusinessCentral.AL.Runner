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

// Serial: this class swaps the process-wide Console writers and Log.Verbose. See
// ConsoleFilterSerialCollection for the parallelism bug that made a [watch] case fail.
[Collection(ConsoleFilterSerialCollection.Name)]
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
    // #2034: NclShadowRuntime's re-exec explanation was tagged [Cecil] (suppressed by
    // default, same class of bug as the [bc] swallow above) so a process silently
    // launching a child had no explanation at default verbosity. re-exec explanations
    // now use their own exempted tag.
    [InlineData("[reexec] Ncl.dll not shipped in this install — re-execing into a shadow runtime dir that has it")]
    // #1642: --dap's "listening on" line is the only readiness signal a DAP client (or
    // a human at a terminal) has that the runner will accept a connection — caught by
    // DapClient's own test harness timing out waiting for a line that was actually
    // printed, just silently dropped before reaching stdout (same failure shape as the
    // [bc] swallow above).
    [InlineData("[dap] listening on 127.0.0.1:4711 — waiting for a debug client to connect...")]
    // #2206: the warning naming a directory the .alpackages scan could not read. Its whole
    // purpose is to stop a permissions problem from surfacing later as a mysterious missing
    // dependency, so being eaten here makes the fix a no-op at default verbosity — the same
    // shape as the [bc] and [expectations] swallows above.
    [InlineData("[warn] skipped 1 unreadable directory while searching for `.alpackages`:")]
    // #2750: provisioning-gap lines survive because the HYPHEN makes them fail the tag
    // pattern ([A-Za-z0-9._+] does not include `-`), not because `provision-gap` is on the
    // exemption list. That is load-bearing and easy to break by "tidying" the character
    // class, so it is pinned here rather than left to a comment. A corrupt .deps-bin sidecar
    // is reported with this tag precisely so it cannot be eaten the way `[deps]` was.
    [InlineData("[provision-gap] 'AL Runner Fixtures Sidecar Dep' v1.0.0.0 has a precompiled sidecar DLL that could not be loaded.")]
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
    // #2750: `deps` is deliberately NOT exempt. It is a different category from the exempt
    // `dep`: DependencyLoader's tier-by-tier loading internals (source-cache HIT/WROTE,
    // compile timings, chunk counts) rather than DependencyResolver's resolution results.
    // Exempting it to surface one corrupt-sidecar message would have surfaced all of this.
    [InlineData("[deps] source-cache HIT: Sidecar Dep v1.0.0.0 key=0123456789ab")]
    public void InternalTags_AreStillSuppressedByDefault(string line)
    {
        Assert.DoesNotContain(line, FilterOnce(line, verbose: false));
        Assert.Contains(line, FilterOnce(line, verbose: true));
    }
}
