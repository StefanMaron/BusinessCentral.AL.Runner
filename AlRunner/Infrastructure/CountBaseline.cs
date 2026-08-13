// CountBaseline — issue #1880: --strict fails a run when a test FAILS, but nothing
// asserts that the expected NUMBER of tests actually ran. A bundle that silently
// stops being discovered (a dependency rename, a duplicate app-id collision — #1850,
// an app group dropped from a run — #1861) produces a smaller-but-still-green run:
// every survivor still passes, so --strict's fail-count gate never fires and the CI
// leg reports success on strictly less coverage than it had before.
//
// This is a runner-owned count baseline, opt-in via `--count-baseline PATH`. It is
// deliberately a SEPARATE schema from tests/expectations/, not folded into
// ExpectationManifest: expectations declare the expected CLASSIFICATION of one named
// test; this declares the expected aggregate COUNT of a whole suite. Mixing "what
// should test X do" with "how many tests should suite Y have" in one loader would
// make both harder to read, and the two drift independently (a classification entry
// changes when a single test's behaviour is reclassified; a count baseline changes
// when the corpus grows or shrinks).
//
// Semantics (see the issue for the full design rationale, and PR #1882's review for
// why this is an EXACT match rather than a floor):
//   - FAIL LOUD on ANY MISMATCH: actual != the expected count for that
//     suite+metric+BC-version, in EITHER direction. A drop (actual < expected) is
//     the "a bundle silently stopped being discovered" scenario the issue exists to
//     catch. Growth (actual > expected) must ALSO fail: if it didn't, a grown suite
//     would print a notice on an otherwise-green run, nobody reads stderr on green,
//     the baseline goes stale, and a LATER real drop can land above the stale
//     (too-low) number and pass unnoticed — the guard silently stops guarding itself.
//     Making both directions hard mirrors tests/expectations/ drift, which is loud
//     in both directions; the only difference here is both directions are also a
//     hard failure, not just one.
//   - Per-BC-version counts can legitimately differ and must be handled explicitly,
//     not by taking a single expected value across all versions. Verified against
//     the live matrix: tests/runner-extras currently reports 110 tests on BC
//     27.0/27.3/27.5 and 116 on BC 28.0/28.1/28.2/28.3/28.4 (some AL surfaces are
//     gated on preprocessor symbols only defined from 28.0 on). A single global
//     expected count of 110 would fail every 28.x leg; a single global expected
//     count of 116 would fail every 27.x leg. A `byBcVersion` override table avoids
//     both: `default` is the expected count for every version not explicitly listed.
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>
/// Expected count for one metric (tests or app groups) of one suite, with an
/// optional per-BC-version override table. <see cref="Resolve"/> picks the
/// BC-version-specific expected count when declared for the given key, else
/// <see cref="Default"/>. A run whose actual count differs from the resolved value
/// in EITHER direction is a mismatch — see <see cref="CountBaselineCheck"/>.
/// </summary>
public sealed record ExpectedCount(int Default, IReadOnlyDictionary<string, int>? ByBcVersion)
{
    public int Resolve(string? bcVersionKey) =>
        bcVersionKey != null && ByBcVersion != null && ByBcVersion.TryGetValue(bcVersionKey, out var v)
            ? v
            : Default;
}

/// <summary>Baseline for one suite: an expected count for its test count and/or its app-group count. Either may be omitted.</summary>
public sealed record SuiteCountBaseline(ExpectedCount? Tests, ExpectedCount? AppGroups);

/// <summary>Actual counts observed for one suite in this run.</summary>
public sealed record SuiteCountActual(int Tests, int AppGroups);

/// <summary>One drop or growth finding — a suite+metric whose actual count diverged from its baseline.</summary>
public sealed record CountBaselineFinding(string Suite, string Metric, int Expected, int Actual, string? BcVersionKey)
{
    public override string ToString()
    {
        var ver = BcVersionKey != null ? $" (BC {BcVersionKey})" : "";
        return $"suite '{Suite}' {Metric} count: expected {Expected}, actual {Actual}{ver}";
    }
}

/// <summary>
/// Loaded view of a count-baseline JSON file. Schema:
/// <code>
/// {
///   "suites": {
///     "&lt;suite-key&gt;": {
///       "tests":     { "default": N, "byBcVersion": { "27.0": M, ... } },
///       "appGroups": { "default": N, "byBcVersion": { "27.0": M, ... } }
///     }
///   }
/// }
/// </code>
/// `suite-key` is matched against the basename of the bundle directory passed on the
/// command line (e.g. `tests/al-language/tests/al-language` → `al-language`,
/// `tests/runner-extras` → `runner-extras`) — the same convention CI already uses for
/// `--out &lt;name&gt;-results.json`. Both `tests` and `byBcVersion` are optional; a
/// suite must declare at least one of `tests` / `appGroups`.
/// </summary>
public sealed class CountBaselineManifest
{
    public IReadOnlyDictionary<string, SuiteCountBaseline> Suites { get; }

