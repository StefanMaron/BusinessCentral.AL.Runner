// AffectedSessionStateSelection — #5050: widens an affectedOnly selection to the tests linked to a
// selected one through session state (WorkDate, number sequences, SingleInstance codeunits), which
// survives every test isolation. Rules and why they are this wide:
// docs/server-mode.md#affectedonly-and-session-state.
namespace AlRunner.Infrastructure;

internal static class AffectedSessionStateSelection
{
    /// <summary>
    /// Adds to <paramref name="selected"/> the tests linked to it through session state. With a
    /// change in this bundle or an earlier one: every test after the first selected one that read
    /// session state an earlier test left, and every test before the last selected one that wrote
    /// session state. Without one: the writers of what each selected test read, before it.
    /// Returns how many tests it added.
    /// </summary>
    /// <param name="discovered">Test keys in execution order (TestExecutor.DiscoverTests).</param>
    /// <param name="recorded">Each test's recorded keys from the baseline; a missing entry is
    /// treated as reading and writing every kind.</param>
    /// <param name="changed">This request changed code or bindings this bundle's tests can reach.</param>
    /// <param name="earlierBundleChanged">A bundle that ran earlier in this request had a change;
    /// the state it wrote reaches this bundle's first test.</param>
    /// <param name="laterBundleFollows">Another bundle of this request runs after this one and may
    /// read what this one writes, which this bundle's selection cannot see.</param>
    internal static int Widen(IReadOnlyList<string> discovered, HashSet<string> selected,
        IReadOnlyDictionary<string, HashSet<string>>? recorded, bool changed, bool earlierBundleChanged,
        bool laterBundleFollows)
    {
        var before = selected.Count;
        HashSet<string>? Record(string t) => recorded != null && recorded.TryGetValue(t, out var r) ? r : null;
        bool Any(string t, string prefix) => Record(t) is not { } r || r.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));

        if (earlierBundleChanged || (changed && selected.Count > 0))
        {
            // A changed test can start reading or writing any kind, which its record cannot show,
            // so every reader after the first selected test and every earlier writer is in.
            var first = earlierBundleChanged ? -1 : IndexOfFirst(discovered, selected);
            for (var i = first + 1; i < discovered.Count; i++)
                if (Any(discovered[i], AlSessionStateTracker.ReadPrefix)) selected.Add(discovered[i]);
            var last = LastIndex(discovered, selected);
            for (var i = 0; i < last; i++)
                if (Any(discovered[i], AlSessionStateTracker.WritePrefix)) selected.Add(discovered[i]);
        }
        else if (selected.Count > 0)
        {
            // Nothing changed, so every test reads and writes what its record says: a selected test
            // needs only the earlier writers of what it read, and theirs in turn.
            var pending = new Queue<int>();
            for (var i = 0; i < discovered.Count; i++)
                if (selected.Contains(discovered[i])) pending.Enqueue(i);
            while (pending.Count > 0)
            {
                var i = pending.Dequeue();
                var reads = Record(discovered[i]);
                for (var j = 0; j < i; j++)
                {
                    var w = discovered[j];
                    if (selected.Contains(w) || !WritesAnyOf(Record(w), reads)) continue;
                    selected.Add(w);
                    pending.Enqueue(j);
                }
            }
        }
        // SingleInstance codeunits are reset per bundle; WorkDate and number sequences are not.
        if (laterBundleFollows)
            foreach (var t in discovered)
                if (Record(t) is not { } r || r.Any(k => k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal)
                        && !k.StartsWith(AlSessionStateTracker.WriteKey("SingleInstance|"), StringComparison.Ordinal)))
                    selected.Add(t);
        return selected.Count - before;
    }

    // Whether a test with record `writer` wrote a kind a test with record `reader` read inherited.
    // A missing record on either side answers yes.
    private static bool WritesAnyOf(HashSet<string>? writer, HashSet<string>? reader)
    {
        if (writer == null) return true;
        if (reader == null) return writer.Any(k => k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal));
        foreach (var k in reader)
            if (k.StartsWith(AlSessionStateTracker.ReadPrefix, StringComparison.Ordinal)
                && writer.Contains(AlSessionStateTracker.WritePrefix + k.Substring(AlSessionStateTracker.ReadPrefix.Length)))
                return true;
        return false;
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
