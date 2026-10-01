namespace AlRunner.Infrastructure;

/// <summary>
/// Per-test record of the session state a test wrote and the session state it read before writing
/// it itself (#5050), for <c>affectedOnly</c> selection. Session state is what survives a test
/// boundary under every test isolation because no database rollback reaches it: WorkDate, number
/// sequences, SingleInstance codeunit instances, the last error and static .NET state (#5057). How
/// selection uses it:
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
        // Overwrite kinds cleared between the previous test and this one (see NoteTestStart).
        public readonly HashSet<string> ClearedAtStart = new(StringComparer.Ordinal);
        public bool Threw;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Bucket> _perTest = new(StringComparer.Ordinal);

    // Per overwrite kind, the last write made outside any test since the last test started: true
    // when it was a clear, false for any other write.
    private static readonly Dictionary<string, bool> _boundaryWrite = new(StringComparer.Ordinal);

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
            if (b != null && !b.Written.Contains(kind) && !b.ClearedAtStart.Contains(kind)) b.ReadInherited.Add(kind);
        }
    }

    /// <summary>The open test wrote <paramref name="kind"/>.</summary>
    public static void NoteWrite(string kind) => NoteWrite(kind, clear: false);

    private static void NoteWrite(string kind, bool clear)
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        lock (_lock)
        {
            var b = CurrentBucket();
            if (b != null) b.Written.Add(kind);
            else if (OverwriteKinds.Contains(kind)) _boundaryWrite[kind] = clear;
        }
    }

    /// <summary>
    /// Called as a test's window opens. An overwrite kind whose last write since the previous test
    /// was a clear made outside any test (130453 "ALTestRunner Reset Environment" clearing the last
    /// error in OnBeforeTestMethodRun) starts empty whatever earlier tests left, so this test
    /// inherits nothing of it (#5057).
    /// </summary>
    public static void NoteTestStart()
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        lock (_lock)
        {
            var b = CurrentBucket();
            if (b != null)
                foreach (var (kind, clear) in _boundaryWrite)
                    if (clear) b.ClearedAtStart.Add(kind);
            _boundaryWrite.Clear();
        }
    }

    /// <summary>A read followed by a write, as <c>NumberSequence.Next</c> does.</summary>
    public static void NoteReadWrite(string kind)
    {
        NoteRead(kind);
        NoteWrite(kind);
    }

    /// <summary>Prepended to <c>NavSession.get_WorkDate</c>: AL's <c>WorkDate()</c> and the
    /// <c>'w'</c> token of Evaluate and date filters all read it.</summary>
    public static void NoteWorkDateRead()
    {
        Interlocked.Increment(ref _workDateAccessCount);
        NoteRead(WorkDateKind);
    }

    /// <summary>Prepended to <c>NavSession.set_WorkDate</c>.</summary>
    public static void NoteWorkDateWrite()
    {
        Interlocked.Increment(ref _workDateAccessCount);
        NoteWrite(WorkDateKind);
    }

    // #5060: every WorkDate read or write, per-test tracking on or off, so TestExecutor can tell
    // whether an install seed depended on session state no install-baseline snapshot carries.
    private static long _workDateAccessCount;
    internal static long WorkDateAccessCount => Interlocked.Read(ref _workDateAccessCount);

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

    /// <summary>The session's last error (#5057): GetLastErrorText/Code/Object/CallStack read it;
    /// any trapped error and ClearLastError write it.</summary>
    internal const string LastErrorKind = "LastError";

    /// <summary>Prepended to every NavSession member that reads its last-error fields.</summary>
    public static void NoteLastErrorRead() => NoteRead(LastErrorKind);

    /// <summary>Prepended to every NavSession member that writes its last-error fields.</summary>
    public static void NoteLastErrorWrite() => NoteWrite(LastErrorKind);

    /// <summary>Prepended to <c>NavSession.ClearLastError</c>: a write that leaves nothing behind.</summary>
    public static void NoteLastErrorClear() => NoteWrite(LastErrorKind, clear: true);

    /// <summary>
    /// Kinds whose every write replaces the whole value without reading the old one, so a reader
    /// sees only the nearest earlier writer (AffectedSessionStateSelection). Every NavSession member
    /// storing the last-error fields assigns them outright (bc284), and every NavBaseException sets
    /// them as it is constructed, trapped or not.
    /// </summary>
    internal static readonly string[] OverwriteKinds = { LastErrorKind };

    /// <summary>
    /// Called as the open test ends. A test that threw leaves the last error to its own failure,
    /// whatever it trapped before, so its overwrite-kind writes are recorded under
    /// <see cref="FailedWriteKey"/>, which a re-recording does not carry forward: a test once failing
    /// and now passing must not stay a writer for good.
    /// </summary>
    public static void NoteTestEnd(bool threw)
    {
        if (!AlCoverageTracker.PerTestEnabled || !threw) return;
        lock (_lock)
        {
            var b = CurrentBucket();
            if (b != null) b.Threw = true;
        }
    }

    /// <summary>Static .NET state reached through DotNet interop (#5057). It cannot be told apart
    /// per field, so any DotNet invocation is both a read and a write of one kind.</summary>
    internal const string DotNetKind = "DotNet";

    /// <summary>Prepended to NavDotNet's invoke, static-field and constructor entry points.</summary>
    public static void NoteDotNetUse()
    {
        if (!AlCoverageTracker.PerTestEnabled || AlCoverageTracker.CurrentTestKey == null) return;
        NoteReadWrite(DotNetKind);
    }

    /// <summary>A key in a test's recorded set: it wrote <paramref name="kind"/>.</summary>
    internal static string WriteKey(string kind) => "st|w|" + kind;

    /// <summary>A key in a test's recorded set: it read <paramref name="kind"/> as an earlier test left it.</summary>
    internal static string ReadKey(string kind) => "st|r|" + kind;

    /// <summary>A key in a test's recorded set: it wrote overwrite kind <paramref name="kind"/> in a
    /// run where it threw. A write all the same, but not kept by AffectedSessionStateSelection.WithPreviousState.</summary>
    internal static string FailedWriteKey(string kind) => "st|f|" + kind;

    /// <summary>A key in a test's recorded set: overwrite kind <paramref name="kind"/> was cleared
    /// outside any test just before it started, so nothing an earlier test left reaches it.</summary>
    internal static string ClearedAtStartKey(string kind) => "st|c|" + kind;

    /// <summary>A key in a test's recorded set: an earlier record of this test wrote overwrite kind
    /// <paramref name="kind"/>, and this one may or may not have (AffectedSessionStateSelection.WithPreviousState).</summary>
    internal static string MaybeWriteKey(string kind) => "st|m|" + kind;

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
                foreach (var k in b.Written)
                    keys.Add(b.Threw && OverwriteKinds.Contains(k) ? FailedWriteKey(k) : WriteKey(k));
                foreach (var k in b.ReadInherited) keys.Add(ReadKey(k));
                foreach (var k in b.ClearedAtStart) keys.Add(ClearedAtStartKey(k));
                result[testKey] = keys;
            }
        }
        return result;
    }
}
