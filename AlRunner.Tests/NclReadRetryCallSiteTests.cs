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
// That is the gap this file closes. The existing test proves the retry WORKS; it cannot prove
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

        Assert.True(offenders.Count == 0,
            "these Ncl.dll reads use the bare AssemblyName.GetAssemblyName, which has no retry. "
            + "The file is atomically replaced by a concurrently-starting runner's Cecil rewrite, "
            + "so the read can land inside the rename and throw (#2489, #4264). Route them through "
            + "BcArtifacts.GetAssemblyNameWithRetry:"
            + System.Environment.NewLine + string.Join(System.Environment.NewLine, offenders));

        _ = readsChecked;
    }
}
