// ObjectMetadataTestDataProvenanceTests — the no-source-column refusal must not fire over rows
// that came from --test-data.
//
// RecordPatches.ObjectMetadataSystemTable.cs states the precedence in its own header:
//
//     the --test-data on-demand loader run FIRST on a freshly created store, and this
//     populator then does nothing at all if the store already has any row. Real rows always
//     win over synthesised ones
//
// and implements it with `if (ProviderHasAnyRow(provider)) return;`.
//
// The refusal added for #2771 did not honour that. Both of its seams resolve through
// RecordPatches.NoSourceFieldsFor, which keyed only on metaTable.TableId == 2000000071 and
// consulted nothing about where the rows came from. So in a --test-data run whose backup
// carries real Object Metadata rows with a genuine published payload, reading Metadata / Hash /
// "Has Subscribers" raised
//
//     out-of-scope: Object Metadata."Metadata" (system table 2000000071)
//
// over data that HAS a source — a loud failure on correct data, and the mirror image of the
// spurious-refusal half of the torn-pair bug, reached by a different route.
//
// That path is reachable rather than theoretical: docs/limitations.md names Microsoft's
// Tests-SINGLESERVER bucket as the settling route for this table, that bucket is OnPrem-target
// and reads 2000000071 directly, and --test-data is mandatory for those buckets.
//
// WHAT THIS FILE CAN AND CANNOT ASSERT. The production readers take a live NavRecord or a live
// NCLMetaField, neither of which a unit test can build without a loaded bundle — the same
// constraint NoSourceColumnCacheTornPairTests documents. So these tests exercise the PROVENANCE
// STATE that gates the refusal, which is the thing the fix adds, rather than driving a full
// --test-data restore. The end-to-end direction is covered by the corpus and runner-extras runs.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection, not a collection of its own (#3101 review). The provenance flag
// is process-wide, and the fix below makes RecordPatches.ResetForReload() clear it — so every one
// of the ~20 classes in that collection which calls ResetForReload() can now land between a
// MarkObjectMetadataRowsAreReal() here and the assertion that reads it. A private collection name
// serialises this class against ITSELF only; xunit still runs it in parallel with every other
// collection, which is exactly the accidental-parallelism shape #1696 documents. Joining the
// serial collection is what actually fences the shared static.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ObjectMetadataTestDataProvenanceTests
{
    // 2000000071. Registered in _noSourceColumnNames; the only table that is.
    private const int ObjectMetadata = 2000000071;

    // An arbitrary table with nothing registered, used as the negative control throughout.
    private const int Customer = 18;

    public ObjectMetadataTestDataProvenanceTests()
        => RecordPatches.ResetObjectMetadataRowProvenance();

    [Fact]
    public void Synthesised_TheDefault_RefusesThePayloadColumns()
    {
        // Nothing has said the rows are real, so the runner synthesised them (or the store is
        // empty) and the nine columns genuinely have no source.
        Assert.False(RecordPatches.ObjectMetadataRowsAreReal);
        Assert.True(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));
    }

    [Fact]
    public void TestDataSuppliedRealRows_DoesNotRefuse()
    {
        // This is the defect. ProviderHasAnyRow() answered true, so PopulateObjectMetadata-
        // SystemTable left the store alone and every row in it came from the backup.
        RecordPatches.MarkObjectMetadataRowsAreReal();

        Assert.True(RecordPatches.ObjectMetadataRowsAreReal);
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));
    }

    [Fact]
    public void RealRows_DoNotSwitchOnRefusalsForSomeOtherTable()
    {
        // The negative twin of the above: marking provenance must not be a global "refuse
        // nothing" switch, nor turn an unregistered table into a refusing one.
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(Customer));
        RecordPatches.MarkObjectMetadataRowsAreReal();
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(Customer));
    }

    [Fact]
    public void ProvenanceIsOneWay_RealRowsAreNeverUnlearnedWithinARun()
    {
        // The populator runs once per provider and a run can create more than one store. If any
        // store was found holding real rows, a later synthesising store must not re-arm the
        // refusal for the whole process — that would refuse over the real rows again.
        RecordPatches.MarkObjectMetadataRowsAreReal();
        RecordPatches.MarkObjectMetadataRowsAreReal();

        Assert.True(RecordPatches.ObjectMetadataRowsAreReal);
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));
    }

    // ── THE RELOAD BOUNDARY (#3101 review) ────────────────────────────────────────────────
    // Everything above is about ONE run. These two are about the seam between two of them.
    //
    // The flag summarises a fact about the in-memory row store: "a store for 2000000071 was
    // found already holding rows". RecordPatches.ResetForReload() DROPS that store
    // (_dataAccessByTable.Clear()) so a --server/--watch process can load the next bundle
    // against empty tables. A latch that outlives the state it describes then answers for rows
    // that no longer exist, and because it is one-way nothing can put it back.
    //
    // The consequence is silent, which is what makes it worth a test rather than a comment:
    // NoSourceRefusalIsActiveFor(2000000071) answers false, and the nine payload columns go
    // back to reading BLANK instead of refusing — #2771's original defect, reintroduced by the
    // change that closes #2771. Same shape as #2478 and #2755 in RecordPatches.ResetForReload's
    // own body, and as #3184's object-reference const memo: per-run derived state that the
    // reload path did not know it owned.

    [Fact]
    public void BundleReload_ReArmsTheRefusal_BecauseTheRowsItDescribedAreGone()
    {
        RecordPatches.MarkObjectMetadataRowsAreReal();
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));

        // The --server/--watch bundle-reload path. It clears _dataAccessByTable, so after this
        // line there is no store holding real Object Metadata rows for the flag to be about.
        RecordPatches.ResetForReload();

        Assert.False(RecordPatches.ObjectMetadataRowsAreReal);
        Assert.True(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));
    }

    [Fact]
    public void BundleReload_DoesNotMakeTheClearOneWayInTheOtherDirection()
    {
        // The negative twin. Re-arming at the reload must not stop the NEXT bundle learning its
        // own provenance: bundle 2 restores real rows too, the populator marks it again, and the
        // refusal must go back off. A clear that could only ever be undone once would refuse
        // over correct data from bundle 2 onwards — the loud-failure-on-good-data half of this
        // file's defect, moved one bundle to the right.
        RecordPatches.MarkObjectMetadataRowsAreReal();
        RecordPatches.ResetForReload();
        Assert.True(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));

        RecordPatches.MarkObjectMetadataRowsAreReal();

        Assert.True(RecordPatches.ObjectMetadataRowsAreReal);
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(ObjectMetadata));
        // ...and still not a global "refuse nothing" switch.
        Assert.False(RecordPatches.NoSourceRefusalIsActiveFor(Customer));
    }
}
