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

[Collection("ObjectMetadataProvenance")]
public sealed class ObjectMetadataTestDataProvenanceTests
{
    // 2000000071. Registered in _noSourceColumnNames; the only table that is.
    private const int ObjectMetadata = 2000000071;

    // An arbitrary table with nothing registered, used as the negative control throughout.
    private const int Customer = 18;

    public ObjectMetadataTestDataProvenanceTests()
        => RecordPatches.ResetObjectMetadataRowProvenanceForTests();

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
}
