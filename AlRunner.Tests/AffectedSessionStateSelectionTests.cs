// AffectedSessionStateSelectionTests — #5050: the widening rules over recorded session-state keys,
// without a runner process. End-to-end: ServerAffectedSelectionSessionStateTests.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class AffectedSessionStateSelectionTests
{
    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);
    private static readonly string W = AlSessionStateTracker.WriteKey(AlSessionStateTracker.WorkDateKind);
    private static readonly string R = AlSessionStateTracker.ReadKey(AlSessionStateTracker.WorkDateKind);
    private static readonly string SiW = AlSessionStateTracker.WriteKey(AlSessionStateTracker.SingleInstanceKind(7));
    private static readonly string SiR = AlSessionStateTracker.ReadKey(AlSessionStateTracker.SingleInstanceKind(7));

    // Execution order: an early reader, a writer, the changed test, a later reader, a test reading
    // nothing, a later writer.
    private static readonly string[] Order = { "C.EarlyReader", "C.Writer", "C.Changed", "C.LateReader", "C.Plain", "C.LateWriter" };

    private static Dictionary<string, HashSet<string>> Recorded() => new(StringComparer.Ordinal)
    {
        ["C.EarlyReader"] = Keys(R),
        ["C.Writer"] = Keys(W),
        ["C.Changed"] = Keys(),
        ["C.LateReader"] = Keys(R),
        ["C.Plain"] = Keys("ev|Codeunit|1|OnX"),
        ["C.LateWriter"] = Keys(W),
    };

    private static string[] Sorted(HashSet<string> s) => s.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void ASelectedTest_PullsLaterReaders_AndEarlierWriters_NotEarlierReadersLaterWritersOrPlainTests()
    {
        var selected = Keys("C.Changed");
        var added = AffectedSessionStateSelection.Widen(Order, selected, Recorded(), true, false, false);
        Assert.Equal(new[] { "C.Changed", "C.LateReader", "C.Writer" }, Sorted(selected));
        Assert.Equal(2, added);
    }

    [Fact]
    public void NothingSelected_AddsNothing()
    {
        var selected = Keys();
        Assert.Equal(0, AffectedSessionStateSelection.Widen(Order, selected, Recorded(), true, false, false));
        Assert.Empty(selected);
    }

    [Fact]
    public void ATestWithNoRecord_CountsAsReadingAndWriting()
    {
        var recorded = Recorded();
        recorded.Remove("C.Plain");
        var selected = Keys("C.Changed");
        AffectedSessionStateSelection.Widen(Order, selected, recorded, true, false, false);
        Assert.Contains("C.Plain", selected);
    }

    [Fact]
    public void AnEarlierBundleChange_PullsEveryReader_EvenBeforeThisBundlesFirstSelectedTest()
    {
        var selected = Keys();
        AffectedSessionStateSelection.Widen(Order, selected, Recorded(), changed: false, earlierBundleChanged: true, laterBundleFollows: false);
        Assert.Equal(new[] { "C.EarlyReader", "C.LateReader", "C.Writer" }, Sorted(selected));
    }

    [Fact]
    public void ALaterBundle_PullsSessionWideWriters_ButNotSingleInstanceOnes()
    {
        var order = new[] { "C.WorkDateWriter", "C.StoreWriter", "C.Plain" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.WorkDateWriter"] = Keys(W),
            ["C.StoreWriter"] = Keys(SiW, SiR),
            ["C.Plain"] = Keys(),
        };
        var selected = Keys();
        AffectedSessionStateSelection.Widen(order, selected, recorded, changed: false, earlierBundleChanged: false, laterBundleFollows: true);
        Assert.Equal(new[] { "C.WorkDateWriter" }, Sorted(selected));
    }

    /// <summary>With nothing changed, a selected test (an unknown one, say) brings only the earlier
    /// writers of what it read, transitively; no reader is pulled.</summary>
    [Fact]
    public void NoChange_ASelectedReader_BringsOnlyTheEarlierWritersOfWhatItRead_Transitively()
    {
        const string seq = "NumberSequence|s|database";
        var order = new[] { "C.SeqWriter", "C.WorkDateWriterReadingSeq", "C.OtherWriter", "C.Reader", "C.LaterReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.SeqWriter"] = Keys(AlSessionStateTracker.WriteKey(seq)),
            ["C.WorkDateWriterReadingSeq"] = Keys(W, AlSessionStateTracker.ReadKey(seq)),
            ["C.OtherWriter"] = Keys(SiW, SiR),
            ["C.Reader"] = Keys(R),
            ["C.LaterReader"] = Keys(R),
        };
        var selected = Keys("C.Reader");
        AffectedSessionStateSelection.Widen(order, selected, recorded, changed: false, earlierBundleChanged: false, laterBundleFollows: false);
        Assert.Equal(new[] { "C.Reader", "C.SeqWriter", "C.WorkDateWriterReadingSeq" }, Sorted(selected));
    }

    // #5069: a re-recording keeps the session-state keys the previous record had, and only those.
    [Fact]
    public void WithPreviousState_KeepsPreviousStateKeys_AndDropsPreviousOtherKeys()
    {
        var merged = AffectedSessionStateSelection.WithPreviousState(
            Keys(R, "ev|Codeunit|1|OnNew"), Keys(SiR, SiW, "ev|Codeunit|1|OnOld", "tbl|Table|5"));
        Assert.Equal(new[] { "ev|Codeunit|1|OnNew", R, SiR, SiW }.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Sorted(merged));
        Assert.Equal(new[] { R }, Sorted(AffectedSessionStateSelection.WithPreviousState(Keys(R), null)));
    }
}
