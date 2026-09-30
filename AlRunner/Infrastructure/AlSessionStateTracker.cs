namespace AlRunner.Infrastructure;

/// <summary>
/// Per-test record of the session state a test wrote and the session state it read before writing
/// it itself (#5050), for <c>affectedOnly</c> selection. Session state is what survives a test
/// boundary under every test isolation because no database rollback reaches it: WorkDate, number
/// sequences, SingleInstance codeunit instances. How selection uses it:
/// docs/server-mode.md#affectedonly-and-session-state.
///
/// A kind is read "inherited" when the test reads it before its own first write, so its value
/// came from an earlier test. That is a property of the test's own code, not of which tests ran
/// before it, so a record taken in a narrowed run means the same as one taken in a full run.
/// </summary>
public static class AlSessionStateTracker
{
    internal const string WorkDateKind = "WorkDate";

    private sealed class Bucket
    {
        public readonly HashSet<string> Written = new(StringComparer.Ordinal);
        public readonly HashSet<string> ReadInherited = new(StringComparer.Ordinal);
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Bucket> _perTest = new(StringComparer.Ordinal);

    public static void ResetPerTest()
    {
        lock (_lock) _perTest.Clear();
    }

    private static Bucket? CurrentBucket()
    {
        var testKey = AlCoverageTracker.CurrentTestKey;
        if (testKey == null) return null;
        if (!_perTest.TryGetValue(testKey, out var b)) _perTest[testKey] = b = new Bucket();
        return b;
    }

    /// <summary>The open test read <paramref name="kind"/>.</summary>
    public static void NoteRead(string kind)
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        lock (_lock)
        {
            var b = CurrentBucket();
            if (b != null && !b.Written.Contains(kind)) b.ReadInherited.Add(kind);
        }
    }

    /// <summary>The open test wrote <paramref name="kind"/>.</summary>
    public static void NoteWrite(string kind)
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        lock (_lock) CurrentBucket()?.Written.Add(kind);
    }

    /// <summary>A read followed by a write, as <c>NumberSequence.Next</c> does.</summary>
    public static void NoteReadWrite(string kind)
    {
        NoteRead(kind);
        NoteWrite(kind);
    }

    /// <summary>Prepended to <c>ALSystemDate.ALWorkDate(NavSession)</c>, the getter every AL
    /// <c>WorkDate()</c> reaches.</summary>
    public static void NoteWorkDateRead() => NoteRead(WorkDateKind);

    /// <summary>Prepended to <c>ALSystemDate.ALWorkDate(NavSession, NavDate)</c>, the setter.</summary>
    public static void NoteWorkDateWrite() => NoteWrite(WorkDateKind);

    internal static string NumberSequenceKind(string name, bool companySpecific)
        => $"NumberSequence|{name.ToLowerInvariant()}|{(companySpecific ? "company" : "database")}";

    /// <summary>Codeunit <paramref name="codeunitId"/> is SingleInstance and the open test reached
    /// its instance. Its globals cannot be told apart by read or write, so a use is both.</summary>
    internal static string SingleInstanceKind(int codeunitId) => $"SingleInstance|{codeunitId}";

    public static void NoteSingleInstanceUse(int codeunitId)
    {
        if (!AlCoverageTracker.PerTestEnabled || AlCoverageTracker.CurrentTestKey == null) return;
        NoteReadWrite(SingleInstanceKind(codeunitId));
    }

    /// <summary>A key in a test's recorded set: it wrote <paramref name="kind"/>.</summary>
    internal static string WriteKey(string kind) => "st|w|" + kind;

    /// <summary>A key in a test's recorded set: it read <paramref name="kind"/> as an earlier test left it.</summary>
    internal static string ReadKey(string kind) => "st|r|" + kind;

    internal const string WritePrefix = "st|w|";
    internal const string ReadPrefix = "st|r|";

    /// <summary>Each test's keys since the last <see cref="ResetPerTest"/>.</summary>
    public static Dictionary<string, HashSet<string>> CollectPerTest()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var (testKey, b) in _perTest)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in b.Written) keys.Add(WriteKey(k));
                foreach (var k in b.ReadInherited) keys.Add(ReadKey(k));
                result[testKey] = keys;
            }
        }
        return result;
    }
}
