// CodeunitSubscriberWitnessMultiChunkTests — the widening invariant on
// RecordPatches.RegisterCodeunitSubscriberWitness (#4090).
//
// WHAT IS BEING PINNED
//   One app's codeunits are spread across several R2R chunks, so the witness for that app is
//   assembled by SEVERAL registrations against ONE app path. The merge must WIDEN: a later
//   chunk adds what it saw and never drops what an earlier one saw. RegisterCodeunitSubscriberWitness
//   states that invariant in a comment and implements it with an AddOrUpdate whose update arm
//   UnionWith-es both sets.
//
// WHY IT NEEDED ITS OWN FIXTURE
//   Every test #4078 shipped registers exactly ONCE per app path, so the update arm never ran.
//   Replacing the whole AddOrUpdate with a plain `_codeunitSubscriberWitness[key] = new(...)`
//   left 51 tests GREEN — measured on this branch before this file existed. A green mutation
//   means "no fixture can see this", not "the code is right" (tdd.md).
//
// WHY THE OBSERVABLE IS A *MISSING* SUBTREE, WHICH IS THE DANGEROUS DIRECTION
//   Narrowing does not produce a wrong value; it produces an ABSENCE. AssemblyProvesNoSubscriber
//   answers false for a codeunit the witness no longer lists as scanned, the subtree stays
//   absent, and that reads as one of the 174 honest absences #4078 already accepts. Nothing
//   counts it as a regression.
//
// THE SCALE, MEASURED RATHER THAN ASSUMED
//   Microsoft_Base Application_28.1.49838.53910.app carries FIVE R2R chunks. All five carry
//   Codeunit<N> types: 205 / 335 / 322 / 356 / 355 distinct ids, 1,573 in total, and every id
//   appears in exactly ONE chunk — a clean partition, no overlap. So on Base Application the
//   merge is not an edge case but the only route to a complete witness: a replace would keep
//   whichever chunk registered last, about 355 of 1,573 ids, and silently return the other ~77%
//   to the unknown state. (#4078's own measurements ran against System Application, which ships
//   as a SINGLE chunk — which is exactly why they never drove this path.)

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Shares the process-global _codeunitSubscriberWitness dictionary with the other RecordPatches
// suites, and clears it — so it must not run beside them (#1696).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CodeunitSubscriberWitnessMultiChunkTests : IDisposable
{
    /// <summary>Scanned by the first chunk only, and clear of subscribers there.</summary>
    private const int ClearInFirstChunk = 71001;

    /// <summary>Scanned by the second chunk only, and clear of subscribers there.</summary>
    private const int ClearInSecondChunk = 71002;

    /// <summary>Declares a subscriber, and the FIRST chunk is what saw it.</summary>
    private const int SubscriberInFirstChunk = 71003;

    /// <summary>Declares a subscriber, and the SECOND chunk is what saw it.</summary>
    private const int SubscriberInSecondChunk = 71004;

    /// <summary>Named by no registration at all: the unknown state, which must stay unknown
    /// however many chunks are merged.</summary>
    private const int NeverScanned = 71005;

    public CodeunitSubscriberWitnessMultiChunkTests()
        => RecordPatches.ClearCodeunitSubscriberWitnessForTests();

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
    }

    /// <summary>
    /// An app path that does NOT exist on disk. EnsureCodeunitSubscriberWitness consults the
    /// in-memory registry before it looks at the filesystem, so a registered path is answered
    /// from the registry; and a path that was never registered returns null at the File.Exists
    /// check rather than trying to unpack a package. That keeps these tests about the merge and
    /// nothing else.
    ///
    /// <para>Routed through TestScratch.FilePath rather than Path.GetTempPath() so the location
    /// has a recorded owner even though nothing here ever creates it (#2743).</para>
    /// </summary>
    private static string AppPath([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => TestScratch.FilePath(
            "al-runner-witness-multichunk", $"al-runner-witness-multichunk-{name}.app");

    /// <summary>
    /// The invariant itself: two chunks of ONE app, each clearing a DIFFERENT codeunit, and both
    /// answers survive the second registration.
    ///
    /// <para>Under a replacing merge the first chunk's codeunit falls out of ScannedCodeunitIds,
    /// so AssemblyProvesNoSubscriber answers false for it and its method subtree silently stops
    /// being emitted.</para>
    /// </summary>
    [Fact]
    public void A_second_chunk_widens_the_cleared_set_rather_than_replacing_it()
    {
        var appPath = AppPath();

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ClearInFirstChunk });

        // The first chunk's answer, before the second one arrives. Asserted so that a failure
        // below is unambiguously the MERGE and not a registration that never took.
        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appPath, ClearInFirstChunk));

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ClearInSecondChunk });

        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appPath, ClearInFirstChunk));
        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appPath, ClearInSecondChunk));
    }

    /// <summary>
    /// The other set widens too, and it is a SEPARATE UnionWith — so it needs its own assertion.
    /// A codeunit whose subscriber the first chunk saw must keep answering "not proven clear"
    /// after a later chunk registers, even though that later chunk scanned it and found nothing.
    ///
    /// <para>This is the direction that would FABRICATE rather than omit: losing a subscriber id
    /// while keeping the codeunit scanned flips AssemblyProvesNoSubscriber from false to true,
    /// and the caller then emits a method subtree from a symbol file that cannot see the
    /// subscriber — the short, positionally mis-paired list #4078 exists to prevent.</para>
    /// </summary>
    [Fact]
    public void A_second_chunk_widens_the_subscriber_set_rather_than_replacing_it()
    {
        var appPath = AppPath();

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath,
            new[] { SubscriberInFirstChunk },
            new[] { SubscriberInFirstChunk, ClearInFirstChunk });

        Assert.False(RecordPatches.AssemblyProvesNoSubscriber(appPath, SubscriberInFirstChunk));

        // A later chunk re-scans the same codeunit and sees no subscriber in ITS half of the
        // type table. That must not clear the id the earlier chunk witnessed.
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath,
            new[] { SubscriberInSecondChunk },
            new[] { SubscriberInFirstChunk, SubscriberInSecondChunk, ClearInSecondChunk });

        Assert.False(RecordPatches.AssemblyProvesNoSubscriber(appPath, SubscriberInFirstChunk));
        Assert.False(RecordPatches.AssemblyProvesNoSubscriber(appPath, SubscriberInSecondChunk));

        // ...and the clear ones from both chunks still read as clear, so the assertions above
        // are about the subscriber set and not about the witness having collapsed entirely.
        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appPath, ClearInFirstChunk));
        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appPath, ClearInSecondChunk));
    }

    /// <summary>
    /// Five chunks, matching what Microsoft actually ships for Base Application, each clearing a
    /// disjoint set of ids — the measured shape: 1,573 ids partitioned across five chunks with no
    /// id in two of them.
    ///
    /// <para>Two-chunk cover would pass for a merge that keeps only the FIRST and the LAST
    /// registration. This one does not: it asserts every id from every chunk, so a merge holding
    /// any bounded number of registrations fails on the ids it dropped.</para>
    /// </summary>
    [Fact]
    public void Five_chunks_accumulate_every_id_none_of_them_displacing_an_earlier_one()
    {
        var appPath = AppPath();
        var chunks = new[]
        {
            new[] { 72010, 72011 },
            new[] { 72020, 72021 },
            new[] { 72030, 72031 },
            new[] { 72040, 72041 },
            new[] { 72050, 72051 },
        };

        foreach (var chunk in chunks)
            RecordPatches.RegisterCodeunitSubscriberWitness(appPath, Array.Empty<int>(), chunk);

        var missing = chunks.SelectMany(c => c)
            .Where(id => !RecordPatches.AssemblyProvesNoSubscriber(appPath, id))
            .ToArray();

        Assert.Equal(Array.Empty<int>(), missing);
    }

    /// <summary>
    /// Widening does not mean inventing. A codeunit no registration ever named stays unknown, so
    /// AssemblyProvesNoSubscriber answers false for it — before and after the merge.
    ///
    /// <para>Without this, a "merge" that answered true for everything would satisfy the three
    /// tests above. This is what makes them assertions about the union rather than about
    /// permissiveness (guards-need-a-third-state.md).</para>
    /// </summary>
    [Fact]
    public void Merging_chunks_does_not_clear_a_codeunit_no_chunk_ever_scanned()
    {
        var appPath = AppPath();

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ClearInFirstChunk });
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ClearInSecondChunk });

        Assert.False(RecordPatches.AssemblyProvesNoSubscriber(appPath, NeverScanned));
    }

    /// <summary>
    /// The merge is keyed per app path, so one app's chunks must not widen another's. Two apps
    /// registering disjoint ids leave each other's unknowns unknown.
    ///
    /// <para>A merge that unioned into a single shared bucket would pass every test above and
    /// clear codeunits of an app whose assembly was never read — the cross-app form of the
    /// fabrication the third state exists to prevent.</para>
    /// </summary>
    [Fact]
    public void Widening_is_scoped_to_one_app_path()
    {
        var appA = AppPath() + ".a";
        var appB = AppPath() + ".b";

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appA, Array.Empty<int>(), new[] { ClearInFirstChunk });
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appB, Array.Empty<int>(), new[] { ClearInSecondChunk });

        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appA, ClearInFirstChunk));
        Assert.False(RecordPatches.AssemblyProvesNoSubscriber(appA, ClearInSecondChunk));

        Assert.True(RecordPatches.AssemblyProvesNoSubscriber(appB, ClearInSecondChunk));
        Assert.False(RecordPatches.AssemblyProvesNoSubscriber(appB, ClearInFirstChunk));
    }

    /// <summary>
    /// The key is the FULL path, so two spellings of one file are one app and their chunks merge.
    /// RegisterCodeunitSubscriberWitness calls Path.GetFullPath for exactly this reason.
    ///
    /// <para>Without normalisation the loader's spelling and the on-demand fallback's spelling
    /// would key two separate witnesses for one app, and each would be short — the same silent
    /// narrowing this file pins, arriving by a different route.</para>
    /// </summary>
    [Fact]
    public void Two_spellings_of_one_path_widen_the_same_witness()
    {
        var appPath = AppPath();
        var indirect = Path.Combine(
            Path.GetDirectoryName(appPath)!, ".", Path.GetFileName(appPath));

        // The test's own premise: these really are different strings for one file.
        Assert.NotEqual(appPath, indirect);
        Assert.Equal(Path.GetFullPath(appPath), Path.GetFullPath(indirect));

        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(), new[] { ClearInFirstChunk });
        RecordPatches.RegisterCodeunitSubscriberWitness(
            indirect, Array.Empty<int>(), new[] { ClearInSecondChunk });

        foreach (var spelling in new[] { appPath, indirect })
        {
            Assert.True(RecordPatches.AssemblyProvesNoSubscriber(spelling, ClearInFirstChunk));
            Assert.True(RecordPatches.AssemblyProvesNoSubscriber(spelling, ClearInSecondChunk));
        }
    }
}
