// NclReadRetryCallSiteTests — issue #4264.
//
// BcArtifacts.GetAssemblyNameWithRetry exists because the file it reads is being REPLACED
// while it reads: NclCecilRewrite.RewriteInPlace does a temp-write + rename over
// AppContext.BaseDirectory/Microsoft.Dynamics.Nav.Ncl.dll at every process's own startup, so
// a concurrent al-runner reading the same path lands inside that rename —
// ERROR_SHARING_VIOLATION on Windows, a transient not-found on either platform (#2489).
//
// #2512 added the helper WITH a caller: VerifyEngineConsistency(binDir), which read that file.
// #4039 changed that method to take a variant COUNT instead of a directory, so it stopped
// reading the file at all and the helper's only caller went with it. Nothing failed: an unused
// `internal` method is not a warning, and NclShadowConcurrentStartupTests kept passing because
// it invokes the helper DIRECTLY.
//
// That is the gap this file closes.
//
// KNOWN BLIND SPOT, found in review: the scan is text-anchored on the fully-qualified spelling,
// so a reader reached through an alias -- `using Reflect = System.Reflection;` then
// `Reflect.AssemblyName.GetAssemblyName(p)` -- is NOT reported, and the helperCalls floor below
// does not rescue it because the helper's own two calls are untouched. Measured: green on a
// genuinely unretried read.
//
// Left as a documented limit rather than fixed, and the reason is FAILURE DIRECTION, not
// likelihood. The wider anchor that would catch it -- `.AssemblyName.GetAssemblyName(` without
// the namespace -- was prototyped: it works, and it also reports any unrelated type named
// AssemblyName as an unretried Ncl read. Measured over the 374 .cs files under AlRunner/, the
// wide and narrow patterns return identical counts (2 and 2): zero aliased uses, zero foreign
// AssemblyName types. So BOTH risks are hypothetical and the base rate cannot choose between
// them.
//
// What decides it is which way each is wrong. A missed alias is silent. A false positive is
// loud and lands on someone who wrote an unrelated type, telling them to route an Ncl read they
// never made -- and a guard that misfires is a guard people learn to paste past. With the risks
// equal, prefer the mode that is loud when wrong.
//
// Closing it properly means resolving the symbol, which needs a semantic model rather than the
// syntax tree CSharpSource parses. If this guard ever needs that, #3527 is where it belongs. The existing test proves the retry WORKS; it cannot prove
// anything CALLS it. Those are different claims, and only the second one was lost.

using Xunit;

namespace AlRunner.Tests;

public sealed class NclReadRetryCallSiteTests
{
    private static readonly string RepoRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The file's CODE, with comment and string-literal content blanked by Roslyn
    /// (<see cref="CSharpSource"/>, #3527). Reading raw text would report a prose mention of the
    /// forbidden spelling as a violation — and this test's own subject is a spelling that any
    /// explanatory comment near the call sites would naturally quote. Line offsets are preserved,
    /// so the line numbers this test reports still point at the real source.
    /// </summary>
    private static string BcArtifactsCode() => CSharpSource.ReadCodeOnly(
        System.IO.Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "BcArtifacts.cs"));

    /// <summary>
    /// Every read of an Ncl.dll in BcArtifacts must go through the retry helper.
    ///
    /// Anchored on the BARE API rather than on a list of methods that should use the helper:
    /// a method list goes stale the moment someone adds a seventh reader, which is exactly how
    /// #4264 happened — the guard has to be keyed on the thing being forbidden, not on today's
    /// population of callers.
    /// </summary>
    [Fact]
    public void EveryNclRead_GoesThroughTheRetryHelper()
    {
        var source = BcArtifactsCode();
        var offenders = new System.Collections.Generic.List<string>();
        var readsChecked = 0;

        // The helper's own body legitimately calls the bare API — twice, once in the retry loop
        // and once as the final attempt that is allowed to throw. Bound the helper textually and
        // exclude that range rather than exempting a count, so a THIRD bare call appearing inside
        // it is still reported.
        var helperStart = source.IndexOf("internal static System.Reflection.AssemblyName GetAssemblyNameWithRetry",
            System.StringComparison.Ordinal);
        Assert.True(helperStart >= 0,
            "GetAssemblyNameWithRetry is gone from BcArtifacts.cs; if it was deliberately removed, "
            + "this test and #2489's protection go with it — say so on #4264 rather than deleting this quietly");
        var helperEnd = source.IndexOf("\n    }", helperStart, System.StringComparison.Ordinal);
        Assert.True(helperEnd > helperStart, "could not find the end of GetAssemblyNameWithRetry");

        const string bare = "System.Reflection.AssemblyName.GetAssemblyName(";
        for (var i = source.IndexOf(bare, System.StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(bare, i + 1, System.StringComparison.Ordinal))
        {
            if (i >= helperStart && i <= helperEnd) continue;   // the helper's own two calls
            readsChecked++;

            var lineNo = source[..i].Count(c => c == '\n') + 1;
            var lineStart = source.LastIndexOf('\n', i) + 1;
            var lineEnd = source.IndexOf('\n', i);
            var line = source[lineStart..(lineEnd < 0 ? source.Length : lineEnd)].Trim();
            offenders.Add($"BcArtifacts.cs:{lineNo}: {line}");
        }

        // A population floor, in the direction that can go silently wrong. If the anchor stops
        // matching — the fully-qualified spelling changes to a `using`, say — every read
        // disappears from this scan and it passes having measured nothing. Two is what the
        // helper itself contains and what the exclusion above must therefore have found.
        var helperCalls = System.Text.RegularExpressions.Regex.Matches(
            source[helperStart..helperEnd], System.Text.RegularExpressions.Regex.Escape(bare)).Count;
        Assert.True(helperCalls >= 2,
            $"expected at least 2 bare reads inside GetAssemblyNameWithRetry, found {helperCalls} — "
            + "the anchor stopped matching, so this test measured almost nothing");

        // readsChecked counts the reads OUTSIDE the helper — the population this test is about.
        // It is legitimately zero when every read is routed, which is the fixed state, so there
        // is no floor to assert on it; reporting it in the failure message is what it is for.
        Assert.True(offenders.Count == 0,
            "these Ncl.dll reads use the bare AssemblyName.GetAssemblyName, which has no retry. "
            + "The file is atomically replaced by a concurrently-starting runner's Cecil rewrite, "
            + "so the read can land inside the rename and throw (#2489, #4264). Route them through "
            + "BcArtifacts.GetAssemblyNameWithRetry:"
            + System.Environment.NewLine + string.Join(System.Environment.NewLine, offenders)
            + System.Environment.NewLine + $"({readsChecked} read(s) outside the helper were checked)");

    }
}
