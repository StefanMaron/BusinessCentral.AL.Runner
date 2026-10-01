// AffectedSessionStateSelection — #5050: widens an affectedOnly selection to the tests linked to a
// selected one through session state (WorkDate, number sequences, SingleInstance codeunits, the
// last error, static .NET state), which survives every test isolation. Rules and why they are
// this wide: docs/server-mode.md#affectedonly-and-session-state.
namespace AlRunner.Infrastructure;

internal static class AffectedSessionStateSelection
{
    private static readonly string[] OverwriteKinds = AlSessionStateTracker.OverwriteKinds;

    private static bool IsOverwriteWrite(string key)
        => OverwriteKinds.Any(k => string.Equals(key, AlSessionStateTracker.WriteKey(k), StringComparison.Ordinal));

    /// <summary>
    /// Adds to <paramref name="selected"/> the tests linked to it through session state. With a
    /// change in this bundle or an earlier one: every test after the first selected one that read
    /// session state an earlier test left, every test before the last selected one that wrote
    /// session state other than an overwrite kind, and the nearest earlier writer of each overwrite
    /// kind before every selected test. Without one: the writers of what each selected test read,
    /// before it (for an overwrite kind, the nearest). Returns how many tests it added.
    /// </summary>
    /// <param name="discovered">Test keys in execution order (TestExecutor.DiscoverTests).</param>
    /// <param name="recorded">Each test's recorded keys from the baseline; a missing entry is
    /// treated as reading and writing every kind.</param>
    /// <param name="changed">This request changed code or bindings this bundle's tests can reach.</param>
    /// <param name="earlierBundleChanged">A bundle that ran earlier in this request had a change;
    /// the state it wrote reaches this bundle's first test.</param>
    /// <param name="laterBundleFollows">Another bundle of this request runs after this one and may
    /// read what this one writes, which this bundle's selection cannot see.</param>
    /// <param name="changedTests">The tests selected for a change, before any widening; null means
    /// <paramref name="selected"/> as it is on entry.</param>
    internal static int Widen(IReadOnlyList<string> discovered, HashSet<string> selected,
        IReadOnlyDictionary<string, HashSet<string>>? recorded, bool changed, bool earlierBundleChanged,
        bool laterBundleFollows, IReadOnlySet<string>? changedTests = null)
    {
        var before = selected.Count;
        HashSet<string>? Record(string t) => recorded != null && recorded.TryGetValue(t, out var r) ? r : null;
        bool Any(string t, string prefix) => Record(t) is not { } r || r.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
        // For a nearest-writer walk, a test selected for a change counts as unrecorded: it may have
        // stopped writing.
        changedTests = !changed && !earlierBundleChanged ? new HashSet<string>(StringComparer.Ordinal)
            : changedTests ?? new HashSet<string>(selected, StringComparer.Ordinal);
        HashSet<string>? WalkRecord(string t) => changedTests.Contains(t) ? null : Record(t);
        bool WritesAccumulating(string t) => Record(t) is not { } r
            || r.Any(k => k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal) && !IsOverwriteWrite(k));