    private CountBaselineManifest(IReadOnlyDictionary<string, SuiteCountBaseline> suites) => Suites = suites;

    /// <summary>
    /// Load and parse <paramref name="path"/>. Throws loudly on a missing file or a
    /// schema violation. Unlike --expectations' auto-probed default, --count-baseline
    /// is explicit-only (never silently activates for an invocation that didn't ask
    /// for it — see Program.cs for why: the same corpus root is also invoked with a
    /// narrowing --test filter, e.g. the xmlport-isolation CI leg, and a baseline
    /// built for the FULL corpus must never fire there), so a typo or malformed file
    /// must abort the run rather than silently disable the guard it was supposed to
    /// install.
    /// </summary>
    public static CountBaselineManifest Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"--count-baseline: file not found: {path}");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException jx)
        {
            throw new InvalidOperationException($"--count-baseline: invalid JSON in {path}: {jx.Message}", jx);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("suites", out var suitesEl) || suitesEl.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"--count-baseline: {path}: root must be an object with a 'suites' object");

            var suites = new Dictionary<string, SuiteCountBaseline>();
            foreach (var suiteProp in suitesEl.EnumerateObject())
            {
                var suiteName = suiteProp.Name;
                if (suiteProp.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"--count-baseline: {path}: suite '{suiteName}' must be an object");
                var tests = ParseExpectedCount(suiteProp.Value, "tests", path, suiteName);
                var appGroups = ParseExpectedCount(suiteProp.Value, "appGroups", path, suiteName);
                if (tests == null && appGroups == null)
                    throw new InvalidOperationException(
                        $"--count-baseline: {path}: suite '{suiteName}' declares neither 'tests' nor 'appGroups'");
                suites[suiteName] = new SuiteCountBaseline(tests, appGroups);
            }
            return new CountBaselineManifest(suites);
        }
    }

    private static ExpectedCount? ParseExpectedCount(JsonElement suiteEl, string metric, string path, string suiteName)
    {
        if (!suiteEl.TryGetProperty(metric, out var metricEl)) return null;
        if (metricEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"--count-baseline: {path}: suite '{suiteName}'.{metric} must be an object with a 'default' field");
        if (!metricEl.TryGetProperty("default", out var defEl) || defEl.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException(
                $"--count-baseline: {path}: suite '{suiteName}'.{metric}.default must be an integer");
        var def = defEl.GetInt32();

        Dictionary<string, int>? byVersion = null;
        if (metricEl.TryGetProperty("byBcVersion", out var byVerEl))
        {
            if (byVerEl.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"--count-baseline: {path}: suite '{suiteName}'.{metric}.byBcVersion must be an object");
            byVersion = new Dictionary<string, int>();
            foreach (var verProp in byVerEl.EnumerateObject())
            {
                if (verProp.Value.ValueKind != JsonValueKind.Number)
                    throw new InvalidOperationException(
                        $"--count-baseline: {path}: suite '{suiteName}'.{metric}.byBcVersion.{verProp.Name} must be an integer");
                byVersion[verProp.Name] = verProp.Value.GetInt32();
            }
        }
        return new ExpectedCount(def, byVersion);
    }
}

/// <summary>
/// Pure comparison: baseline vs actual, split into drops (actual below expected) and
/// growths (actual above expected) — purely for message wording ("shrank" vs "grew").
/// BOTH are mismatches that must fail the run: see the header comment on why growth
/// is not exempted. A suite the manifest does not mention imposes no expectation; a
/// suite the manifest mentions but this run did not touch is silently skipped (a
/// baseline written for CI's two legs must not fire when someone points the runner
/// at an unrelated bundle).
/// </summary>
public static class CountBaselineCheck
{
    public static (IReadOnlyList<CountBaselineFinding> Drops, IReadOnlyList<CountBaselineFinding> Growths) Evaluate(
        CountBaselineManifest manifest,
        IReadOnlyDictionary<string, SuiteCountActual> actualBySuite,
        string? bcVersionKey)
    {
        var drops = new List<CountBaselineFinding>();
        var growths = new List<CountBaselineFinding>();

        foreach (var (suite, baseline) in manifest.Suites)
        {
            if (!actualBySuite.TryGetValue(suite, out var actual)) continue;

            if (baseline.Tests is { } testsExpected)
                Compare(suite, "tests", testsExpected.Resolve(bcVersionKey), actual.Tests, bcVersionKey, drops, growths);
            if (baseline.AppGroups is { } groupsExpected)
                Compare(suite, "appGroups", groupsExpected.Resolve(bcVersionKey), actual.AppGroups, bcVersionKey, drops, growths);
        }

        return (drops, growths);
    }

    private static void Compare(string suite, string metric, int expected, int actual, string? bcVersionKey,
        List<CountBaselineFinding> drops, List<CountBaselineFinding> growths)
    {
        if (actual < expected)
            drops.Add(new CountBaselineFinding(suite, metric, expected, actual, bcVersionKey));
        else if (actual > expected)
            growths.Add(new CountBaselineFinding(suite, metric, expected, actual, bcVersionKey));
    }
}
