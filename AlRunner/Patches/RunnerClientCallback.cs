// RunnerClientCallback — stands in for NavSession.ClientCallbackOverride so
// NavDialog.ALMessage's `session.ClientCallbackOrNull?.DialogMessage(...)` has somewhere
// real to land during `server execute` (issue #2117).
//
// NavSession.ClientCallbackOverride is a plain, public, settable property BC itself
// ships on NavSession — decompiled:
//     public IClientCallback ClientCallbackOverride { get; set; }
//     public IClientCallback ClientCallbackOrNull =>
//         ClientCallbackOverride ?? serviceConnection?.ClientCallback;
//     public IClientCallback ClientCallback =>
//         ClientCallbackOrNull ?? throw new NavNCLCallbackNotAllowedException();
// Installing an instance here is exactly the extension point BC provides for a
// process with no real client connection — no Cecil rewrite of NavDialog or any other
// Ncl.dll business logic is needed.
//
// WHY THIS IS SAFE TO INSTALL SESSION-WIDE, NOT JUST FOR ALMessage
//   Installing ClientCallbackOverride makes ClientCallbackOrNull non-null for the WHOLE
//   session, not only inside ALMessage — so every OTHER Ncl.dll call site that reads
//   ClientCallbackOrNull/ClientCallback is affected too. Verified by decompiling
//   Microsoft.Dynamics.Nav.Ncl.dll 28.1 in full (ilspycmd -il on the whole assembly,
//   every `callvirt ... IClientCallback::<Member>` site traced back to its nearest
//   preceding getter) rather than assumed. Two shapes exist, and they need DIFFERENT
//   answers here:
//
//   (A) Reached via the THROWING `session.ClientCallback` property (or a decorator
//       whose own ctor already went through it, e.g. DataItemIteratorClientCallback /
//       QueryClientCallback) — DialogConfirm, DialogSelectionMenu, DialogOpen/Update/
//       Close, ProcessServerRequests, ImportDataAction/ExportDataAction,
//       DownloadFileAction/UploadFileAction/ViewFileAction, FormRun/FormRunModal/
//       FormClose/FormActivate, CreateDotNetHandle/GetDotNetObject/
//       InvokeAutomationMethod/DisposeAutomationObject, RequestCredentials,
//       ClearClientMetadataCache, DialogHyperlink, InvokeTaskPaneAction,
//       DataSetPageReady, VerifyCallbackAllowed. On the runner (no client, no
//       override) that property THROWS before any of these are ever reached — with
//       this override installed it returns US instead, so these members must
//       reproduce that EXACT exception rather than silently answering something.
//       They throw NavNCLCallbackNotAllowedException — the identical type+message
//       `ClientCallback`'s own getter raises — so this class changes NOTHING
//       observable for any of them.
//
//   (B) Reached via a NULL-CHECK on `ClientCallbackOrNull` (never the throwing
//       property) — WorkDateChanged (NavSession.set_WorkDate — AL `WorkDate := ...`,
//       extremely common), FeedbackRequested (Base App's in-app-feedback codeunit
//       2000000021), SendSessionUpdateRequest (NavSessionSettings.SaveSessionSettings),
//       CompanyInformationChanged (SystemTableTriggers reacting to a Company
//       display-name write), TokenChangedNotification (Agents.
//       AgentTaskTableChangeMonitor's background poll). BC's own real behaviour for
//       ALL of these, with no client, is "do nothing" — a pure best-effort
//       client-UI-refresh signal with nobody to receive it, same shape as the
//       existing Batch-5 no-op headless progress dialog
//       (ALSystemOperatingSystem's neighbouring NavDialog.ALOpenAsync/ALUpdateAsync/
//       ALClose — see NclCecilRewrite.cs). THROWING here instead — which an earlier
//       revision of this file did — would be a REAL regression on the `execute` path:
//       any AL codeunit that sets `WorkDate` (near-universal in BC test setup) would
//       flip from "ran fine" to "throws", discovered only by a reviewer decompiling
//       Ncl.dll, not stated here. So these five reproduce the EXACT pre-existing
//       silent-no-client answer: do nothing, return a default where one is needed.
//       This is not the silent-fake loud-failures.md forbids — it is the literal,
//       unconditional real-BC answer for "no client is connected" on a
//       notification nobody exists to receive; DialogMessage (case (D) below) is
//       the ONE such site this issue changes on purpose, and it changes it because
//       the caller of `execute` explicitly wants that text, unlike these five.
//       NavNotification.ALSend/ALRecall (SendNotification/SendGlobalNotification's
//       PUBLIC AL-facing entry points) are separately Cecil-owned (NclCecilRewrite.cs,
//       Batch 8) — their REAL bodies with this same null-check shape are already dead
//       code on the runner regardless of what this class does, so those two members
//       are implemented here only to complete the interface, never actually reached.
//
//   (C) The `CallbackAllowed` getter (`ClientCallbackOrNull?.IsCallbackAllowed ??
//       false`) — deliberately answers `true`: the runner IS a UI-capable session
//       (same reasoning as ALSystemOperatingSystem.get_ALGuiAllowed's own Cecil
//       rewrite), not a silent default.
//
//   (D) DialogMessage — the fix. See below.
//
// [Test]-PROCEDURE BEHAVIOUR IS UNCHANGED
//   Message() called from within a [Test] procedure never reaches this class at all:
//   NavTestExecution.TestHandleMessage resolves (or raises "Unhandled UI") via
//   FindHandler while `executingTestMethod` is set, strictly BEFORE ALMessage ever
//   consults ClientCallbackOrNull. See AlMessageCapture.cs's header for the full call
//   chain and ServerExecuteMessagesTests for the regression guard.
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner.Patches;

