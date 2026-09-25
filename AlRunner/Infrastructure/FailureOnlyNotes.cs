namespace AlRunner.Infrastructure;

/// <summary>
/// A runner limitation the user cannot act on, which can only matter as an explanation of a
/// failed test (#4561). Under --verbose the full text prints where it arises; otherwise one
/// line per note prints after the summary, and only when a test failed or errored. Never
/// dropped: a failing run always names it.
/// </summary>
internal static class FailureOnlyNotes
{
    private static readonly object Gate = new();
    private static readonly List<string> Pending = new();

    /// <param name="fullText">Printed immediately under --verbose, exactly as before.</param>
    /// <param name="footerLine">The one-line form held for the end of the run. Keep a
    /// `[warn]` prefix: Log's filter drops any other bracketed tag at default verbosity.</param>
    public static void Add(string fullText, string footerLine)
    {
        if (Log.Verbose)
            Console.Error.WriteLine(fullText);
        lock (Gate)
        {
            if (!Pending.Contains(footerLine))
                Pending.Add(footerLine);
        }
    }

    /// <summary>Writes the held lines when <paramref name="anyTestFailed"/>, and clears them
    /// either way. Returns how many lines it wrote.</summary>
    public static int Flush(TextWriter w, bool anyTestFailed)
    {
        List<string> notes;
        lock (Gate)
        {
            notes = new List<string>(Pending);
            Pending.Clear();
        }
        if (!anyTestFailed) return 0;
        foreach (var n in notes)
            w.WriteLine(n);
        return notes.Count;
    }

    internal static IReadOnlyList<string> PendingForTests()
    {
        lock (Gate) return Pending.ToList();
    }

    internal static void ResetForTests()
    {
        lock (Gate) Pending.Clear();
    }
}
