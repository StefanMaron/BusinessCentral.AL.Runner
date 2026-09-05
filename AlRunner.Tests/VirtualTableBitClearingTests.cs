// VirtualTableBitClearingTests — proves #2543: RecordPatches.ClearVirtualBit is a helper
// shared by three virtual tables, and its "already done" memo was keyed on ONE hardcoded
// table id, so the first table to populate latched the other two.
//
// The mechanism
// -------------
// ClearVirtualBit has three callers, each handing it a DIFFERENT NCLMetaTable:
//   - RecordPatches.FieldVirtualTable.cs   — Field,   2000000041
//   - RecordPatches.IntegerVirtualTable.cs — Integer, 2000000026
//   - RecordPatches.DateVirtualTable.cs    — Date,    2000000007
// Every one of them read and wrote a `_virtualBitCleared` HashSet under the constant
// FieldVirtualTableId. Whichever ran first inserted 2000000041; the other two then hit
// `if (_virtualBitCleared.Contains(FieldVirtualTableId)) return;` and returned before ever
// touching their own metatable's `tableTypes` field, so their Virtual bit stayed set.
//
// Measured on BC 28.1 against a bundle that reads Date, then Integer, then Field. With the
// memo in place the runner logged, for the three calls:
//     tableId=2000000007  virtualBitSet=True   -> cleared
//     tableId=2000000026  virtualBitSet=True   -> SKIPPED, bit left set
//     tableId=2000000041  virtualBitSet=True   -> SKIPPED, bit left set
// With the memo removed all three clear, and the second call for a table that is already
// clear correctly no-ops (tableTypes=5, virtualBitSet=False).
//
// A second, independent defect in the same memo: it is process-static and nothing resets it.
// RecordPatches.ResetForReload() clears _metaTableCache, so the next access builds a fresh
// NCLMetaTable with the Virtual bit set again — and the memo then short-circuits it. Under
// --watch and --server that makes the virtual tables correct on cycle 1 and wrong from cycle
// 2 on, for the same unedited bundle. The reload test below pins that direction.
//
// Why this test drives ClearVirtualBit directly rather than through AL
// -------------------------------------------------------------------
// Because the AL-visible consequence is not currently observable, and saying so is part of
// the finding. On BC 28.1 the same probe bundle passes whether or not the bit is cleared —
// Count(), FindSet() and Next() over Date, Integer and Field all answer correctly with the
// Virtual bit still set. So this is a latent defect, not a live wrong answer: the bit is
// demonstrably left set on two of the three tables, and anything that later comes to depend
// on IsVirtualTable=false would fail depending on which virtual table a bundle happened to
// touch first. An AL fixture cannot express that; it would pass before and after and prove
// nothing. This test asserts the mechanical contract the helper is supposed to keep, which
// is exactly the thing that was broken.
using System;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class VirtualTableBitClearingTests : IDisposable
{
    private const int TableTypesVirtualBit = 0x8;

    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public VirtualTableBitClearingTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-2543-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static FieldInfo TableTypesField(NCLMetaTable t) =>
        t.GetType().GetField("tableTypes", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "NCLMetaTable.tableTypes not found by reflection — ClearVirtualBit relies on this field.");

    private static int TableTypes(NCLMetaTable t) =>
        Convert.ToInt32(TableTypesField(t).GetValue(t));

    private static void SetTableTypes(NCLMetaTable t, int value)
    {
        var f = TableTypesField(t);
        FieldPoke.SetInstance(f, t, Enum.ToObject(f.FieldType, value));
    }

    /// <summary>
    /// Materialise a real NCLMetaTable for a freshly declared AL table. Same entry point the
    /// production virtual-table populate paths reach their metatable through.
    /// </summary>
    private NCLMetaTable BuildTable(int tableId, string name)
    {
        var dir = Path.Combine(_root, $"t{tableId}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{tableId}.al"), $$"""
            table {{tableId}} "{{name}}"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                }
                keys
                {
                    key(PK; "No.") { Clustered = true; }
                }
            }
            """);
        RecordPatches.AddSourceDir(dir);

        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);
        var meta = RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, tableId, false, 0);
        Assert.NotNull(meta);
        Assert.Equal(tableId, meta!.TableId);
        return meta;
    }

    [SkippableFact]
    public void ClearVirtualBit_ClearsEveryMetaTableItIsHanded_NotOnlyTheFirst()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Ids picked to be process-wide unique among AlRunner.Tests statics — these land in the
        // same static _parsedTables / _metaTableCache the whole test assembly shares.
        var first = BuildTable(93940, "Bug2543 First");
        var second = BuildTable(93941, "Bug2543 Second");
        var third = BuildTable(93942, "Bug2543 Third");

        // 13 == 0b1101 is the value BC hands the runner for the real virtual tables (measured
        // on BC 28.1 for 2000000007, 2000000026 and 2000000041 alike): the Virtual bit 0x8 plus
        // the System/App/Tenant bits 0x1 and 0x4 that must survive the clear.
        const int virtualPlusOtherBits = 13;
        const int otherBitsOnly = 5;
        foreach (var t in new[] { first, second, third })
        {
            SetTableTypes(t, virtualPlusOtherBits);
            Assert.Equal(virtualPlusOtherBits, TableTypes(t));
        }

        // Three calls in a row, exactly as Date/Integer/Field make them — different metatable
        // each time. Before the fix the first call latched and the second and third returned
        // early, so only `first` came back clear.
        RecordPatches.ClearVirtualBit(first);
        RecordPatches.ClearVirtualBit(second);
        RecordPatches.ClearVirtualBit(third);

        Assert.Equal(otherBitsOnly, TableTypes(first));
        Assert.Equal(otherBitsOnly, TableTypes(second));
        Assert.Equal(otherBitsOnly, TableTypes(third));

        // Stated as the bit-level claim too, so a future change that clears the WRONG bits
        // (e.g. zeroing tableTypes outright) cannot satisfy the equality above by accident.
        foreach (var t in new[] { first, second, third })
        {
            Assert.Equal(0, TableTypes(t) & TableTypesVirtualBit);
            Assert.Equal(otherBitsOnly, TableTypes(t) & ~TableTypesVirtualBit);
        }
    }

    [SkippableFact]
    public void ClearVirtualBit_OnATableWhoseBitIsAlreadyClear_LeavesEveryOtherBitAlone()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The negative direction: the per-instance early return that replaces the memo must be
        // a genuine no-op, not a second clear that could disturb the surviving bits. Without
        // this, "clear the bit every time" could be satisfied by a helper that also stamped
        // over System/App/Tenant on the second pass.
        var t = BuildTable(93943, "Bug2543 AlreadyClear");
        const int otherBitsOnly = 5;
        SetTableTypes(t, otherBitsOnly);

        RecordPatches.ClearVirtualBit(t);
        Assert.Equal(otherBitsOnly, TableTypes(t));

        RecordPatches.ClearVirtualBit(t);
        Assert.Equal(otherBitsOnly, TableTypes(t));
    }

    [SkippableFact]
    public void ClearVirtualBit_AfterResetForReload_StillClearsAFreshMetaTable()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The --watch / --server direction. ResetForReload() clears _metaTableCache, so the next
        // access builds a BRAND NEW NCLMetaTable carrying the Virtual bit again. The old memo
        // was process-static and nothing reset it, so it short-circuited that fresh instance and
        // the table stayed virtual from cycle 2 onward for the same unedited bundle.
        var cycle1 = BuildTable(93944, "Bug2543 Reload");
        const int virtualPlusOtherBits = 13;
        const int otherBitsOnly = 5;

        SetTableTypes(cycle1, virtualPlusOtherBits);
        RecordPatches.ClearVirtualBit(cycle1);
        Assert.Equal(otherBitsOnly, TableTypes(cycle1));

        // Cycle 2: a fresh metatable instance for the same id, with the bit set again.
        var cycle2 = BuildTable(93945, "Bug2543 Reload Cycle2");
        SetTableTypes(cycle2, virtualPlusOtherBits);
        Assert.Equal(virtualPlusOtherBits, TableTypes(cycle2));

        RecordPatches.ClearVirtualBit(cycle2);

        Assert.Equal(otherBitsOnly, TableTypes(cycle2));
        Assert.Equal(0, TableTypes(cycle2) & TableTypesVirtualBit);
    }
}
