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
// The one divergence this leaves — narrowing the load set AFTER a fetch and asking before
// re-fetching — is written up, with the BC side still awaiting a service-tier verdict, at
// docs/limitations.md#are-fields-loaded-narrow-after-fetch. Alternatives weighed: PR for #3358.
// Reference: upstream corpus codeunit 60775 "Test Record Partial Load".

using System.Collections;
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private const string PartialLoadSurface = "Record.AreFieldsLoaded (partial records)";

    private static FieldInfo? _riMutableRecordBuffer;
    private static MethodInfo? _riIsFieldSelectedForLoad;
    private static readonly object _partialLoadReflLock = new();

    /// <summary>
    /// Cecil replacement for <c>RecordImplementation.AreFieldsLoaded(IEnumerable&lt;NCLMetaField&gt;)</c>.
    /// False when the record has never been fetched — BC's own <c>mutableRecordBuffer == null</c>
    /// guard, which is why corpus test <c>PartialLoad_AreFieldsLoaded_BeforeFetch_ReturnsFalse</c>
    /// passes today. Otherwise answers from <c>TableState.FieldLoadInfo</c>.
    /// </summary>
    public static bool RecordImplementation_AreFieldsLoaded(object self, object fields)
    {
        EnsurePartialLoadReflection(self.GetType());

        // "Loaded" is a statement about a FETCHED buffer, not about a field list: before any
        // Get/Find nothing is loaded, not even the primary key.
        if (_riMutableRecordBuffer!.GetValue(self) is null) return false;

        // An EMPTY field list is "all loaded", which the loop below answers by not iterating —
        // BC's own body does the same. A NULL one is refused by RequiredEnumerable rather than
        // answered: today NavRecord.AreFieldsLoaded's ArgumentNullException.ThrowIfNull upstream
        // makes it unreachable, but that is a property of BC's callers, not of this code,
        // so answering the success value would be the silent default loud-failures.md forbids
        // (#3372; the refusal is executed on a null in PartialLoadFieldListRefusalTests).
        var enumerable = BcShape.RequiredEnumerable(
            fields, "RecordImplementation.AreFieldsLoaded(fields)", PartialLoadSurface,
            "the runner cannot tell which fields were asked about");

        var oneField = new object?[1];
        foreach (var field in enumerable)
        {
            oneField[0] = field;
            if (_riIsFieldSelectedForLoad!.Invoke(self, oneField) is not true) return false;
        }
        return true;
    }

    private static void EnsurePartialLoadReflection(Type recordImplementation)
    {
        if (_riMutableRecordBuffer != null && _riIsFieldSelectedForLoad != null) return;
        lock (_partialLoadReflLock)
        {
            if (_riMutableRecordBuffer != null && _riIsFieldSelectedForLoad != null) return;

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
            _riIsFieldSelectedForLoad = isSelected;
        }
    }
}
