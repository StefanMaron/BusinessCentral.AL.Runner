// BlobStoreIsolationPatchesTests — contract tests for the Rename BLOB-loss fix
// (issue #1765, see BlobStoreIsolationPatches.cs file header for the full story).
//
// These pin the C# CONTRACT the fix depends on — not "what BC does" (that's the job of
// the upstream corpus PR, StefanMaron/BusinessCentral.AL.Language.Tests#30, codeunit
// 60944 "Test Blob Rename Isolation", green on real BC 27.5/28.3). What's provable
// here without a loaded BC runtime is the actual marking/lookup logic at the unit
// level: OnModifyAllTrees(...) marks a (row, field index) pair ineligible for
// FlowFieldPatches.LoadBlobField's reload exactly when the corpus measurement says
// real BC loses the value — a temporary-table row, renamed, carrying a BLOB that was
// NOT freshly dirtied by this call — and IsFieldIneligibleForCalcFieldsReload(...)
// reports that pair back correctly. Crucially, the asymmetry the corpus measurement
// found is pinned directly: the identical shape on a DATABASE-BACKED provider must
// NOT be marked, because real BC's database-backed Rename() keeps the BLOB intact.
//
// NavBLOB is Ncl's own sealed type (Microsoft.Dynamics.Nav.Runtime.NavBLOB); it is
// constructed for real here (AlRunner.Tests already references Ncl.dll directly —
// see the .csproj) rather than faked, since BlobStoreIsolationPatches pattern-matches
// on it with `as NavBLOB` / `is NavBLOB`, not via reflection on a duck-typed shape.
// Its IsDirty setter is `internal`, so SetDirty below reaches it via reflection —
// exactly the kind of boundary-crossing the patch itself already does throughout.
//
// The other objects (mutableRecordBuffer, metaTable, field, row) ARE reflected-shape
// fakes, mirroring MediaSetPatchesTests' FakeMediaValue/FakeNavGuid approach: the
// patch reaches them purely by reflection (property/method names), so a plain POCO
// with those members exercises the same reflection path the real Ncl
// MutableRecordBuffer/NCLMetaTable/NCLMetaField/TempTableRecordBuffer would.
using System;
using System.Collections.Generic;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class BlobStoreIsolationPatchesTests
{
    private sealed class FakeField
    {
        public NavNclType FieldNclType { get; }
        public FakeField(NavNclType t) => FieldNclType = t;
    }

    private sealed class FakeMetaTable
    {
        private readonly FakeField[] _fields;
        public FakeMetaTable(params FakeField[] fields) => _fields = fields;
        public FakeField GetFieldByIndex(int i) => _fields[i];
    }

    private sealed class FakeMutableRecordBuffer
    {
        public FakeMetaTable MetaTable { get; }
        public int FieldCount => _changed.Length;
        private readonly NavBLOB?[] _changed;
        public FakeMutableRecordBuffer(FakeMetaTable metaTable, NavBLOB?[] changedValuesByIndex)
        {
            MetaTable = metaTable;
            _changed = changedValuesByIndex;
        }
        public NavBLOB? GetChangedFieldValue(int idx) => _changed[idx];
    }

    private sealed class FakeDataAccess
    {
        public object DataProvider { get; }
        public FakeDataAccess(object provider) => DataProvider = provider;
    }

    /// <summary>NavBLOB.IsDirty has an `internal set` — reached via reflection, same
    /// boundary-crossing pattern BlobStoreIsolationPatches itself relies on throughout.</summary>
    private static NavBLOB DirtyBlobWithContent(byte[] bytes)
    {
        var blob = new NavBLOB(bytes, useContentInstance: true);
        typeof(NavBLOB).GetProperty("IsDirty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(blob, true);
        Assert.True(blob.IsDirty); // sanity: the helper actually worked
        return blob;
    }

    /// <summary>A BLOB carrying content from BEFORE this Modify()/Rename() call — the
    /// exact "carried over, not freshly written" shape 60944's surprise pins.</summary>
    private static NavBLOB NonDirtyBlobWithContent(byte[] bytes)
        => new(bytes, useContentInstance: true); // isDirty defaults to false — no constructor sets it

    private const int BlobFieldIndex = 0;

    private static FakeMutableRecordBuffer OneBlobFieldBuffer(NavBLOB? changedValue)
        => new(new FakeMetaTable(new FakeField(NavNclType.NavBlob)), new[] { changedValue });

    // ── The core #1765 finding: temporary + rename + non-dirty BLOB → marked ──────

    [Fact]
    public void OnModifyAllTrees_TemporaryProvider_RenameWithNonDirtyBlob_MarksFieldIneligible()
    {
        var provider = new object(); // never registered via MarkDatabaseBacked => temporary
        var mutableRecordBuffer = OneBlobFieldBuffer(NonDirtyBlobWithContent(new byte[] { 1, 2, 3 }));
        var workTableBuffer = new object();   // freshly constructed row = a rename happened
        var storedTableBuffer = new object(); // the removed old-key row — distinct object

        BlobStoreIsolationPatches.OnModifyAllTrees(provider, mutableRecordBuffer, workTableBuffer, storedTableBuffer);

        Assert.True(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(workTableBuffer, BlobFieldIndex));
    }

    // ── The uncommitted-write control: dirty BLOB on the SAME call → never marked ──
    // (60944's Blob_UncommittedWrite_Rename_* / TempBlob_UncommittedWrite_Rename_*
    // shape: the write reaches the row via Ncl's own dirty-BLOB path, so nothing here
    // needs to intervene.)

    [Fact]
    public void OnModifyAllTrees_TemporaryProvider_RenameWithDirtyBlob_DoesNotMark()
    {
        var provider = new object();
        var mutableRecordBuffer = OneBlobFieldBuffer(DirtyBlobWithContent(new byte[] { 1, 2, 3 }));
        var workTableBuffer = new object();
        var storedTableBuffer = new object();

        BlobStoreIsolationPatches.OnModifyAllTrees(provider, mutableRecordBuffer, workTableBuffer, storedTableBuffer);

        Assert.False(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(workTableBuffer, BlobFieldIndex));
    }

    // ── The asymmetry that makes this issue worth a regression test: the IDENTICAL
    // shape on a database-backed provider must NOT be marked — real BC's Rename()
    // keeps a Modify()-committed BLOB intact for that shape
    // (Blob_CommittedWrite_Rename_SecondInstanceGet_ReadsWrittenBytes in 60944). If
    // this patch's database-backed guard is ever "simplified" away, this is the test
    // that catches it turning the database shape into a false leak — the exact trap
    // #1751's fix for Insert already existed to avoid.

    [Fact]
    public void OnModifyAllTrees_DatabaseBackedProvider_RenameWithNonDirtyBlob_NeverMarks()
    {
        var provider = new object();
        BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(provider));

        var mutableRecordBuffer = OneBlobFieldBuffer(NonDirtyBlobWithContent(new byte[] { 9, 9, 9 }));
        var workTableBuffer = new object();
        var storedTableBuffer = new object();

        BlobStoreIsolationPatches.OnModifyAllTrees(provider, mutableRecordBuffer, workTableBuffer, storedTableBuffer);

        Assert.False(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(workTableBuffer, BlobFieldIndex));
    }

    // ── Not a rename at all (plain Modify): workTableBuffer IS storedTableBuffer ──
    // Ncl's own dirty-BLOB path in TempTableDataProvider.Modify already handles this
    // shape correctly (see 60940's committed positive controls) — nothing to mark
    // regardless of dirty state.

    [Fact]
    public void OnModifyAllTrees_PlainModify_SameWorkAndStoredBuffer_NeverMarks()
    {
        var provider = new object();
        var mutableRecordBuffer = OneBlobFieldBuffer(NonDirtyBlobWithContent(new byte[] { 4, 5, 6 }));
        var sameBuffer = new object(); // workTableBuffer === storedTableBuffer: not a rename

        BlobStoreIsolationPatches.OnModifyAllTrees(provider, mutableRecordBuffer, sameBuffer, sameBuffer);

        Assert.False(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(sameBuffer, BlobFieldIndex));
    }

    // ── Lookup-side controls ───────────────────────────────────────────────────────

    [Fact]
    public void IsFieldIneligibleForCalcFieldsReload_UnknownRow_ReturnsFalse()
    {
        Assert.False(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(new object(), BlobFieldIndex));
    }

    [Fact]
    public void IsFieldIneligibleForCalcFieldsReload_NullRow_ReturnsFalse()
    {
        Assert.False(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(null, BlobFieldIndex));
    }

    [Fact]
    public void OnModifyAllTrees_MarkedRow_OtherFieldIndexOnSameRow_StaysEligible()
    {
        // Two BLOB fields on the row: field 0 carried over (marked), field 1 freshly
        // dirtied by this same call (not marked) — the marker must be per-field, not
        // blanket the whole row.
        var provider = new object();
        var metaTable = new FakeMetaTable(new FakeField(NavNclType.NavBlob), new FakeField(NavNclType.NavBlob));
        var mutableRecordBuffer = new FakeMutableRecordBuffer(metaTable, new NavBLOB?[]
        {
            NonDirtyBlobWithContent(new byte[] { 1 }),
            DirtyBlobWithContent(new byte[] { 2 }),
        });
        var workTableBuffer = new object();
        var storedTableBuffer = new object();

        BlobStoreIsolationPatches.OnModifyAllTrees(provider, mutableRecordBuffer, workTableBuffer, storedTableBuffer);

        Assert.True(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(workTableBuffer, 0));
        Assert.False(BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(workTableBuffer, 1));
    }
}
