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

    // #5057: the last error is replaced whole by every write, so a reader sees only the nearest
    // earlier writer. Raising any error writes it, so every-earlier-writer would select most tests.
    private static readonly string LeW = AlSessionStateTracker.WriteKey(AlSessionStateTracker.LastErrorKind);
    private static readonly string LeR = AlSessionStateTracker.ReadKey(AlSessionStateTracker.LastErrorKind);

    [Fact]
    public void Changed_LastError_BringsOnlyTheNearestEarlierWriter()
    {
        var order = new[] { "C.LeW1", "C.LeW2", "C.Changed", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.LeW2"] = Keys(LeW),
            ["C.Changed"] = Keys(),
            ["C.LeReader"] = Keys(LeR),
        };
        var selected = Keys("C.Changed");
        AffectedSessionStateSelection.Widen(order, selected, recorded, true, false, false);
        Assert.Equal(new[] { "C.Changed", "C.LeReader", "C.LeW2" }, Sorted(selected));
    }

    /// <summary>A changed test recorded as writing the last error may have stopped writing it, so the
    /// walk does not stop there.</summary>
    [Fact]
    public void Changed_TheWalkPassesTheChangedTest()
    {
        var order = new[] { "C.LeW1", "C.Changed", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.Changed"] = Keys(LeW),
            ["C.LeReader"] = Keys(LeR),
        };
        var selected = Keys("C.Changed");
        AffectedSessionStateSelection.Widen(order, selected, recorded, true, false, false);
        Assert.Equal(new[] { "C.Changed", "C.LeReader", "C.LeW1" }, Sorted(selected));
    }

    [Fact]
    public void NoChange_ALastErrorReader_BringsOnlyTheNearestEarlierWriter()
    {
        var order = new[] { "C.LeW1", "C.LeW2", "C.Plain", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.LeW2"] = Keys(LeW),
            ["C.Plain"] = Keys(),
            ["C.LeReader"] = Keys(LeR),
        };
        var selected = Keys("C.LeReader");
        AffectedSessionStateSelection.Widen(order, selected, recorded, false, false, false);
        Assert.Equal(new[] { "C.LeReader", "C.LeW2" }, Sorted(selected));
    }

    [Fact]
    public void ALaterBundle_PullsOnlyTheBundlesLastLastErrorWriter()
    {
        var order = new[] { "C.LeW1", "C.LeW2", "C.Plain" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.LeW2"] = Keys(LeW),
            ["C.Plain"] = Keys(),
        };
        var selected = Keys();
        AffectedSessionStateSelection.Widen(order, selected, recorded, false, false, laterBundleFollows: true);
        Assert.Equal(new[] { "C.LeW2" }, Sorted(selected));
    }

    /// <summary>A test that threw records its last-error write under the failed-write key: a writer for
    /// the nearest-writer walk, and not carried into its next record.</summary>
    [Fact]
    public void AFailedRunsLastErrorWrite_StopsTheWalk_AndIsNotCarriedForward()
    {
        var leF = AlSessionStateTracker.FailedWriteKey(AlSessionStateTracker.LastErrorKind);
        var order = new[] { "C.LeW1", "C.Failing", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.Failing"] = Keys(leF),
            ["C.LeReader"] = Keys(LeR),
        };
        var selected = Keys("C.LeReader");
        AffectedSessionStateSelection.Widen(order, selected, recorded, false, false, false);
        Assert.Equal(new[] { "C.Failing", "C.LeReader" }, Sorted(selected));

        // A trapped write from an earlier record is carried only as a maybe-write; the failed run's
        // is not carried at all. A stale definite write would end a later walk early (review of #5080).
        var merged = AffectedSessionStateSelection.WithPreviousState(Keys(R), Keys(leF, LeW));
        Assert.Equal(new[] { R, LeM }.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Sorted(merged));
        // A write this record made itself stays definite.
        Assert.Equal(new[] { LeW }, Sorted(AffectedSessionStateSelection.WithPreviousState(Keys(LeW), Keys(LeW))));
    }

    private static readonly string LeM = AlSessionStateTracker.MaybeWriteKey(AlSessionStateTracker.LastErrorKind);
    private static readonly string LeC = AlSessionStateTracker.ClearedAtStartKey(AlSessionStateTracker.LastErrorKind);

    /// <summary>A maybe-write is selected but does not end the walk: the definite writer before it
    /// comes too.</summary>
    [Fact]
    public void AMaybeWriter_IsWalkedPast()
    {
        var order = new[] { "C.LeW1", "C.Maybe", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.Maybe"] = Keys(LeM),
            ["C.LeReader"] = Keys(LeR),
        };
        var selected = Keys("C.LeReader");
        AffectedSessionStateSelection.Widen(order, selected, recorded, false, false, false);
        Assert.Equal(new[] { "C.LeReader", "C.LeW1", "C.Maybe" }, Sorted(selected));
    }

    /// <summary>The review's first counterexample, as a unit: B reads WorkDate after the change, so
    /// its last-error write may be gone, and the walk from C passes it to reach W2.</summary>
    [Fact]
    public void Changed_AReaderAfterTheChange_DoesNotEndTheWalk()
    {
        var order = new[] { "C.X", "C.W2", "C.B", "C.C" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.X"] = Keys(W),
            ["C.W2"] = Keys(LeW),
            ["C.B"] = Keys(R, LeW),
            ["C.C"] = Keys(LeR),
        };
        var selected = Keys("C.X");
        AffectedSessionStateSelection.Widen(order, selected, recorded, true, false, false);
        Assert.Equal(new[] { "C.B", "C.C", "C.W2", "C.X" }, Sorted(selected));
    }

    /// <summary>A test whose last error was cleared just before it started (the Test Runner app's
    /// reset) inherits nothing: neither it nor a changed test like it walks back.</summary>
    [Fact]
    public void ClearedAtStart_NoWalk_ChangedOrNot()
    {
        var order = new[] { "C.LeW1", "C.Changed", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW, LeC),
            ["C.Changed"] = Keys(LeC),
            ["C.LeReader"] = Keys(LeC),
        };
        var selected = Keys("C.Changed");
        AffectedSessionStateSelection.Widen(order, selected, recorded, true, false, true);
        Assert.Equal(new[] { "C.Changed" }, Sorted(selected));

        selected = Keys("C.LeReader");
        AffectedSessionStateSelection.Widen(order, selected, recorded, false, false, false);
        Assert.Equal(new[] { "C.LeReader" }, Sorted(selected));
    }

    /// <summary>A test that started cleared ends a walk from a later test that did not: what that
    /// later test sees came after the clear.</summary>
    [Fact]
    public void ClearedAtStart_EndsAWalkFromATestThatWasNotCleared()
    {
        var order = new[] { "C.LeW1", "C.Cleared", "C.LeReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.LeW1"] = Keys(LeW),
            ["C.Cleared"] = Keys(LeC),
            ["C.LeReader"] = Keys(LeR),
        };
        var selected = Keys("C.LeReader");
        AffectedSessionStateSelection.Widen(order, selected, recorded, false, false, false);
        Assert.Equal(new[] { "C.Cleared", "C.LeReader" }, Sorted(selected));
    }

    /// <summary>A writer brought in from before the first change only has to reproduce its recorded
    /// run: it brings the tests of its codeunit up to it, not after it.</summary>
    [Fact]
    public void WidenWithIsolation_AnEarlyWriter_BringsOnlyItsCodeunitsPrefix()
    {
        var order = new[] { "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50100.A3", "Codeunit50101.B1", "Codeunit50102.C1", "Codeunit50102.C2" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Codeunit50100.A1"] = Keys(),
            ["Codeunit50100.A2"] = Keys(LeW),
            ["Codeunit50100.A3"] = Keys(),
            ["Codeunit50101.B1"] = Keys(),
            ["Codeunit50102.C1"] = Keys(R),
            ["Codeunit50102.C2"] = Keys(),
        };
        var selected = Keys("Codeunit50101.B1");
        AffectedSessionStateSelection.WidenWithIsolation(order, selected, recorded, TestIsolation.Codeunit, true, false, false);
        // C1 reads after the change, so its whole codeunit runs; A2 is a writer from before it.
        Assert.Equal(new[] { "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50101.B1", "Codeunit50102.C1", "Codeunit50102.C2" },
            Sorted(selected));
    }

    /// <summary>A test brought in through session state runs with its whole codeunit under Codeunit
    /// isolation, like any other selected test: run alone it would miss the state its codeunit's
    /// earlier tests leave it (#5035). Measured on the corpus: codeunit 60720's last test, brought in
    /// as a last-error writer without its siblings, failed where a full run passes it.</summary>
    [Fact]
    public void WidenWithIsolation_ATestBroughtInThroughSessionState_BringsItsCodeunit()
    {
        var order = new[] { "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50101.B1" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Codeunit50100.A1"] = Keys(),
            ["Codeunit50100.A2"] = Keys(LeW),
            ["Codeunit50101.B1"] = Keys(),
        };
        var selected = Keys("Codeunit50101.B1");
        var (byIsolation, byState) = AffectedSessionStateSelection.WidenWithIsolation(
            order, selected, recorded, TestIsolation.Codeunit, true, false, false);
        Assert.Equal(new[] { "Codeunit50100.A1", "Codeunit50100.A2", "Codeunit50101.B1" }, Sorted(selected));
        Assert.Equal((1, 1), (byIsolation, byState));
    }

    /// <summary>A writer brought in on the first pass is not a change, so the second pass does not
    /// take readers from it: readers come from the first CHANGED test.</summary>
    [Fact]
    public void WidenWithIsolation_ABroughtInWriter_DoesNotPullTheReadersAfterIt()
    {
        var order = new[] { "C.Writer", "C.EarlyReader", "C.ChangedReader" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["C.Writer"] = Keys(W),
            ["C.EarlyReader"] = Keys(R),
            ["C.ChangedReader"] = Keys(R),
        };
        var selected = Keys("C.ChangedReader");
        AffectedSessionStateSelection.WidenWithIsolation(order, selected, recorded, TestIsolation.Test, true, false, false);
        Assert.Equal(new[] { "C.ChangedReader", "C.Writer" }, Sorted(selected));
    }

    /// <summary>With nothing changed, a writer an unknown test brings in only reproduces its recorded
    /// run wherever it sits, so it brings its codeunit's prefix even after the first selected test.</summary>
    [Fact]
    public void WidenWithIsolation_NothingChanged_AWriterAfterTheUnknownTest_BringsOnlyItsPrefix()
    {
        var order = new[] { "Codeunit50100.U1", "Codeunit50101.W1", "Codeunit50101.W2", "Codeunit50101.W3", "Codeunit50102.R1" };
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Codeunit50100.U1"] = Keys(),
            ["Codeunit50101.W1"] = Keys(),
            ["Codeunit50101.W2"] = Keys(W),
            ["Codeunit50101.W3"] = Keys(),
            ["Codeunit50102.R1"] = Keys(R),
        };
        var selected = Keys("Codeunit50100.U1", "Codeunit50102.R1");
        AffectedSessionStateSelection.WidenWithIsolation(order, selected, recorded, TestIsolation.Codeunit, false, false, false);
        Assert.Equal(new[] { "Codeunit50100.U1", "Codeunit50101.W1", "Codeunit50101.W2", "Codeunit50102.R1" }, Sorted(selected));
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
