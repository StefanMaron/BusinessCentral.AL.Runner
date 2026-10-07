// PlainRecordFallbackGateTests — a missing Record{id} class is only tolerated where BC says it can be (#5382).
//
// BC 29's compiler emits no Record{id} class for a table without code and answers StubNavRecord for it
// (NCLMetaTable.LoadClrType). The runner builds a plain NavRecord then. On BC 27/28 every table has a class,
// so a missing one means something was dropped (an emit-excluded table, a failed load) and must stay the loud
// error it was, not a trigger-less record built without a word.
//
// The gate is decided by whether the running BC has a StubNavRecord type, so it is passed in: a test on a BC 29
// build cannot otherwise reach the 27/28 branch. Each test fails the obvious wrong implementation: "always
// build the plain record" fails the first, "always throw" fails the second.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PlainRecordFallbackGateTests
{
    [Fact]
    public void NoRecordClassAndNoStubType_IsTheOriginalLoudError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecordPatches.NewRecordInstance(
                recordType: null, parent: null, metaTable: new object(), tableId: 5471, isTemporary: false,
                securityFiltering: new object(), stubNavRecordType: null));

        Assert.Equal("no loaded type Record5471 found", ex.Message);
    }

    [Fact]
    public void NoRecordClassButAStubType_IsNotRefusedByTheGate()
    {
        // The gate passes; what happens next is the plain-record build, which this fake metatable cannot
        // complete, so what is asserted is only that the refusal text is not the gate's.
        var ex = Record.Exception(() =>
            RecordPatches.NewRecordInstance(
                recordType: null, parent: null, metaTable: new object(), tableId: 5471, isTemporary: false,
                securityFiltering: new object(), stubNavRecordType: typeof(object)));

        Assert.True(ex is null || ex.Message != "no loaded type Record5471 found",
            "the gate refused although BC has a StubNavRecord type: " + ex?.Message);
    }

    [Fact]
    public void ARecordClass_IsNeverAffectedByTheGate()
    {
        // A recordType present means the gate has nothing to decide, with or without a stub type.
        RecordPatches.ThrowIfNoRecordTypeAndNoStub(typeof(object), null, 5471);
        RecordPatches.ThrowIfNoRecordTypeAndNoStub(typeof(object), typeof(object), 5471);
    }

    [Theory]
    [InlineData(false, false, false)]   // neither: the loud error (BC 27/28 with a dropped table)
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]     // BC 29: no class, but BC answers StubNavRecord
    [InlineData(true, true, true)]
    public void ThePredicateTheThreeSitesShare(bool hasRecordClass, bool hasStub, bool expected)
    {
        // CreateTarget and RecordRef.Open reach it through NewRecordInstance, TestPageFactory calls it directly.
        Assert.Equal(expected, RecordPatches.RecordTypeOrStubAvailable(
            hasRecordClass ? typeof(object) : null, hasStub ? typeof(object) : null));
    }
}
