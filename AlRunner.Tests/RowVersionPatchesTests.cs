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
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class RowVersionPatchesTests
{
    private const int CompanyToken = 0;

    // Keep in sync with every private static PropertyInfo/FieldInfo/MethodInfo cache
    // field declared across RowVersionPatches.cs AND RowVersionPatches.SystemIdIntegrity.cs
    // (same class, split across two files — see that file's header) — a field left out
    // here leaks a resolution from one test into the next.
    private static void ResetReflectionCache()
    {
        var t = typeof(RowVersionPatches);
        foreach (var name in new[]
        {
            "_pMetaTable", "_pTimestampField", "_pFieldIndex", "_pItem", "_mCreate",
            "_pSystemIdField", "_pSystemIdProp", "_pReadOnlyBuffer", "_pReadOnlyBufferSystemId",
            "_pTableCaptionSafe", "_fPrimaryTree", "_mCreateUniqueConstraint",
        })
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"test setup: RowVersionPatches.{name} not found");
            f.SetValue(null, null);
        }

        // _rowSystemIdGetters replaced the old _pRowSystemId PropertyInfo cache: the
        // per-stored-row SystemId read is now a compiled delegate keyed by row type, not a
        // PropertyInfo.GetValue call. It is readonly and non-null, so it is cleared rather
        // than nulled — same purpose as the loop above, keeping one test's resolution from
        // leaking into the next.
        var getters = t.GetField("_rowSystemIdGetters", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("test setup: RowVersionPatches._rowSystemIdGetters not found");
        ((System.Collections.IDictionary)getters.GetValue(null)!).Clear();
    }

    private static object MarkDatabaseBackedProvider(object? provider = null)
    {
        provider ??= new object();
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
        public FakeMetaField? SystemIdField { get; }
        public string? TableCaptionSafe { get; }
        public FakeMetaTable(FakeMetaField? timestampField, FakeMetaField? systemIdField = null,
            string? tableCaptionSafe = "Fake Table")
        {
            TimestampField = timestampField;
            SystemIdField = systemIdField;
            TableCaptionSafe = tableCaptionSafe;
        }
    }

    private sealed class FakeReadOnlyBuffer
    {
        public NavGuid SystemId { get; }
        public FakeReadOnlyBuffer(NavGuid systemId) => SystemId = systemId;
    }

    private sealed class FakeBuffer
    {
        public FakeMetaTable MetaTable { get; }
        public FakeReadOnlyBuffer? ReadOnlyBuffer { get; set; }
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
        // Mirrors the real MutableRecordBuffer.SystemId getter body verbatim (decompiled
        // BC 28.1 Ncl.dll): reads through the indexer at SystemIdField.FieldIndex. Only
        // ever invoked by production code once ResolveSystemIdFieldIndex has already
        // confirmed SystemIdField is non-null, so the ! here matches that guarantee.
        public NavGuid SystemId => (NavGuid)(this[MetaTable.SystemIdField!.FieldIndex] ?? NavGuid.Null);
    }

    // A stored row in the provider's primary tree — stands in for a real
    // TempTableRecordBuffer, whose own SystemId property CheckNoDuplicateSystemId
    // reaches purely by reflection (see RowVersionPatches.SystemIdIntegrity.cs).
    private sealed class FakeStoredRow
    {
        public NavGuid SystemId { get; }
        public FakeStoredRow(NavGuid systemId) => SystemId = systemId;
    }

    // Stands in for TempTableDataProvider: CheckNoDuplicateSystemId reaches its
    // "primaryTree" field by name via reflection, matching the real (internal,
    // private) field it targets.
    private sealed class FakeProvider
    {
        private readonly System.Collections.IEnumerable? primaryTree;
        public FakeProvider(System.Collections.IEnumerable? primaryTree = null) => this.primaryTree = primaryTree;
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

    // ── #2573: Insert refuses a duplicate explicit SystemId ────────────────────────

    [Fact]
    public void OnBeforeInsert_DuplicateSystemId_Throws_WithUniqueIndexMessage()
    {
        ResetReflectionCache();
        var duplicateId = NavGuid.NewGuid();
        var provider = new FakeProvider(new object[] { new FakeStoredRow(duplicateId) });
        MarkDatabaseBackedProvider(provider);
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1) { [0] = duplicateId };

        var ex = Record.Exception(() => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.NotNull(ex);
        Assert.Contains("unique index", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Negative control: a DIFFERENT explicit SystemId must insert cleanly, so an
    // implementation that refused every second Insert() (regardless of SystemId)
    // fails this test.
    [Fact]
    public void OnBeforeInsert_DifferentSystemId_DoesNotThrow()
    {
        ResetReflectionCache();
        var provider = new FakeProvider(new object[] { new FakeStoredRow(NavGuid.NewGuid()) });
        MarkDatabaseBackedProvider(provider);
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1) { [0] = NavGuid.NewGuid() };

        var record = Record.Exception(() => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.Null(record);
    }

    // A zero/empty incoming SystemId (none supplied — the UUID-generation hook will
    // assign one) must never even reach the provider's primary tree, so a provider
    // with no primaryTree support at all (a plain object) is still safe.
    [Fact]
    public void OnBeforeInsert_ZeroSystemId_SkipsCheck_EvenWithoutPrimaryTreeSupport()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider(); // plain object — no "primaryTree" field
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1); // slot 0 unset => NavGuid.Null

        var record = Record.Exception(() => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.Null(record);
    }

    // Loud-failure companion to the two guard-clause cases above: an EXPLICIT,
    // non-zero SystemId on a provider that genuinely lacks "primaryTree" must throw
    // naming the missing member, not silently skip the check.
    [Fact]
    public void OnBeforeInsert_ProviderMissingPrimaryTreeField_ExplicitSystemId_ThrowsNamingTheMember()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider(); // plain object — no "primaryTree" field
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1) { [0] = NavGuid.NewGuid() };

        var ex = Assert.Throws<InvalidOperationException>(
            () => RowVersionPatches.OnBeforeInsert(provider, CompanyToken, buffer));

        Assert.Contains("primaryTree", ex.Message);
    }

    // ── #2573: Modify never lets an existing row's SystemId change ────────────────

    [Fact]
    public void OnBeforeModify_IncomingSystemIdDiffersFromStored_RestoresTheStoredValue()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var storedId = NavGuid.NewGuid();
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1)
        {
            ReadOnlyBuffer = new FakeReadOnlyBuffer(storedId),
            [0] = NavGuid.NewGuid(), // wrong value about to be written — simulates a corrupted/reset slot
        };

        RowVersionPatches.OnBeforeModify(provider, CompanyToken, buffer);

        Assert.Equal(storedId.Value, ((NavGuid)buffer[0]!).Value);
    }

    [Fact]
    public void OnBeforeModify_IncomingSystemIdMatchesStored_LeavesItUnchanged()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var id = NavGuid.NewGuid();
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1)
        {
            ReadOnlyBuffer = new FakeReadOnlyBuffer(id),
            [0] = id,
        };

        RowVersionPatches.OnBeforeModify(provider, CompanyToken, buffer);

        Assert.Equal(id.Value, ((NavGuid)buffer[0]!).Value);
    }

    [Fact]
    public void OnBeforeModify_TableWithNoSystemIdField_DoesNotThrow_StaysQuiet()
    {
        ResetReflectionCache();
        var provider = MarkDatabaseBackedProvider();
        var metaTable = new FakeMetaTable(timestampField: null); // SystemIdField defaults to null
        var buffer = new FakeBuffer(metaTable, slotCount: 1);

        var record = Record.Exception(() => RowVersionPatches.OnBeforeModify(provider, CompanyToken, buffer));

        Assert.Null(record);
    }

    // Mirrors the file's existing "not database-backed => temporary" guard: a
    // `temporary` record's SystemId has no DB-level immutability on real BC either,
    // so a mismatched incoming value must be left exactly as the caller set it.
    [Fact]
    public void OnBeforeModify_ProviderNotDatabaseBacked_LeavesMismatchedSystemIdUntouched()
    {
        ResetReflectionCache();
        var provider = new object(); // never marked database-backed
        var metaTable = new FakeMetaTable(timestampField: null, systemIdField: new FakeMetaField(0));
        var buffer = new FakeBuffer(metaTable, slotCount: 1)
        {
            ReadOnlyBuffer = new FakeReadOnlyBuffer(NavGuid.NewGuid()),
            [0] = NavGuid.NewGuid(),
        };
        var incoming = (NavGuid)buffer[0]!;

        RowVersionPatches.OnBeforeModify(provider, CompanyToken, buffer);

        Assert.Equal(incoming.Value, ((NavGuid)buffer[0]!).Value);
    }
}
