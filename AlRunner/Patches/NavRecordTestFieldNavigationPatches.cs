// NavRecordTestFieldNavigationPatches — issue #1938 (regression from #1926).
//
// Real BC's NavRecord.TestFieldNotBlank / TestFieldError (both AL-triggered — e.g. a plain
// `SalesSetup.TestField("Order Nos.")`) call the private helper
// TryAddTestFieldAction(NCLMetaField) BEFORE throwing the real NavTestFieldException, to
// optionally attach a "navigate to related record" ErrorInfo action pointing at the table's
// LookupPageId/DrillDownPageId. TryAddTestFieldAction in turn calls the private static
// GetPageToOpen(NCLMetaTable), which may resolve the page's CardFormID via
// NavGlobal.MetadataProvider.GetPageDefinition(pageId), and then (back in
// TryAddTestFieldAction) NavGlobal.NCLMetadata.GetMetaFormById(pageId, requireCompiled: true).
//
// On real BC both calls always succeed for a valid page id — Base App/System App/every
// installed extension is always FULLY compiled together, so "the table's declared lookup page
// doesn't exist" never happens there. The runner does not eagerly build NCLMetaForm /
// PageDefinition metadata for every Base App page a table's LookupPageId might reference —
// only pages the test bundle itself compiled (RecordPatches.NclMetaFormReportBuilder, gated on
// _parsedPages/_parsedPageExtensions) or explicitly probed (RecordPatches.RealPageMetadata).
// #1926 started resolving NCLMetaTable.LookupFormId faithfully (#1918), which means this
// convenience-action code path is now reached far more often, and for a Base App page the
// runner never loaded it throws NavMetadataNotFoundException — a real BC exception type, but
// one this specific call site (unlike its sibling NCLMetadata.TryGetMetaApplicationObject)
// does not catch. That exception then propagates OUT of TryAddTestFieldAction and pre-empts
// the `throw NavTestFieldException.CreateNonblank(...)` statement it was only supposed to be
// an ARGUMENT to — so BC's own AL-exception remap (ALMethodScope.ALException) sees the
// metadata failure instead of the TestField failure, and the test sees "You tried to invoke
// the Page object with the ID …" instead of the real "… must have a value …" message.
//
// The navigate action is a pure UI convenience: it attaches a clickable "Show <page>" action
// to the ErrorInfo for rich clients. It never changes NavTestFieldException's own message text
// (built separately, from the field/table captions and the primary key), so a headless runner
// — which has no UI to click that action in anyway (page rendering is out of scope, see
// docs/scope.md) — loses nothing observable by treating "can't resolve the navigate target"
// the same way the ORIGINAL BC method already treats "wrong table" / "wrong page type" / "no
// permission": silently skip the action, return null, and let the real error surface.
//
// Both guarded call sites are patched (GetPageToOpen for the CardFormID follow-through,
// TryAddTestFieldAction for the GetMetaFormById lookup) — guarding only one still lets the
// other throw and reproduce the bug, since either can independently hit an unbuilt page.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static MethodInfo? _mNavRecordCreateErrorInfoData;
    private static MethodInfo? _mNavUserPermissionsGetEffectivePermissionForObject;

    /// <summary>
    /// Replacement for the private static <c>NavRecord.GetPageToOpen(NCLMetaTable)</c> — see
    /// file header. Guards ONLY the optional CardFormID follow-through; a table's plain
    /// LookupFormId/DrillDownPageId is always returned even when that follow-through can't be
    /// resolved on this run.
    /// </summary>
    public static int NavRecord_GetPageToOpen(NCLMetaTable metaTable)
    {
        int num = metaTable.LookupFormId > 0 ? metaTable.LookupFormId : metaTable.DrillDownPageId;
        if (num > 0)
        {
            try
            {
                int cardFormId = NavGlobal.MetadataProvider.GetPageDefinition(num).Properties.CardFormID;
                if (cardFormId > 0) num = cardFormId;
            }
            catch (NavMetadataNotFoundException)
            {
                // Runner gap (#1938): the runner has no NCLMetaForm/PageDefinition for
                // page `num` — it was never compiled by the test bundle and is not one of
                // the pages RealPageMetadata probes. Degrade exactly like the
                // cardFormId <= 0 case already does: keep the plain page id.
            }
        }
        return num;
    }

    /// <summary>
    /// Replacement for the private instance <c>NavRecord.TryAddTestFieldAction(NCLMetaField)</c>
    /// — see file header. Reimplements BC's own guard chain verbatim (temporary/blank record,
    /// no page to open, already on that page, page not resolvable, wrong source table, wrong
    /// page type, no read/execute permission) and additionally treats a metadata-resolution
    /// failure for the navigate target as one more reason to skip the action, instead of
    /// letting it propagate and replace the caller's real TestField error.
    ///
    /// The one difference from BC's real body: the platform diagnostics trace tag
    /// (`Session.Diagnostics.SendTraceTag(...)`) is dropped. It is pure telemetry with no AL-
    /// or test-observable effect, and `Session.Diagnostics` is null on the runner's skeleton
    /// session (see HelperShims.cs's NavNotification_ALSend for the same, already-accepted gap
    /// elsewhere) — keeping the call would trade one crash for another with nothing to show
    /// for it.
    /// </summary>
    public static ErrorInfoData? NavRecord_TryAddTestFieldAction(NavRecord self, NCLMetaField metaField)
    {
        var recordId = self.ALRecordId;
        if (recordId == null || recordId.IsZeroOrEmpty || self.IsTemporary)
            return null;

        int pageToOpen;
        try
        {
            pageToOpen = NavRecord_GetPageToOpen(self.MetaTable);
        }
        catch (NavMetadataNotFoundException)
        {
            return null;
        }
        if (pageToOpen == 0)
            return null;

        if (self.Session.CurrentMethodScope.TopLevelApplicationObject is not NavForm navForm)
            return null;
        if (navForm.FormId == pageToOpen)
            return null;

        NCLMetaForm? metaFormById;
        try
        {
            metaFormById = NavGlobal.NCLMetadata.GetMetaFormById(pageToOpen, requireCompiled: true);
        }
        catch (NavMetadataNotFoundException)
        {
            // Runner gap (#1938) — see file header. No navigate target available; skip the
            // action exactly like every other guard condition in this method already does.
            return null;
        }
        if (metaFormById == null || metaFormById.SourceTableTemporary
            || metaFormById.SourceTable != recordId.TableNo
            || (metaFormById.PageType != PageType.Card && metaFormById.PageType != PageType.Document))
            return null;

        EnsureTestFieldActionReflection();
        if (_mNavUserPermissionsGetEffectivePermissionForObject == null || _mNavRecordCreateErrorInfoData == null)
            return null;

        var perm = (PermissionMask)_mNavUserPermissionsGetEffectivePermissionForObject.Invoke(
            self.Session.Permissions, new object[] { self.Session.CompanyName, metaFormById.ApplicationObjectId })!;
        if (!perm.HasFlag(PermissionMask.Read) && !perm.HasFlag(PermissionMask.Execute))
            return null;

        return (ErrorInfoData?)_mNavRecordCreateErrorInfoData.Invoke(self, new object[] { metaField, metaFormById });
    }

    private static void EnsureTestFieldActionReflection()
    {
        _mNavUserPermissionsGetEffectivePermissionForObject ??= typeof(NavUserPermissions).GetMethod(
            "GetEffectivePermissionForObject", BindingFlags.NonPublic | BindingFlags.Instance);
        _mNavRecordCreateErrorInfoData ??= typeof(NavRecord).GetMethod(
            "CreateErrorInfoData", BindingFlags.NonPublic | BindingFlags.Instance);
    }
}