        if (earlierBundleChanged || (changed && selected.Count > 0))
        {
            // A changed test can start reading or writing any kind, which its record cannot show,
            // so every reader after the first selected test and every earlier writer is in.
            // From the first CHANGED test: a test brought in as a writer changes no state a full run would not.
            var first = earlierBundleChanged ? -1 : IndexOfFirst(discovered, changedTests.Count > 0 ? changedTests : selected);
            for (var i = first + 1; i < discovered.Count; i++)
                if (Any(discovered[i], AlSessionStateTracker.ReadPrefix)) selected.Add(discovered[i]);
            var last = LastIndex(discovered, selected);
            for (var i = 0; i < last; i++)
                if (WritesAccumulating(discovered[i])) selected.Add(discovered[i]);
            // A changed or unrecorded test may read any overwrite kind; any other, what it read.
            AddNearestOverwriters(discovered, selected, WalkRecord, i => WalkRecord(discovered[i]) is { } r
                ? OverwriteKinds.Where(k => r.Contains(AlSessionStateTracker.ReadKey(k)))
                : OverwriteKinds);
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
                foreach (var j in NearestOverwriters(discovered, i, Record,
                             OverwriteKinds.Where(k => reads == null || reads.Contains(AlSessionStateTracker.ReadKey(k)))))
                    if (selected.Add(discovered[j])) pending.Enqueue(j);
            }
        }
        // SingleInstance codeunits are reset per bundle; every other kind is not. Of an overwrite
        // kind, only the bundle's last writer reaches the next bundle.
        if (laterBundleFollows)
        {
            foreach (var t in discovered)
                if (Record(t) is not { } r || r.Any(k => k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal)
                        && !IsOverwriteWrite(k)
                        && !k.StartsWith(AlSessionStateTracker.WriteKey("SingleInstance|"), StringComparison.Ordinal)))
                    selected.Add(t);
            foreach (var j in NearestOverwriters(discovered, discovered.Count, WalkRecord, OverwriteKinds))
                selected.Add(discovered[j]);
        }
        return selected.Count - before;
    }

    // Before every selected test (and every one this adds), the nearest earlier writer of each kind
    // `kindsFor` names for it.
    private static void AddNearestOverwriters(IReadOnlyList<string> discovered, HashSet<string> selected,
        Func<string, HashSet<string>?> record, Func<int, IEnumerable<string>> kindsFor)
    {
        var pending = new Queue<int>();
        for (var i = 0; i < discovered.Count; i++)
            if (selected.Contains(discovered[i])) pending.Enqueue(i);
        while (pending.Count > 0)
        {
            var i = pending.Dequeue();
            foreach (var j in NearestOverwriters(discovered, i, record, kindsFor(i)))
                if (selected.Add(discovered[j])) pending.Enqueue(j);
        }
    }

    // For each kind, walking back from `before`: every test with no record (it may or may not write
    // it) up to and including the nearest one whose record writes it.
    private static IEnumerable<int> NearestOverwriters(IReadOnlyList<string> discovered, int before,
        Func<string, HashSet<string>?> record, IEnumerable<string> kinds)
    {
        foreach (var kind in kinds)
        {
            var write = AlSessionStateTracker.WriteKey(kind);
            for (var j = before - 1; j >= 0; j--)
            {
                var r = record(discovered[j]);
                if (r != null && !r.Contains(write) && !r.Contains(AlSessionStateTracker.FailedWriteKey(kind))) continue;
                yield return j;
                if (r != null) break;
            }
        }
    }

    /// <summary>
    /// Isolation widening (#5035) and session-state widening, repeated until neither adds a test: a
    /// test brought in as a writer or reader shares its codeunit's state like any other selected
    /// test, so its codeunit (or bundle) comes too, and those tests can link further ones (#5057).
    /// Returns how many tests each added.
    /// </summary>
    internal static (int Isolation, int State) WidenWithIsolation(IReadOnlyList<string> discovered,
        HashSet<string> selected, IReadOnlyDictionary<string, HashSet<string>>? recorded, TestIsolation isolation,
        bool changed, bool earlierBundleChanged, bool laterBundleFollows)
    {
        var changedTests = new HashSet<string>(selected, StringComparer.Ordinal);
        int byIsolation = 0, byState = 0;
        while (true)
        {
            byIsolation += AffectedIsolationWidening.Widen(discovered, selected, isolation);
            var added = Widen(discovered, selected, recorded, changed, earlierBundleChanged, laterBundleFollows, changedTests);
            byState += added;
            if (added == 0) return (byIsolation, byState);
        }
    }

    /// <summary>
    /// A re-recorded test's keys plus the session-state keys of its previous record (#5069).
    /// A use can happen only in whichever test first reaches a per-session cache (a table's
    /// trigger mask, a caption lookup), so a narrowed run can record fewer uses than a full run
    /// did; keeping the earlier ones means a re-recording can only widen what it links.
    /// </summary>
    internal static HashSet<string> WithPreviousState(HashSet<string> recorded, HashSet<string>? previous)
    {
        if (previous == null) return recorded;
        foreach (var k in previous)
            if (k.StartsWith(AlSessionStateTracker.ReadPrefix, StringComparison.Ordinal)
                || k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal))
                recorded.Add(k);
        return recorded;
    }

    // Whether a test with record `writer` wrote a kind a test with record `reader` read inherited.
    // A missing record on either side answers yes.
    // Overwrite kinds are left to NearestOverwriters.
    private static bool WritesAnyOf(HashSet<string>? writer, HashSet<string>? reader)
    {
        if (writer == null) return true;
        if (reader == null) return writer.Any(k => k.StartsWith(AlSessionStateTracker.WritePrefix, StringComparison.Ordinal) && !IsOverwriteWrite(k));
        foreach (var k in reader)
        {
            if (!k.StartsWith(AlSessionStateTracker.ReadPrefix, StringComparison.Ordinal)) continue;
            var w = AlSessionStateTracker.WritePrefix + k.Substring(AlSessionStateTracker.ReadPrefix.Length);
            if (!IsOverwriteWrite(w) && writer.Contains(w)) return true;
        }
        return false;
    }

    private static int IndexOfFirst(IReadOnlyList<string> discovered, IReadOnlySet<string> selected)
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
