// AffectedIsolationWidening — #5035: widens an affectedOnly selection to the tests that share state
// with a selected one under the run's test isolation.
// Why whole codeunits and not a suffix: docs/server-mode.md#affectedonly-and-test-isolation.
namespace AlRunner.Infrastructure;

internal static class AffectedIsolationWidening
{
    /// <summary>
    /// Adds to <paramref name="selected"/> every discovered test that shares state with a selected
    /// test: its whole codeunit under <see cref="TestIsolation.Codeunit"/>, the whole bundle under
    /// <see cref="TestIsolation.Disabled"/>, nothing under <see cref="TestIsolation.Test"/>.
    /// Returns how many tests it added.
    /// </summary>
    /// <param name="discovered">Test keys in the "{Codeunit}.{Method}" shape TestExecutor.DiscoverTests
    /// returns; the codeunit part is a .NET type name, so it holds no '.'.</param>
    internal static int Widen(IReadOnlyCollection<string> discovered, HashSet<string> selected, TestIsolation isolation)
    {
        if (selected.Count == 0 || isolation == TestIsolation.Test) return 0;
        var before = selected.Count;
        if (isolation == TestIsolation.Disabled)
        {
            selected.UnionWith(discovered);
            return selected.Count - before;
        }
        var codeunits = selected.Select(CodeunitOf).ToHashSet(StringComparer.Ordinal);
        foreach (var test in discovered)
            if (codeunits.Contains(CodeunitOf(test))) selected.Add(test);
        return selected.Count - before;
    }

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
