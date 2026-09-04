// TestExclusionFilter — "run everything EXCEPT these" (--exclude-test).
//
// Until this existed, selection was inclusive only (--test PATTERN). That left no way to
// express the one thing a run needs after a watchdog abort: continue, but skip the test that
// hung. TestExecutor abandons the rest of the codeunit and every later codeunit when a test's
// watchdog fires — correctly, because the hung thread is never killed and keeps mutating shared
// BC state, so continuing in-process would produce results that lie. The only safe way to reach
// the abandoned tests is a fresh process that skips the offender, and that needs this.
//
// Measured on Microsoft's BaseApp buckets with --test-data: Tests-ERM ran 2 of 9,500 tests
// because one abort in ERM Close Income Statement took the whole bucket, and eleven aborts
// across the run cost more than every other failure cause combined.
//
// Matching deliberately mirrors --test's case-insensitivity, but NOT its substring behaviour.
// --test is a human convenience where matching too much only wastes time; excluding too much
// silently drops tests and still reports a confident green. So a pattern matches either the
// whole "Codeunit.Method" or the whole codeunit name — never a prefix of a longer one, which is
// why "Codeunit1342" must not swallow Codeunit134228.

namespace AlRunner.Infrastructure;

public sealed class TestExclusionFilter
{
    private readonly HashSet<string> _patterns;

    public TestExclusionFilter(IEnumerable<string> patterns)
        => _patterns = new HashSet<string>(
            patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>
    /// True when this test must not run. Matches the full <c>Codeunit.Method</c> or the whole
    /// codeunit name; never a partial segment.
    /// </summary>
    public bool IsExcluded(string codeunit, string method)
    {
        if (_patterns.Count == 0) return false;
        var cu = codeunit.ToLowerInvariant();
        return _patterns.Contains(cu) || _patterns.Contains($"{cu}.{method.ToLowerInvariant()}");
    }
}
