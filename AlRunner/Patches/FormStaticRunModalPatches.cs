// FormStaticRunModalPatches — NCLMetaForm.CreateObjectInstance(NavRecord), the
// construction path the STATIC Page.RunModal(id[, Record]) / Page.RunModal(id, isLookup, …)
// overloads reach (and, transitively, any Base App code that calls through them, e.g.
// Codeunit 700 "Page Management".PageRunModal/PageRun).
//
// Issue #1897: BC's real NCLMetaForm.CreateObjectInstance(NavRecord) body is
//
//     Delegate applicationObjectConstructor = base.ApplicationObjectConstructor;
//     NavForm navForm = ((CreateNavForm)applicationObjectConstructor)(
//         NavCurrentThread.Session.Company.SharedObjects, record, base.StaticMetadata);
//     ...
//
// and NCLMetaApplicationObject.ApplicationObjectConstructor is forced null for every
// object type on the runner's skeleton (see RecordPatches.CreateObjectInstance.cs /
// XmlPortPatches.cs / CodeunitPatches.cs's NCLMetaReport/NCLMetaQuery siblings) — so this
// NREs on the invoke. Even with a non-null delegate, NavCurrentThread.Session.Company is
// also null on the skeleton (see the RequestPageBase ctor rewrite in NclCecilRewrite.cs),
// so the body would NRE a second way.
//
// The AL-variable form (`P: Page "X"; P.SetRecord(Rec); P.RunModal();`) never reaches this
// method — NavFormHandle.CreateTarget (CodeunitPatches.NavFormHandle_CreateTarget) already
// has its own, working per-type construction path for that case. The STATIC forms reach
// NCLMetaForm.CreateObjectInstance directly from NavForm.RunModalAsync(bool, bool, int,
// NavRecord, int) — `NCLMetadata.GetMetaFormById(formId, true).CreateObjectInstance(record)`
// — so they need the identical construction, just entered from a different caller and
// without a NavFormHandle already in hand to serve as the parent ITreeObject.
//
// Only NCLMetaForm.CreateObjectInstance(NavRecord) is Cecil-rewritten (see
// NclCecilRewrite.cs). The 0-arg overload chains to it (`CreateObjectInstance((NavRecord)
// null)`), so it is covered automatically; the string-personalizationId overload is
// unrelated (Session-level personalization, out of scope here) and left untouched.
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner;

public static partial class BcRuntime
{
    /// <summary>
    /// Replacement for <c>NCLMetaForm.CreateObjectInstance(NavRecord)</c>.
    ///
    /// Constructs the AL-emitted <c>Page{id}</c> instance the same way
    /// <c>CodeunitPatches.NavFormHandle_CreateTarget</c> does for the AL-variable path —
    /// through the (ITreeObject, NavRecord) ctor when the page's source table is
    /// resolvable, falling back to the (ITreeObject) ctor otherwise — except the record
    /// this method receives (if any) is the one the caller already supplied, so it is
    /// bound directly instead of a freshly-built blank one. There is no NavFormHandle to
    /// serve as the parent ITreeObject here (unlike the page-variable path — construction
    /// happens BEFORE NavForm.RunModalAsync wraps the result in one), so the skeleton
    /// session is used instead — the same "no handle for a static-by-id call" approach
    /// NavReportSync.CreateReportForRequestPage already takes for the static Report
    /// RunRequestPage overloads.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Microsoft.Dynamics.Nav.Runtime.NavForm NCLMetaForm_CreateObjectInstance(
        object self, NavRecord? record)
    {
        int id = ReadMetaObjectNumber(self);
        if (id == 0)
            throw new InvalidOperationException(
                "NCLMetaForm.CreateObjectInstance: could not read the page's object id from " +
                "its metadata — constructing the wrong page would silently open the wrong " +
                "list/card.");

        return (Microsoft.Dynamics.Nav.Runtime.NavForm)ConstructFormForStaticEntry(id, record);
    }

    private static object ConstructFormForStaticEntry(int id, NavRecord? record)
    {
        var formType = _formTypeCache.GetOrAdd(id, FindFormType);
        if (formType == null)
            throw new InvalidOperationException(
                $"Page{id} is not present in the test assembly or any loaded dependency.");

        var parent = SkeletonSession as Microsoft.Dynamics.Nav.Runtime.ITreeObject
            ?? throw new InvalidOperationException(
                "NCLMetaForm.CreateObjectInstance: the skeleton session is not initialized yet.");

        var ctors = formType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var twoArgCtor = ctors.FirstOrDefault(c => c.GetParameters().Length == 2
            && typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject).IsAssignableFrom(c.GetParameters()[0].ParameterType)
            && typeof(NavRecord).IsAssignableFrom(c.GetParameters()[1].ParameterType));

        var boundRecord = record;
        if (twoArgCtor != null)
        {
            if (boundRecord == null)
            {
                // The 0-arg overload (CreateObjectInstance() → CreateObjectInstance(null))
                // has no caller-supplied record. Build a blank one, same as the
                // page-variable path (NavFormHandle_CreateTarget), so a page whose
                // OnInit/OnOpenPage reads Rec before the caller ever calls SetSourceTable
                // does not NRE.
                var tableId = RecordPatches.ResolveSourceTableIdForAnyPage(id);
                if (tableId != 0)
                {
                    var isTemporary = RecordPatches.ResolveSourceTableTemporaryForAnyPage(id);
                    boundRecord = TestPageFactory.TryBuildBlankRecord(parent, tableId, isTemporary, out _);
                }
            }

            if (boundRecord != null)
            {
                var instance = twoArgCtor.Invoke(new object?[] { parent, boundRecord });
                BindPageSourceObjectId(instance, boundRecord.TableID);

                // NavForm.SetSourceTable(record, clone: false) is BC's own binding step —
                // reused rather than reimplemented, same rationale as
                // NavFormHandle_CreateTarget. NavForm.RunModalAsync(record, fieldNo) (the
                // real, unmodified body this construction feeds into) calls
                // SetSourceTable(record, clone: true, ...) again itself once
                // CreateObjectInstance returns, so this first bind only has to make the
                // freshly-constructed instance safe to touch before that point.
                var setSourceTable = instance.GetType().GetMethod("SetSourceTable",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: new[] { typeof(NavRecord), typeof(bool) }, modifiers: null);
                try { setSourceTable?.Invoke(instance, new object?[] { boundRecord, false }); }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    Console.Error.WriteLine(
                        $"[BcRuntime] Page{id}: SetSourceTable failed after binding SourceTable "
                        + $"{boundRecord.TableID} ({tie.InnerException.GetType().Name}: {tie.InnerException.Message}); "
                        + "Rec stays unbound, as before this fix.");
                }
                return instance;
            }
        }

        var oneArgCtor = ctors.FirstOrDefault(c => c.GetParameters().Length == 1 &&
                typeof(Microsoft.Dynamics.Nav.Runtime.ITreeObject)
                    .IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (oneArgCtor == null)
            throw new InvalidOperationException(
                $"Page{id} has no single-arg ITreeObject constructor");
        return oneArgCtor.Invoke(new object[] { parent });
    }
}
