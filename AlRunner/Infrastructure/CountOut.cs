using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>
/// `--count-out`: what this run actually ran, per suite, as a machine-readable document
/// (#3675).
/// </summary>
/// <remarks>
/// <para>The corpus is resolved per run rather than pinned (#3737), so an exact test count
/// committed to <c>tests/expectations/count-baseline/</c> would go stale the moment an
/// upstream corpus PR merged. CI therefore compares each leg's count against the last one a
/// <c>main</c> run recorded — and needs the count in a file to do it.</para>
/// <para><b>Not <c>--out</c>.</b> That document is a failure report — <c>generated</c>,
/// <c>total_failures</c>, <c>classifications</c>, <c>all_failures</c> — and never a list of
/// tests. Pointing the comparison at it produced "no 'tests' array" on every BC leg, so the
/// guard never ran once; caught on run 34412771210. Changing <c>--out</c>'s schema was the
/// alternative and was rejected: it is a public contract with consumers that read it for
/// failure triage, and they would break for a reason unrelated to what they read.</para>
/// <para>The numbers are the SAME tally <c>--count-baseline</c> compares against
/// (<see cref="CountBaselineCheck"/>), so the file a comparison reads and the number the
/// baseline check uses cannot disagree.</para>
/// </remarks>
public static class CountOut
{
    /// <summary>Per-suite counts, keyed the way <c>--count-baseline</c> keys them.</summary>
    /// <param name="buckets">(bundle path, tests in it, app groups in it) per bucket.</param>
    /// <remarks>
    /// The suite key is the bundle directory's BASENAME, which is what
    /// <see cref="CountBaselineManifest"/> matches on — so a suite named here and a suite
    /// named there are the same suite. Two roots resolving to one name ADD, because that is
    /// what a suite split across roots means; overwriting would silently report the last one.
    /// </remarks>
    public static IReadOnlyDictionary<string, SuiteCountActual> Tally(
        IEnumerable<(string BucketPath, int Tests, int AppGroups)> buckets)
    {
        var bySuite = new Dictionary<string, SuiteCountActual>(StringComparer.Ordinal);
        foreach (var (path, tests, groups) in buckets)
        {
            var key = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            bySuite[key] = bySuite.TryGetValue(key, out var prior)
                ? new SuiteCountActual(prior.Tests + tests, prior.AppGroups + groups)
                : new SuiteCountActual(tests, groups);
        }
        return bySuite;
    }

    /// <summary>The document `.github/scripts/compare_corpus_count.py` reads.</summary>
    /// <remarks>
    /// Suites are written in sorted order: the file is cached between runs and diffed, and
    /// dictionary insertion order follows whichever bundle finished first, so two identical
    /// runs would otherwise produce different bytes.
    ///
    /// A run with no suites writes an EMPTY `suites` object rather than omitting it. The
    /// comparison refuses a document with no `suites` key as "could not measure" (exit 3)
    /// and reads an empty one as a real measurement of zero — different facts, and only one
    /// of them is about the corpus (`guards-need-a-third-state.md`).
    /// </remarks>
    public static string Serialize(
        string bcVersion, IReadOnlyDictionary<string, SuiteCountActual> suites)
    {
        var doc = new Dictionary<string, object>
        {
            ["bcVersion"] = bcVersion,
            ["suites"] = suites.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(
                    kv => kv.Key,
                    kv => (object)new Dictionary<string, int>
                    {
                        ["tests"] = kv.Value.Tests,
                        ["appGroups"] = kv.Value.AppGroups,
                    },
                    StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Write the document, creating the directory the caller named.</summary>
    public static void Write(
        string path, string bcVersion, IReadOnlyDictionary<string, SuiteCountActual> suites)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(bcVersion, suites) + Environment.NewLine);
    }
}
