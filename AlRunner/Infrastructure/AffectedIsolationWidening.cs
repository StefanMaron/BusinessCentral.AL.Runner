// AffectedIsolationWidening — #5035: widens an affectedOnly selection to the tests that share state
// with a selected one under the run's test isolation.
// Why whole codeunits and not a suffix: docs/server-mode.md#affectedonly-and-test-isolation.
namespace AlRunner.Infrastructure;

internal static class AffectedIsolationWidening
{
    /// <summary>
    /// Adds to <paramref name="selected"/> every discovered test that shares state with a selected
    /// test: its whole codeunit under <see cref="TestIsolation.Codeunit"/>, the whole bundle under
    /// <see cref="TestIsolation.Disabled"/>. Under <see cref="TestIsolation.Test"/> the database
    /// resets per test but the codeunit instance does not (#4826), so its whole codeunit only when
    /// <paramref name="sharesStateAcrossTests"/> says the codeunit can carry state from one test to
    /// the next (AL globals or an OnRun); null means assume it can.
    /// Returns how many tests it added.
    /// </summary>
    /// <param name="discovered">Test keys in the "{Codeunit}.{Method}" shape TestExecutor.DiscoverTests
    /// returns; the codeunit part is a .NET type name, so it holds no '.'.</param>
    internal static int Widen(IReadOnlyCollection<string> discovered, HashSet<string> selected, TestIsolation isolation,
        Func<string, bool>? sharesStateAcrossTests = null)
    {
        if (selected.Count == 0) return 0;
        var before = selected.Count;
        if (isolation == TestIsolation.Disabled)
        {
            selected.UnionWith(discovered);
            return selected.Count - before;
        }
        var codeunits = selected.Select(CodeunitOf)
            .Where(c => SharesState(isolation, c, sharesStateAcrossTests))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var test in discovered)
            if (codeunits.Contains(CodeunitOf(test))) selected.Add(test);
        return selected.Count - before;
    }

    /// <summary>Whether a selected test's codeunit carries state to its other tests under
    /// <paramref name="isolation"/>; only <see cref="TestIsolation.Test"/> asks the predicate.</summary>
    internal static bool SharesState(TestIsolation isolation, string codeunit, Func<string, bool>? sharesStateAcrossTests)
        => isolation != TestIsolation.Test || sharesStateAcrossTests?.Invoke(codeunit) != false;

    /// <summary>
    /// The selection environment key with the isolation appended: coverage recorded under one
    /// isolation attributes shared setup differently from another, so it cannot select for it.
    /// </summary>
    internal static string EnvironmentKey(string environmentKey, TestIsolation isolation)
        => $"{environmentKey}|isolation={isolation}";

    internal static string CodeunitOf(string testKey)
    {
        var dot = testKey.IndexOf('.');
        return dot < 0 ? testKey : testKey[..dot];
    }
}
