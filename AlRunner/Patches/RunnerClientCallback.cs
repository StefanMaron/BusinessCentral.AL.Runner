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
// SCOPE OF THE CHANGE
//   DialogMessage is the ONLY member whose answer differs from what real BC would do
//   with no client connected. Every other member reproduces
//   NavNCLCallbackNotAllowedException exactly — the SAME exception
//   `NavSession.ClientCallback`'s throwing getter raises when ClientCallbackOrNull is
//   null. That matters because installing this override makes ClientCallbackOrNull
//   non-null for the WHOLE session, so BC's own ALConfirm/ALStrMenu bodies (which read
//   the throwing `session.ClientCallback`, not `ClientCallbackOrNull`) stop throwing at
//   the property access and instead call DialogConfirm/DialogSelectionMenu on US.
//   Reproducing the exact same exception type+message there means Confirm()/StrMenu()
//   on the `execute` path keep their EXISTING, already-correct, already-loud behaviour
//   byte-for-byte — this override changes NOTHING observable except Message().
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
    // The runner IS a UI-capable session for the same reason
    // ALSystemOperatingSystem.get_ALGuiAllowed is rewritten to true (see
    // NclCecilRewrite.cs ~line 1537) — it dispatches UI callbacks itself rather than
    // delegating to a client window. Nothing on the Message()/Confirm()/StrMenu() path
    // consults this getter today; it is answered truthfully rather than defaulted to
    // false in case something else ever does.
    public bool IsCallbackAllowed => true;

    /// <summary>The fix: capture the message and the AL statement that produced it
    /// instead of the real BC "no client" answer (silently doing nothing). Reads
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

    // ── Everything else: reproduce BC's own "no client connected" answer exactly ──
    // (see the file header — this is NOT a silent fake: it is the identical exception
    // NavSession.ClientCallback's throwing getter raises today, for callers this
    // override's mere existence would otherwise divert around that getter.)
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
    public void SendNotification(NotificationInfo notification) => throw NotAllowed();
    public void SendGlobalNotification(NotificationInfo notification) => throw NotAllowed();
    public void SendSessionUpdateRequest(SessionSettingsInfo sessionSettingsInfo) => throw NotAllowed();
    public void CompanyInformationChanged(CompanyInformationChanges companyInformationChanges) => throw NotAllowed();
    public void WorkDateChanged(DateTime workDate) => throw NotAllowed();
    public void VerifyCallbackAllowed(NavApplicationObjectBase applicationObject) => throw NotAllowed();
    public Task SendPageBackgroundTaskCompletedNotificationAsync(Guid formHandle, int taskId, string clientActivityId) => throw NotAllowed();
    public Task FeedbackRequested(FeedbackRequest feedbackRequest) => throw NotAllowed();
    public void TokenChangedNotification() => throw NotAllowed();
    public Task InvokeTaskPaneAction(InvokeTaskPaneActionArguments arguments) => throw NotAllowed();

    private static NavNCLCallbackNotAllowedException NotAllowed() => new();
}