public sealed class RunnerClientCallback : IClientCallback
{
    // (C) — see file header. Nothing on the Message()/Confirm()/StrMenu() path
    // consults this getter today; it is answered truthfully rather than defaulted to
    // false in case something else ever does.
    public bool IsCallbackAllowed => true;

    /// <summary>(D) — the fix: capture the message and the AL statement that produced
    /// it instead of the real BC "no client" answer (silently doing nothing). Reads
    /// AlCurrentStatement, NOT NavSession.CurrentMethodScope — see that class's doc
    /// comment for why the latter does not track a trigger scope like OnRun.</summary>
    public void DialogMessage(string message, Guid automationId)
    {
        var (scope, statementId) = Infrastructure.AlCurrentStatement.Current;
        string scopeName;
        if (scope != null)
        {
            Infrastructure.AlNavNameReflection.EnsureInit();
            scopeName = Infrastructure.AlNavNameReflection.GetAlName(scope.GetType()) ?? scope.GetType().Name;
        }
        else
        {
            scopeName = "?";
        }
        Infrastructure.AlMessageCapture.Record(message, scopeName, statementId);
    }

    // ── (B) — reached only via a null-check today; a no-op here is the EXACT real-BC
    // "no client connected" answer, not a silent fake (see file header, case (B)).
    public void WorkDateChanged(DateTime workDate) { }
    public Task FeedbackRequested(FeedbackRequest feedbackRequest) => Task.CompletedTask;
    public void SendSessionUpdateRequest(SessionSettingsInfo sessionSettingsInfo) { }
    public void CompanyInformationChanged(CompanyInformationChanges companyInformationChanges) { }
    public void TokenChangedNotification() { }
    // NavNotification.ALSend/ALRecall (the only AL-facing entry points to these two)
    // are Cecil-owned (NclCecilRewrite.cs, Batch 8) — dead code regardless of this
    // implementation. No-op for the same reason as the rest of case (B), not reached.
    public void SendNotification(NotificationInfo notification) { }
    public void SendGlobalNotification(NotificationInfo notification) { }

    // ── (A) — reproduce BC's own "no client connected" answer exactly (see file
    // header): the identical exception NavSession.ClientCallback's throwing getter
    // raises today, for callers this override's mere existence would otherwise divert
    // around that getter.
    public void DialogHyperlink(string hyperlink, Guid automationId) => throw NotAllowed();
    public bool DialogConfirm(string message, bool defaultValue, Guid automationId) => throw NotAllowed();
    public void ProcessServerRequests() => throw NotAllowed();
    public void DialogOpen(Guid handle, Guid automationId, DialogCancellationBehavior cancellationBehavior, string format, object[] parameters) => throw NotAllowed();
    public void DialogUpdate(Guid dialogHandle, DialogCancellationBehavior cancellationBehavior, object[] parameters) => throw NotAllowed();
    public void DialogClose(Guid dialogHandle) => throw NotAllowed();
    public void ThrowIfDialogCanceled() => throw NotAllowed();
    public int DialogSelectionMenu(string[] options, int defaultSelection, string instruction, Guid automationId) => throw NotAllowed();
    public bool DownloadFileAction(Stream stream, bool displayDialog, string title, string initialFolder, string typeFilter, ref string fileName, Guid automationId) => throw NotAllowed();
    public FileBufferedStream UploadFileAction(bool displayDialog, string title, string initialFolder, string typeFilter, ref string fileName, Guid automationId) => throw NotAllowed();
    public bool ViewFileAction(Stream stream, string fileName, bool allowDownloadAndPrint) => throw NotAllowed();
    public bool ExportDataAction(Stream stream, ref string fileName, string dialogTitle, bool showDialog) => throw NotAllowed();
    public bool ImportDataAction(ref string fileName, string dialogTitle, bool showDialog) => throw NotAllowed();
    public FormResult FormRunModal(NavForm form, NavFormRuntimeParameters parameters) => throw NotAllowed();
    public void FormRun(NavForm form, NavFormRuntimeParameters parameters) => throw NotAllowed();
    public void FormClose(NavForm form) => throw NotAllowed();
    public void FormActivate(NavForm form, bool refresh) => throw NotAllowed();
    public bool DataSetPageReady(DataSetRequest request) => throw NotAllowed();
    public NavAutomationHandle CreateDotNetHandle(string assemblyFullName, string typeName, Guid formHandle, string varName, bool createInstance, params object[] arguments) => throw NotAllowed();
    public NavAutomationHandle GetDotNetObject(Guid formHandle, int controlId) => throw NotAllowed();
    public UserNamePasswordCredentials RequestCredentials(UserNamePasswordRequestOptions requestOptions) => throw NotAllowed();
    public void DisposeAutomationObject(int handle, bool suppressDispose) => throw NotAllowed();
    public object InvokeAutomationMethod(InvokeAutomationMethodRequest<object> request) => throw NotAllowed();
    public void ClearClientMetadataCache() => throw NotAllowed();
    public void VerifyCallbackAllowed(NavApplicationObjectBase applicationObject) => throw NotAllowed();
    public Task SendPageBackgroundTaskCompletedNotificationAsync(Guid formHandle, int taskId, string clientActivityId) => throw NotAllowed();
    public Task InvokeTaskPaneAction(InvokeTaskPaneActionArguments arguments) => throw NotAllowed();

    private static NavNCLCallbackNotAllowedException NotAllowed() => new();
}
