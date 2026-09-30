// AffectedSessionStateSelection — #5050: widens an affectedOnly selection to the tests linked to a
// selected one through session state (WorkDate, number sequences, SingleInstance codeunits), which
// survives every test isolation. Rules and why they are this wide:
// docs/server-mode.md#affectedonly-and-session-state.
namespace AlRunner.Infrastructure;

internal static class AffectedSessionStateSelection
{
    /// <summary>
    /// Adds to <paramref name="selected"/>, when anything in this request is selected:
    /// every test after the first selected one that read session state an earlier test left
    /// (or has no record), and every test that wrote session state and runs before the last
    /// selected test. Returns how many tests it added.
    /// </summary>
    /// <param name="discovered">Test keys in execution order (TestExecutor.DiscoverTests).</param>
    /// <param name="recorded">Each test's recorded keys from the baseline; a missing entry is
    /// treated as reading and writing every kind.</param>
    /// <param name="earlierBundleSelected">A bundle that ran earlier in this request selected a
    /// test; state it wrote reaches this bundle's first test.</param>
    /// <param name="laterBundleFollows">Another bundle of this request runs after this one and may
    /// read what this one writes, which this bundle's selection cannot see.</param>
    internal static int Widen(IReadOnlyList<string> discovered, HashSet<string> selected,
        IReadOnlyDictionary<string, HashSet<string>>? recorded, bool earlierBundleSelected, bool laterBundleFollows)
    {
        var before = selected.Count;
        var anySelected = selected.Count > 0 || earlierBundleSelected;

        bool Reads(string t) => Record(t) is not { } r || r.Any(k => k.StartsWith(AlSessionStateTracker.ReadPrefix, StringComparison.Ordinal));
        bool Writes(string t) => Record(t) is not { } r || r.Any(k => k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal));
        bool WritesSessionWide(string t) => Record(t) is not { } r || r.Any(k =>
            k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal)
            && !k.StartsWith(AlSessionStateTracker.WriteKey("SingleInstance|"), StringComparison.Ordinal));
        HashSet<string>? Record(string t) => recorded != null && recorded.TryGetValue(t, out var r) ? r : null;

        if (anySelected)
        {
            // A changed test can start reading or writing any kind, which its record cannot show,
            // so every reader after the first selected test and every earlier writer is in.
            var first = earlierBundleSelected ? -1 : IndexOfFirst(discovered, selected);
            for (var i = first + 1; i < discovered.Count; i++)
                if (Reads(discovered[i])) selected.Add(discovered[i]);
            var last = LastIndex(discovered, selected);
            for (var i = 0; i < last; i++)
                if (Writes(discovered[i])) selected.Add(discovered[i]);
        }
        // SingleInstance codeunits are reset per bundle; WorkDate and number sequences are not.
        if (laterBundleFollows)
            foreach (var t in discovered)
                if (WritesSessionWide(t)) selected.Add(t);
        return selected.Count - before;
    }

    private static int IndexOfFirst(IReadOnlyList<string> discovered, HashSet<string> selected)
    {
        for (var i = 0; i < discovered.Count; i++)
            if (selected.Contains(discovered[i])) return i;
        return discovered.Count;
    }

    private static int LastIndex(IReadOnlyList<string> discovered, HashSet<string> selected)
    {
        for (var i = discovered.Count - 1; i >= 0; i--)
            if (selected.Contains(discovered[i])) return i;
        return -1;
    }
}
