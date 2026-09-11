// RecordPatches.PartialLoad — Record.AreFieldsLoaded reports the load set BC itself keeps,
// instead of the always-fully-loaded row the runner hands back (#3358).
//
// OBSERVABLY EQUIVALENT (loud-failures.md's audit obligation for a new patch):
// the load set is not missing, only unreachable from where BC reads it. BC maintains it in
// TableState.FieldLoadInfo inside Ncl.dll with no SQL involved — SetLoadFields (both arities),
// AssignKeyAndSortingFields, SetFiltersOnCurrentGroup and AddLoadField all write it — and
// exposes it through RecordImplementation.IsFieldSelectedForLoad, which this helper calls.
// Nothing is invented here. What BC's own AreFieldsLoaded reads instead is the fetched
// buffer's FieldLoadInfo, and the runner's TempTableDataProvider returns whole rows carrying
// the table's DEFAULT load info whatever the request asked for, so the answer was "loaded" for
// every field on every table.
//
// Eagerly loading every field stays faithful: BC's contract is that an omitted field is
// JIT-loaded to its real stored value on access, never blanked, so loading it up front is
// indistinguishable — except through this API. Nothing else calls the rewritten method, and
// the data path is untouched: the read of an unloaded field still lands on BC's
// GetFieldValue `else if` arm, which calls AddLoadField and flips the field to loaded.
//
// The one divergence this leaves — narrowing the load set after a field is already in the
// fetched buffer — is service-tier measured (corpus PR 323, cloud legs 27.3 and 27.5: BC keeps
// the field loaded, and a re-fetch does not change that) and tracked by #3859. Write-up:
// docs/limitations.md#are-fields-loaded-narrow-after-fetch. Alternatives weighed: PR for #3358.
// Reference: upstream corpus codeunit 60775 "Test Record Partial Load".
//
// TRAP: three NEIGHBOURING partial-load rewrites cannot be turned red by any test, and the
// reason is a dead call graph, NOT the R2R inlining the obvious hypothesis reaches for
// (#3372). Measured with find_callers over Ncl.dll, identical on 27.0 and 28.4:
// ALSetBaseLoadFields/0 has ZERO callers; ALSetBaseLoadFields/1's only caller is /0; and
// RecordImplementation.SetLoadFields(FieldLoadInfo)'s only caller is
// DataItemIterator.SetLoadFieldsBasedOnMetadata, which this runner already Cecil-no-ops.
// AL's SetLoadFields reaches the ISet overload instead, never the FieldLoadInfo one. So a
// test going red on those three would mean a caller appeared — which is what
// PartialLoadDeadRewriteTests watches for.

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private const string PartialLoadSurface = "Record.AreFieldsLoaded (partial records)";

    private static FieldInfo? _riMutableRecordBuffer;
    private static MethodInfo? _riIsFieldSelectedForLoad;
    private static FieldInfo? _riMetaTable;
    private static PropertyInfo? _mtFieldCount;
    private static MethodInfo? _mtGetFieldByIndex;
    private static PropertyInfo? _mfFieldNo;
    private static readonly object _partialLoadReflLock = new();

    /// <summary>
    /// Per-record record of which fields a FETCH has actually materialised, which is the thing
    /// BC's own <c>AreFieldsLoaded</c> reports and the runner otherwise cannot see: BC reads
    /// <c>mutableRecordBuffer.ReadOnlyBuffer.FieldLoadInfo</c>, but only
    /// <c>SqlTableDataProvider</c> and its helpers ever construct a buffer carrying a real one
    /// (measured with find_callers over the four <c>ReadOnlyRecordBuffer</c> constructors on
    /// 27.5), and the runner routes every table through <c>TempTableDataProvider</c>, whose
    /// buffers carry the table's DEFAULT load info. See docs/limitations.md#are-fields-loaded-narrow-after-fetch.
    /// </summary>
    private static readonly ConditionalWeakTable<object, FetchedFieldSet> _fetchedFields = new();

    private sealed class FetchedFieldSet
    {
        /// <summary>Identity of the buffer whose fetch was last folded in — NOT a value
        /// comparison: a re-fetch installs a new buffer object, which is the event this
        /// records.</summary>
        public object? LastFoldedBuffer;

        /// <summary>Field numbers a fetch has materialised since the last <c>Clear</c>.</summary>
        public readonly HashSet<int> Loaded = new();
    }

    /// <summary>
    /// Cecil replacement for <c>RecordImplementation.AreFieldsLoaded(IEnumerable&lt;NCLMetaField&gt;)</c>.
    /// Answers from what a FETCH has materialised, which is what real BC reports.
    /// False when the record has never been fetched — BC's own <c>mutableRecordBuffer == null</c>
    /// guard, which is why corpus test <c>PartialLoad_AreFieldsLoaded_BeforeFetch_ReturnsFalse</c>
    /// passes today.
    /// </summary>
    public static bool RecordImplementation_AreFieldsLoaded(object self, object fields)
    {
        EnsurePartialLoadReflection(self.GetType());

        // "Loaded" is a statement about a FETCHED buffer, not about a field list: before any
        // Get/Find nothing is loaded, not even the primary key.
        var buffer = _riMutableRecordBuffer!.GetValue(self);
        if (buffer is null)
        {
            // Clear(Rec) runs BC's ClearRecord, whose whole body is
            // `mutableRecordBuffer = null; ResetRecord();` — so a null buffer IS the reset
            // signal, and the accumulator is dropped with it rather than needing its own hook.
            if (_fetchedFields.TryGetValue(self, out var cleared))
            {
                cleared.Loaded.Clear();
                cleared.LastFoldedBuffer = null;
            }
            return false;
        }

        FoldFetchIntoLoadedSet(self, buffer);

        // An EMPTY field list is "all loaded", which the loop below answers by not iterating —
        // BC's own body does the same. A NULL one is refused by RequiredEnumerable rather than
        // answered: today NavRecord.AreFieldsLoaded's ArgumentNullException.ThrowIfNull upstream
        // makes it unreachable, but that is a property of BC's callers, not of this code,
        // so answering the success value would be the silent default loud-failures.md forbids
        // (#3372; the refusal is executed on a null in PartialLoadFieldListRefusalTests).
        var enumerable = BcShape.RequiredEnumerable(
            fields, "RecordImplementation.AreFieldsLoaded(fields)", PartialLoadSurface,
            "the runner cannot tell which fields were asked about");

        var state = _fetchedFields.GetOrCreateValue(self);
        var oneField = new object?[1];
        foreach (var field in enumerable)
        {
            // The fetched set is AUTHORITATIVE once a fetch has happened — there is no
            // requested-set fallback, and that absence is the fix. A field a fetch materialised
            // stays loaded however the requested set narrows afterwards (corpus 60766 arm 1),
            // and a field no fetch materialised stays unloaded however the requested set widens
            // (arm 2). Consulting IsFieldSelectedForLoad here would answer from the REQUESTED
            // set, which is wrong in whichever direction the request last moved — the
            // divergence #3859 records.
            if (!state.Loaded.Contains(FieldNoOf(field))) return false;
        }
        return true;
    }

    /// <summary>
    /// Records the fields the CURRENT buffer's fetch materialised, once per buffer object.
    /// A fetch installs a NEW <c>MutableRecordBuffer</c>, so buffer identity is the fetch
    /// event; the requested set at that moment (<c>TableState.FieldLoadInfo</c>) is what that
    /// fetch asked for, and the runner's provider returns whole rows, so everything asked for
    /// is genuinely in hand.
    /// </summary>
    private static void FoldFetchIntoLoadedSet(object self, object buffer)
    {
        var state = _fetchedFields.GetOrCreateValue(self);
        if (ReferenceEquals(state.LastFoldedBuffer, buffer)) return;
        state.LastFoldedBuffer = buffer;

        var metaTable = _riMetaTable!.GetValue(self);
        if (metaTable is null) return;
        var fieldCount = (int)_mtFieldCount!.GetValue(metaTable)!;

        var oneField = new object?[1];
        for (var i = 0; i < fieldCount; i++)
        {
            var field = _mtGetFieldByIndex!.Invoke(metaTable, new object?[] { i });
            if (field is null) continue;
            oneField[0] = field;
            if (_riIsFieldSelectedForLoad!.Invoke(self, oneField) is true)
                state.Loaded.Add(FieldNoOf(field));
        }
    }

    /// <summary>
    /// Prepended to <c>RecordImplementation.ClearRecord()</c>, whose own body is
    /// <c>mutableRecordBuffer = null; ResetRecord();</c> — a clear discards the materialised
    /// row, so the record of what a fetch had materialised goes with it. Without this the
    /// negative half of corpus 60766 arm 3 reads a field as loaded that
    /// <c>Clear(Rec)</c> dropped.
    ///
    /// <c>ClearRecord</c> has exactly one caller in Ncl.dll — <c>NavRecord.Clear()</c>,
    /// measured with find_callers on 27.5 — so this observes AL's <c>Clear</c> and nothing else.
    /// </summary>
    public static void RecordImplementation_ClearRecord_Prologue(object self)
    {
        if (_fetchedFields.TryGetValue(self, out var state))
        {
            state.Loaded.Clear();
            state.LastFoldedBuffer = null;
        }
    }

    /// <summary>
    /// Prepended to <c>RecordImplementation.AddLoadField(NCLMetaField)</c>, which is how BC
    /// records a JIT load: <c>GetFieldValue</c> reads a field the load set omits and calls this,
    /// after which the value is genuinely in hand. So the field becomes materialised, exactly as
    /// a fetch materialises one — corpus 60775 <c>PartialLoad_ReadOmittedField_JitLoadsRealValue</c>
    /// asserts the field reports loaded afterwards.
    /// </summary>
    public static void RecordImplementation_AddLoadField_Prologue(object self, object field)
    {
        if (field is null) return;
        EnsurePartialLoadReflection(self.GetType());
        _fetchedFields.GetOrCreateValue(self).Loaded.Add(FieldNoOf(field));
    }

    private static int FieldNoOf(object? field)
        => field is null ? 0 : (int)_mfFieldNo!.GetValue(field)!;

    /// <summary>
    /// A public/non-public instance property, or a <see cref="BcShapeGapException"/> naming it.
    /// Absence refuses rather than answering: a fold that recorded nothing would report every
    /// field unloaded, which is the silent default loud-failures.md forbids.
    /// </summary>
    private static PropertyInfo RequiredProperty(Type declaring, string name, string detail)
        => declaring.GetProperty(
               name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
           ?? throw new BcShapeGapException(
               PartialLoadSurface, $"{declaring.Name}.{name}",
               $"property not found — {detail}");

    private static void EnsurePartialLoadReflection(Type recordImplementation)
    {
        if (_riMutableRecordBuffer != null && _riIsFieldSelectedForLoad != null
            && _riMetaTable != null && _mfFieldNo != null) return;
        lock (_partialLoadReflLock)
        {
            if (_riMutableRecordBuffer != null && _riIsFieldSelectedForLoad != null
            && _riMetaTable != null && _mfFieldNo != null) return;

            // BC declares two overloads — (Int32 fieldNo) and (NCLMetaField field). The
            // rewritten body hands us NCLMetaField instances, so pin that signature rather
            // than resolve by name and get refused for ambiguity.
            var metaFieldType = recordImplementation.Assembly.GetType(
                "Microsoft.Dynamics.Nav.Runtime.NCLMetaField")
                ?? throw new BcShapeGapException(
                    PartialLoadSurface, "Microsoft.Dynamics.Nav.Runtime.NCLMetaField",
                    "type not found — AreFieldsLoaded cannot name the overload it needs");

            var isSelected = BcShape.RequiredMethod(
                recordImplementation, "IsFieldSelectedForLoad",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                PartialLoadSurface, "RecordImplementation.IsFieldSelectedForLoad",
                "AreFieldsLoaded cannot report which fields the load set holds",
                new[] { metaFieldType });

            _riMutableRecordBuffer = BcShape.RequiredField(
                recordImplementation, "mutableRecordBuffer", PartialLoadSurface,
                "AreFieldsLoaded cannot tell a fetched record from one never read, and would "
                + "report every field as loaded on an unfetched buffer");

            // The metadata walk that turns "the requested set at fetch time" into a field-number
            // set. Every one of these refuses rather than answering on absence: a fold that
            // silently recorded nothing would make every field read unloaded, which is the
            // silent default loud-failures.md forbids.
            var metaTableField = BcShape.RequiredField(
                recordImplementation, "metaTable", PartialLoadSurface,
                "AreFieldsLoaded cannot enumerate the table's fields, so it cannot record "
                + "which ones a fetch materialised");
            var metaTableType = metaTableField.FieldType;

            var fieldCount = RequiredProperty(
                metaTableType, "FieldCount",
                "AreFieldsLoaded cannot bound the field walk that records a fetch");
            var getByIndex = BcShape.RequiredMethod(
                metaTableType, "GetFieldByIndex",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                PartialLoadSurface, $"{metaTableType.Name}.GetFieldByIndex",
                "AreFieldsLoaded cannot walk the table's fields to record a fetch",
                new[] { typeof(int) });
            var fieldNo = RequiredProperty(
                metaFieldType, "FieldNo",
                "AreFieldsLoaded cannot key the fetched-field set");

            _riMetaTable = metaTableField;
            _mtFieldCount = fieldCount;
            _mtGetFieldByIndex = getByIndex;
            _mfFieldNo = fieldNo;
            _riIsFieldSelectedForLoad = isSelected;
        }
    }
}
