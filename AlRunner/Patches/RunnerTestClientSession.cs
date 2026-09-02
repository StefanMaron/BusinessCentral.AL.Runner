// RunnerTestClientSession — the ITestClientSession BC's test harness expects a client to
// provide.
//
// WHERE IT IS USED
//   NavTestExecution.TestHandleModalForm pushes a delegate that builds the NavTestPage the
//   AL [ModalPageHandler] receives:
//       new NavTestPage(..., TestClientProxy<ITestPage>.Proxy(
//           testClientSession.GetPage(runRequest.Data.FormHandle)))
//   Real BC gets that session by Assembly.Load-ing the TestPageClient, which does not exist
//   in the runner — so testClientSession stayed null and the delegate NRE'd the moment the
//   dispatch reached it.
//
// WHAT GetPage RETURNS
//   The page BC already opened. The form is live and registered under the handle, so the
//   page under test is that form's own record and control tree — not a second instance
//   built alongside it. Handing back a fresh page would let the handler drive one object
//   while the AL that called RunModal observes another.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

public sealed class RunnerTestClientSession : ITestClientSession
{
    private readonly object _session;

    public RunnerTestClientSession(object session) => _session = session;

    /// <summary>
    /// The live page for a form BC registered under <paramref name="formHandle"/>.
    /// </summary>
    public ITestPage GetPage(Guid formHandle, bool forClose = false)
    {
        var form = RegisteredForm(formHandle)
            ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                $"TestPage modal page (handle {formHandle})",
                "testpage-modal — no form is registered under this handle, so the runner cannot "
                + "hand the [ModalPageHandler] the page it is being asked to drive. "
                + "See docs/scope.md");

        // A REQUEST page is not a page over a record — it has no source table at all, so the
        // record check below would refuse it by name for something that is simply not part of
        // its shape. It gets the request-page surface instead: one filter group per report
        // data item, plus the built-in OK/Cancel. The binding is registered by whoever ran
        // the request page (NavReportSync), which is the only place that knows which report
        // this form belongs to.
        if (RequestPageTestPage.TryGetFor(form) is { } requestPage)
            return requestPage;

        var pageId = PageIdOf(form);

        // A page with no SourceTable is ordinary, legal AL — the StandardDialog shape, whose
        // controls are bound to page globals rather than a record (issue #2007). It used to be
        // refused right here with "the modal form has no source table bound", which is true and
        // beside the point: the handler never needed a record in the first place, only the
        // control tree. LiveNavTestPage accepts a null record and answers every Rec-dependent
        // member (row navigation, filtering, Insert/Modify, Rec-bound field access) with a loud,
        // named refusal ONLY if the AL under test actually reaches for one — see
        // LiveNavTestPage.RequireRecord. A page-variable-bound field, which is the only shape a
        // no-source-table page's controls can be, resolves through RunnerPageInstance's own
        // source-expression table and never touches the record at all.
        var record = ReadProperty(form, "SourceTable") as NavRecord;

        var live = new AlRunner.LiveNavTestPage(
            record,
            RecordPatches.GetPageControlFieldMap(pageId),
            RecordPatches.GetInsertAllowedForPage(pageId),
            RunnerPageInstance.Adopt(form, pageId),
            _session,
            pageId);

        // This wrapper is built AFTER the underlying form already opened — the code that
        // positions a TestPage the AL test opened itself (RunnerTestPageState.MarkOpened)
        // never runs on this path, so without this the page a [PageHandler]/[ModalPageHandler]
        // receives sits on no row at all, even when its view has one. A real client positions
        // on the first row (or the implicit new-row line, if the view has none — see
        // LiveNavTestPage.MoveFirst) the moment a page is shown, before any AL handler code
        // runs. Issue #2392: Base App's ApprovalCommentsHandler writes a field straight after
        // being handed the page — no New()/First() of its own — which needs exactly this.
        //
        // GUARDED on IsUnpositioned: `record` is the underlying form's OWN record, and
        // `Page.SetRecord(specific row); Page.RunModal();` — corpus CU60848
        // RunModal_OpensOnTheRecordSetByTheCaller — already positions it before this method
        // ever runs. Calling MoveFirst() unconditionally re-queried the table from scratch and
        // silently overwrote the caller's chosen row with whatever sorts first, which is
        // exactly what that corpus test exists to catch (measured: it did, on first push of
        // this fix). Only reposition a record nothing has touched yet.
        if (record != null && IsUnpositioned(record)) live.MoveFirst();

        return live;
    }

    /// <summary>
    /// Whether <paramref name="record"/> is still at its Init() default — every primary-key
    /// field zero/blank — as opposed to already holding a specific row's values (via
    /// Page.SetRecord, or any other caller-side positioning that does not go through
    /// LiveNavTestPage.MoveFirst itself).
    /// </summary>
    internal static bool IsUnpositioned(NavRecord record)
    {
        var primaryKey = record.MetaTable?.PrimaryKey;
        if (primaryKey == null) return true;
        for (var i = 0; i < primaryKey.KeyFieldCount; i++)
        {
            var fieldNo = primaryKey.KeyFieldsList[i].FieldNo;
            if (record.MetaTable!.TryGetFieldByNo(fieldNo, out var field)
                && !record.GetFieldValue(field).IsZeroOrEmpty)
                return false;
        }
        return true;
    }

    private object? RegisteredForm(Guid handle)
    {
        var company = ReadProperty(_session, "Company");
        var getRegisteredForm = company?.GetType().GetMethod("GetRegisteredForm",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(Guid) }, modifiers: null);
        try { return getRegisteredForm?.Invoke(company, new object[] { handle }); }
        catch (TargetInvocationException) { return null; }
    }

    private static int PageIdOf(object form)
        => ReadProperty(form, "ObjectId") is { } objectId
           && ReadProperty(objectId, "ObjectNumber") is int number
            ? number
            : 0;

    private static object? ReadProperty(object target, string name)
        => target.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(target);

    // ── The rest of the client-session surface ────────────────────────────────
    // Nothing in the runner's dispatch path reaches these. They refuse by name rather than
    // answering with a default, which would make a test that used one silently wrong.

    public ITestPage CreatePage(int id, ViewMode mode) =>
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"ITestClientSession.CreatePage({id})",
            "testpage-modal — the runner builds a TestPage through NavTestPageHandle.CreateTarget, "
            + "not through the client session (NavTestPage.Open is rewritten to skip this). "
            + "See docs/scope.md");

    public bool ActivatePage(Guid formHandle, bool refresh) =>
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "ITestClientSession.ActivatePage",
            "testpage-modal — page activation is a client-window concept with no in-process "
            + "equivalent here. See docs/scope.md");

    /// <summary>Always 0: the runner keeps no window list — BC tracks open forms itself,
    /// on NavCompany.registeredForms, which is what every code path here consults.</summary>
    public int OpenFormsCount => 0;

    public bool SaveDataOnDispose { get; set; }

    public void Dispose() { }
}
