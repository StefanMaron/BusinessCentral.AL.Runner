// RowVersionPatchesTests — contract tests for issue #1986.
//
// RowVersionPatches.Stamp (added by #1983) resolves five members by reflection
// (MutableRecordBuffer.MetaTable, NCLMetaTable.TimestampField, NCLMetaField.
// FieldIndex, the buffer indexer, NavBigInteger.Create(long)). Before this fix, any
// failed lookup was caught, latched into a process-wide "give up forever" flag, and
// reported with one line to Console.Out — which the test host captures — so the
// #1980 bug it exists to fix (HasBeenInserted permanently false) would silently
// resurface with no visible cause.
//
// These pin the C# CONTRACT directly, the same way BlobStoreIsolationPatchesTests
// pins its sibling patch in the same Cecil-prepend group: reflected-shape fake POCOs
// exercise the exact reflection path Stamp walks, without needing a loaded BC
// runtime. This is runner-internal behaviour (a reflection-resolution failure mode),
// not a claim about what BC does, so it belongs here rather than in the upstream
// corpus — see bc-behavior-tests-go-upstream.md.
//
// RowVersionPatches' PropertyInfo/MethodInfo fields are a process-wide cache keyed
// by nothing but "first successful resolution wins" (mirrors production: every real
// record buffer is the same concrete MutableRecordBuffer type). A fake POCO missing
// a member only exercises the "not found" branch if the cache has not already been
// warmed by a real buffer elsewhere in this process — so every test here resets the
// cache via reflection first, the same boundary-crossing pattern
// BlobStoreIsolationPatchesTests uses for NavBLOB.IsDirty. All tests live in one
// class (xunit runs collections in parallel but a class's own tests serially by
// default), so there is nothing else in-process racing this shared static state.
using System;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class RowVersionPatchesTests
{
    private const int CompanyToken = 0;

    private static void ResetReflectionCache()
    {
        var t = typeof(RowVersionPatches);
        foreach (var name in new[] { "_pMetaTable", "_pTimestampField", "_pFieldIndex", "_pItem", "_mCreate" })
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"test setup: RowVersionPatches.{name} not found");
            f.SetValue(null, null);
        }
    }

    private static object MarkDatabaseBackedProvider()
    {
        var provider = new object();
        BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(provider));
        return provider;
    }

    private sealed class FakeDataAccess
    {
        public object DataProvider { get; }
        public FakeDataAccess(object provider) => DataProvider = provider;
    }

    // A record buffer with no "MetaTable" member at all — simulates a future BC
    // build renaming/removing the very first member Stamp resolves.
    private sealed class BufferMissingMetaTable
    {
    }

    private sealed class FakeMetaField
    {
        public int FieldIndex { get; }
        public FakeMetaField(int fieldIndex) => FieldIndex = fieldIndex;
    }

    private sealed class FakeMetaTable
    {
        public FakeMetaField? TimestampField { get; }
        public FakeMetaTable(FakeMetaField? timestampField) => TimestampField = timestampField;
    }

    private sealed class FakeBuffer
    {
        public FakeMetaTable MetaTable { get; }
        private readonly object?[] _slots;
        public FakeBuffer(FakeMetaTable metaTable, int slotCount)
        {
            MetaTable = metaTable;
            _slots = new object?[slotCount];
        }
        public object? this[int index]
        {
            get => _slots[index];
            set => _slots[index] = value;
        }
    }

    // ── RED case: a failed lookup must throw loudly, not disappear ────────────────

    [Fact]
    public void OnBeforeInsert_BufferMissingMetaTableMember_ThrowsNamingTheMember()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var buffer = new BufferMissingMetaTable();

        var ex = Assert.Throws<InvalidOperationException>(
            () => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.Contains("MetaTable", ex.Message);
        Assert.Contains(nameof(BufferMissingMetaTable), ex.Message);
    }

    [Fact]
    public void OnBeforeModify_BufferMissingMetaTableMember_ThrowsNamingTheMember()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var buffer = new BufferMissingMetaTable();

        var ex = Assert.Throws<InvalidOperationException>(
            () => RowVersionPatches.OnBeforeModify(provider, CompanyToken, buffer));

        Assert.Contains("MetaTable", ex.Message);
    }

    // A repeat call must keep throwing — no "loud once, then permanently silent
    // fallback" latch. That latch was the exact mechanism #1986 forbids: it meant
    // the SECOND and every later insert reverted to the pre-#1980 bug with nothing
    // printed anywhere the test host could see.
    [Fact]
    public void OnBeforeInsert_RepeatedFailedLookup_KeepsThrowing_NeverLatchesSilentFallback()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var buffer = new BufferMissingMetaTable();

        Assert.Throws<InvalidOperationException>(
            () => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));
        // Second call, same process, no reset in between: must throw again.
        Assert.Throws<InvalidOperationException>(
            () => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));
    }

    // ── Not a reflection failure: no timestamp field is a legitimate quiet no-op ──

    [Fact]
    public void OnBeforeInsert_TableWithNoTimestampField_DoesNotThrow_StaysQuiet()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var metaTable = new FakeMetaTable(timestampField: null); // property resolves fine, answers "none"
        var buffer = new FakeBuffer(metaTable, slotCount: 1);

        var record = Record.Exception(() => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.Null(record);
    }

    // ── Positive path still stamps once every member resolves ─────────────────────

    [Fact]
    public void OnBeforeInsert_AllMembersResolve_StampsRowVersionIntoTimestampSlot()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        const int timestampSlot = 0;
        var metaTable = new FakeMetaTable(new FakeMetaField(timestampSlot));
        var buffer = new FakeBuffer(metaTable, slotCount: 1);

        RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer);

        var stamped = Assert.IsType<Microsoft.Dynamics.Nav.Runtime.NavBigInteger>(buffer[timestampSlot]);
        Assert.False(stamped.IsZeroOrEmpty);
    }

    // ── Guard clauses stay quiet: nothing to stamp, no reflection even attempted ──

    [Fact]
    public void OnBeforeInsert_NullBuffer_DoesNotThrow()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();

        var record = Record.Exception(() => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, null));

        Assert.Null(record);
    }

    [Fact]
    public void OnBeforeInsert_ProviderNotDatabaseBacked_DoesNotThrow_EvenWithBrokenBuffer()
    {
        ResetReflectionCache();
        var provider = new object(); // never marked database-backed => temporary
        var buffer = new BufferMissingMetaTable();

        var record = Record.Exception(() => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.Null(record);
    }
}
