using System;
using System.Collections.Generic;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

/// <summary>
/// The record buffer behind a TestPage control write, snapshotted before the write and put
/// back when the write is refused (#3640).
///
/// <para>Observably equivalent: real BC discards the in-memory Rec mutations a page-driven
/// write's triggers made once that write raises, so the still-open page reads the values it
/// held before the write. Measured on a real service tier, not inferred — corpus PR #305's
/// original failed-write arm was answered <c>''</c> on all eight cloud legs (run
/// <c>34319509704</c>) where the runner answered <c>'before;'</c>, unanimously and
/// deterministically. The claim is stated upstream by corpus PR #309; see
/// docs/testpage-write-buffer.md for what the tier measured and what it did not.</para>
///
/// <para>Trap for a later editor: the restore is by <em>value</em>, field by field, because
/// <c>NavRecord.CopyRecord</c> is <c>internal</c>. A NavValue is immutable — BC's
/// <c>SetFieldValue</c> stores the instance and <c>GetFieldValue</c> hands the same one back —
/// so holding the reference is a faithful snapshot. If a future BC build makes NavValue
/// mutable in place, this silently becomes a no-op and every arm still passes.</para>
/// </summary>
internal static class TestPageWriteBuffer
{
    /// <summary>
    /// The buffer a write is unwound against, named so the unwind policy can be exercised
    /// without a loaded BC runtime. <see cref="NavRecordBuffer"/> is the only production
    /// implementation; <c>AlRunner.Tests.TestPageWriteBufferTests</c> drives the same policy
    /// over a dictionary.
    /// </summary>
    internal interface IRestorableBuffer
    {
        /// <summary>The field numbers this buffer restores — see NavRecordBuffer for which
        /// fields are excluded and why.</summary>
        IEnumerable<int> RestorableFieldNos { get; }
        object? Read(int fieldNo);
        void Write(int fieldNo, object? value);
    }

    /// <summary>
    /// Take a snapshot and hand back the action that puts it back, or null when there is
    /// nothing restorable (a record-less binding, or a buffer that cannot be read at all).
    ///
    /// <para>The two-part spelling exists for one caller: <c>LiveNavTestField.Write</c>, whose
    /// IL is read by <c>TestPageNewRowLinePromotionTests</c> to pin that <c>_onBeforeEdit</c>
    /// precedes <c>ALValidateAsync</c> and <c>_onEdited</c> follows it (#2923). Wrapping that
    /// method's body in a lambda moves all three into a compiler-generated closure, and the
    /// ordering test then finds none of its three markers and fails for the wrong reason. So
    /// that caller snapshots inline and restores in its own <c>catch</c>; every other caller
    /// should use <see cref="RunRestoringOnRefusal(NavRecord?, Action)"/>, which cannot get
    /// the try/catch wrong.</para>
    /// </summary>
    internal static Action? Snapshot(NavRecord? record)
    {
        if (record == null) return null;

        var buffer = new NavRecordBuffer(record);
        var snapshot = TrySnapshot(buffer);
        return snapshot == null ? null : () => Restore(buffer, snapshot);
    }

    /// <summary>
    /// Run <paramref name="write"/>, and if it raises, put the record's field values back as
    /// they were before returning the exception to the caller.
    ///
    /// <para>A null record is the record-less binding (a page over no source table), which has
    /// no buffer to restore; the write runs unwrapped rather than being refused.</para>
    /// </summary>
    internal static void RunRestoringOnRefusal(NavRecord? record, Action write)
        => RunRestoringOnRefusal(record == null ? null : new NavRecordBuffer(record), write);

    internal static void RunRestoringOnRefusal(IRestorableBuffer? buffer, Action write)
    {
        if (buffer == null) { write(); return; }

        var snapshot = TrySnapshot(buffer);
        if (snapshot == null) { write(); return; }

        try
        {
            write();
        }
        catch
        {
            Restore(buffer, snapshot);
            throw;
        }
    }

    /// <summary>
    /// The current value of every restorable field, or null if the buffer cannot be read at
    /// all (a closed or stale record — BC's GetFieldValue raises rather than answering).
    /// Failing to snapshot leaves the write unwrapped: keeping today's behaviour is a smaller
    /// error than refusing a write BC allows.
    /// </summary>
    private static Dictionary<int, object?>? TrySnapshot(IRestorableBuffer buffer)
    {
        try
        {
            var snapshot = new Dictionary<int, object?>();
            foreach (var fieldNo in buffer.RestorableFieldNos)
                snapshot[fieldNo] = buffer.Read(fieldNo);
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static void Restore(IRestorableBuffer buffer, Dictionary<int, object?> snapshot)
    {
        foreach (var pair in snapshot)
        {
            // Per field, so one unrestorable field cannot abandon the rest of the row
            // half-restored — a partially-restored buffer is the exact state this rule exists
            // to remove, and it would be harder to diagnose than the un-restored one.
            try { buffer.Write(pair.Key, pair.Value); }
            catch { /* leave that one field as the failed write left it */ }
        }
    }

    private sealed class NavRecordBuffer : IRestorableBuffer
    {
        private readonly NavRecord _record;
        internal NavRecordBuffer(NavRecord record) => _record = record;

        public IEnumerable<int> RestorableFieldNos
        {
            get
            {
                foreach (var field in _record.MetaTable.Fields)
                    if (IsRestorable(field)) yield return field.FieldNo;
            }
        }

        public object? Read(int fieldNo) => _record.GetFieldValue(fieldNo);

        public void Write(int fieldNo, object? value)
        {
            if (value is NavValue navValue) _record.SetFieldValue(fieldNo, navValue);
        }

        /// <summary>
        /// Only a Normal field is snapshotted and restored.
        ///
        /// <para>A FlowField is computed from other rows and holds no stored value of its own —
        /// writing one back would push a cached calculation into the buffer as though it had
        /// been stored. A FlowFilter is a filter, not a value. An inactive or obsoleted field
        /// answers BC's default rather than a stored value
        /// (<c>NavRecord.GetFieldValue</c> returns <c>GetDefaultNavValue</c> for it), so
        /// restoring it would write that default over whatever the buffer holds. Field index 0
        /// is the row timestamp, which <c>SetFieldValue</c> routes to
        /// <c>SetRecordTimestamp</c> rather than to the field store.</para>
        /// </summary>
        private static bool IsRestorable(NCLMetaField field)
            => field.FieldClass == FieldClass.Normal && field.FieldActive && field.FieldIndex != 0;
    }
}
