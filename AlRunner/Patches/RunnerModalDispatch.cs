// RunnerModalDispatch — stand in for the CLIENT half of BC's page-handler round-trip, both
// the modal one (RunModal → [ModalPageHandler]) and the non-modal one (Run → [PageHandler]).
//
// HOW BC DOES IT
//   NavTestExecution.TestHandleModalForm finds the test's [ModalPageHandler], pushes a
//   delegate that will build a NavTestPage and invoke that handler onto dialogHandlerStack,
//   and then asks the CLIENT to run the form modally:
//       TestClientProxy<IClientCallbackHandler>.Proxy(ServiceConnection.CallbackHandler)
//           .FormRunModal(runRequest);
//   A real client opens the page and calls back into the server, which lands in
//   NavTestExecution.ShowDialog(handle) — that pops the delegate, runs the handler, and the
//   resulting FormResult is stored with SetLastFormResult for TestHandleModalForm to return.
//
// WHY THIS EXISTS
//   The runner has no client, so that call reached BC's HeadlessClientCallback, whose whole
//   purpose is to refuse ("Client callbacks are not supported on {0}"). Implementing the
//   real IService/IClientCallbackHandler surface to satisfy one call means ~130 members that
//   exist only to throw, so NclCecilRewrite instead redirects that single call site here.
//
//   TestHandleForm — the NON-MODAL twin — ends in the same receiver chain, and it was worse
//   than refused there: the runner field-pokes NavTestExecution.testClientSession with its own
//   RunnerTestClientSession but never sets testServiceConnection (BC only assigns that inside
//   CreateTestClientSession, which the poke bypasses). So ServiceConnection returned null and
//   `callvirt IService::get_CallbackHandler()` NRE'd inside TestHandleForm's own frame, with
//   no inner frame to name the cause. Redirecting that call site here removes the last read of
//   ServiceConnection, so the broken invariant has no observer left. See issue #2349.
//
//   This is not a shortcut around BC's logic — it performs exactly the step the client would
//   have caused, using BC's own methods: pop the dialog handler, record its result. Every
//   decision that matters (which handler, what it does, what OK/Cancel means) stays in BC's
//   code and in the AL handler.
using System.Reflection;

namespace AlRunner.Patches;

public static class RunnerModalDispatch
{
    /// <summary>
    /// Called from the rewritten NavTestExecution.TestHandleModalForm in place of the client
    /// callback. Signature matches the call site's stack shape: the NavTestExecution (left by
    /// the original `ldarg.0`) and the FormRunModalRequest.
    /// </summary>
    public static void FormRunModal(object testExecution, object runRequest)
    {
        if (testExecution == null || runRequest == null)
            throw RunnerShapeGap.ModalDispatchContext(
                "TestPage modal dispatch",
                "testpage-modal-dispatch-context",
                "the runner was asked to run a modal page with no test-execution context or no "
                + "request");

        var handle = FormHandleOf(runRequest);
        var type = testExecution.GetType();

        // Open the page before handing it to the handler. A real client opens the form on the
        // server as part of this round trip, and opening is what raises the page's OnOpenPage
        // trigger — the single place an AL page is allowed to initialise the state its
        // controls and actions then read. Skipping it meant every handler drove a page whose
        // OnOpenPage had never run, so anything that trigger sets up simply was not there.
        //
        // Deliberately NOT wrapped in a catch: OnOpenPage is AL, and an Error() raised there
        // is a real test failure that must reach the test, not a runner detail to absorb.
        var form = RegisteredForm(handle);
        var opened = TryOpenForm(form);

        // BC's own ShowDialog: pops dialogHandlerStack and runs the pushed handler delegate,
        // which builds the NavTestPage and invokes the AL [ModalPageHandler].
        var showDialog = type.GetMethod("ShowDialog", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "NavTestExecution.ShowDialog not found — Ncl shape changed; do not commit");
        object? result = null;
        try
        {
            result = Invoke(showDialog, testExecution, new object?[] { handle });

            // The step a real client performs between "the user pressed OK" and "the form is
            // gone", and the one this dispatch used to skip entirely: ask the page whether it
            // may close, which is the ONLY thing that raises OnQueryClosePage. BC's
            // NavForm.CloseForm raises OnClosePage and nothing else, so a page doing its work
            // in OnQueryClosePage — the ordinary "Manage X" shape, where the trigger writes a
            // caller-supplied record copy back on OK — lost that write silently. See #3050.
            // Gated on BC's OWN IsOpen, not only the runner-local `opened` captured above.
            // A page can close ITSELF while the handler is running -- CurrPage.Close() from an
            // action's OnAction -- and that path raises both close triggers at the moment the
            // AL asks for them. `opened` cannot see that; IsOpen can. Running this step anyway
            // fired every close trigger a second time, so a page persisting from
            // OnQueryClosePage wrote twice (issue #3091). Real BC runs each exactly once:
            // corpus codeunit 60296 "MQC Self Close Tests".
            if (opened && IsFormOpen(form) && !TryQueryCloseForm(form!, result)) result = null;
        }
        finally
        {
            // Close only what this method opened, and only through BC's own CloseForm, so the
            // form leaves the company's registry exactly the way BC would have left it.
            //
            // FormResult.None stays deliberate: passing the handler's real result would also
            // turn on CloseFormAsync's StoreSaveValues(..., persistData: true), a second
            // behaviour change nothing here has measured.
            if (opened && IsFormOpen(form)) TryCloseForm(form!, result: null);
        }

        // The handler's outcome (OK/Cancel) is what the AL that called RunModal receives.
        // TestHandleModalForm reads it off formResultStack, which SetLastFormResult writes.
        var setLastFormResult = type.GetMethod("SetLastFormResult",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "NavTestExecution.SetLastFormResult not found — Ncl shape changed; do not commit");
        if (result != null && setLastFormResult.GetParameters()[0].ParameterType.IsInstanceOfType(result))
            Invoke(setLastFormResult, testExecution, new[] { result });
    }

    /// <summary>
    /// Called from the rewritten NavTestExecution.TestHandleForm in place of the client
    /// callback — the NON-MODAL twin of <see cref="FormRunModal"/>. Signature matches the call
    /// site's stack shape: the NavTestExecution (left by the original `ldarg.0`) and the
    /// FormRunRequest.
    ///
    /// BC's own callback for this direction is ShowForm(handle), which a real client reaches
    /// after opening the page. ShowForm decides everything that matters: whether the page was
    /// trapped by the test (attach it and let the test drive it), or whether a [PageHandler]
    /// answers it (build a NavTestPage and invoke the handler), or neither — in which case BC
    /// raises its own NavTestPageInvokedWithoutHandlerException. None of that logic is
    /// duplicated here.
    /// </summary>
    public static void FormRun(object testExecution, object runRequest)
    {
        if (testExecution == null || runRequest == null)
            throw RunnerShapeGap.ModalDispatchContext(
                "TestPage page dispatch",
                "testpage-page-dispatch-context",
                "the runner was asked to run a page with no test-execution context or no request");

        var handle = FormHandleOf(runRequest);
        var type = testExecution.GetType();
        var form = RegisteredForm(handle);

        // A page the test TRAPPED (TestPage.Trap()) is handed to the test's own TestPage
        // variable by ShowForm and stays open until the test closes it. Closing it here would
        // pull the page out from under the AL that is about to drive it — so ask BEFORE
        // ShowForm, which consumes the trap.
        var trapped = HasTrap(testExecution, form);

        // Same reasoning as the modal path: a real client opens the form as part of this round
        // trip, and opening is what raises OnOpenPage. Not wrapped in a catch — an Error()
        // raised in OnOpenPage is a real test failure.
        var opened = TryOpenForm(form);

        var showForm = type.GetMethod("ShowForm", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "NavTestExecution.ShowForm not found — Ncl shape changed; do not commit");
        try
        {
            Invoke(showForm, testExecution, new object?[] { handle });

            // Same missing step as the modal path (#3050). ShowForm returns nothing, so there
            // is no handler result to forward here — and BC does not need one: measured on
            // real BC 28.4.53241.0 (corpus "MQC Tests", codeunit 60276, arm g), a
            // [PageHandler]-driven Page.Run raises OnQueryClosePage with CloseAction OK and
            // then OnClosePage. A trapped page is the test's to close, so it is left alone.
            if (opened && !trapped && IsFormOpen(form))
                TryQueryCloseForm(form!, NonModalCloseResult(form!));
        }
        finally
        {
            if (opened && !trapped && IsFormOpen(form)) TryCloseForm(form!, result: null);
        }
    }

    /// <summary>
    /// Whether the test has an outstanding TestPage.Trap() for this form's page, asked through
    /// BC's own NavTestExecution.HasTrap so the answer is the one ShowForm will act on.
    /// </summary>
    private static bool HasTrap(object testExecution, object? form)
    {
        if (form == null) return false;

        var objectId = form.GetType()
            .GetProperty("ObjectId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(form);
        if (objectId?.GetType()
                .GetProperty("ObjectNumber", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(objectId) is not int pageNo)
            return false;

        var hasTrap = testExecution.GetType().GetMethod("HasTrap",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { typeof(int) }, modifiers: null);
        if (hasTrap == null) return false;
        return Invoke(hasTrap, testExecution, new object?[] { pageNo }) is true;
    }

    /// <summary>
    /// Replacement for NavSession.SetServerFormRequestData, whose real body throws
    /// NotSupportedException outright when there is no service connection — which there never
    /// is here — before the modal dispatch above is ever reached.
    ///
    /// The real body delegates to the server connection to fill the request the CLIENT will
    /// act on: which page, its parameters, its handle. Of that, the only field anything in
    /// this process reads is FormHandle: BC's pushed dialog delegate uses it to fetch the
    /// page, and NavCompany.GetRegisteredForm is keyed by exactly that value. So set it from
    /// the form and leave the client-facing rest alone — there is no client to render it.
    /// </summary>
    public static void SetServerFormRequestData(object session, object form, object parameters, object data)
    {
        if (form == null || data == null) return;

        var handle = form.GetType()
            .GetProperty("Handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(form)
            ?? throw new InvalidOperationException(
                "NavForm.Handle not found — Ncl shape changed; do not commit");

        var formHandle = data.GetType()
            .GetProperty("FormHandle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "FormRunData.FormHandle not found — Ncl shape changed; do not commit");
        formHandle.SetValue(data, handle);
    }

    /// <summary>The NavForm BC registered under <paramref name="handle"/>, or null.</summary>
    private static object? RegisteredForm(Guid handle)
    {
        try
        {
            var session = AlRunner.BcRuntime.SkeletonSession;
            var company = session?.GetType()
                .GetProperty("Company", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(session);
            var get = company?.GetType().GetMethod("GetRegisteredForm",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(Guid) }, modifiers: null);
            return get?.Invoke(company, new object[] { handle });
        }
        catch (TargetInvocationException) { return null; }
    }

    /// <summary>
    /// Open <paramref name="form"/> through BC's own OpenForm (which raises OnOpenPage).
    /// Returns whether this call is the one that opened it — a form BC already opened must
    /// not be opened again (NavNCLFormAlreadyOpenedException) nor closed by us.
    /// </summary>
    private static bool TryOpenForm(object? form)
    {
        if (form == null) return false;
        var isOpen = form.GetType().GetProperty("IsOpen",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (isOpen?.GetValue(form) is true) return false;

        var openForm = form.GetType().GetMethod("OpenForm",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        if (openForm == null) return false;
        Invoke(openForm, form, Array.Empty<object?>());
        return true;
    }

    /// <summary>
    /// Whether BC still considers this form open — <c>NavForm.IsOpen</c>, the state BC's own
    /// <c>NavForm.Close()</c> guards on. Asked instead of trusting the runner-local flag
    /// captured before the handler ran, because AL can close the page from under it.
    ///
    /// A form whose IsOpen cannot be read answers TRUE, deliberately: that preserves the
    /// behaviour this gate was added to, so an unreadable property cannot silently turn the
    /// close sequence off. Doing the close twice is the bug being fixed; not doing it at all
    /// would be the bug #3050 fixed, and this is not the place to reintroduce it.
    /// </summary>
    private static bool IsFormOpen(object? form)
    {
        if (form == null) return false;
        try
        {
            var isOpen = form.GetType().GetProperty("IsOpen",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return isOpen?.GetValue(form) is not false;
        }
        catch (Exception)
        {
            // One caller is inside a finally, where an exception raised here would REPLACE
            // whatever the handler was already failing with — the test would report a
            // reflection error instead of its own. Answer "open", the same way an unreadable
            // property does above, and let the original failure through.
            return true;
        }
    }

    /// <summary>
    /// Raise the page's OnQueryClosePage through BC's own NavForm.QueryCloseForm, which is the
    /// only method that raises it (NavForm.CloseForm raises OnClosePage alone).
    ///
    /// Returns whether the page allowed the close. A page whose OnQueryClosePage returns false
    /// makes BC's QueryCloseFormAsync throw NavFormCloseNotAllowedException; measured on real
    /// BC 28.4.53241.0, that veto does NOT reach the test as an error — the page still closes
    /// and RunModal() reports Action::None instead of what the handler chose. Answering false
    /// here reproduces exactly that: the caller drops the handler's result, so the FormResult.None
    /// BC already pushed on formResultStack is what the calling AL reads back.
    ///
    /// Anything else the trigger raises — an Error() in AL, most of all — propagates untouched.
    /// </summary>
    private static bool TryQueryCloseForm(object form, object? result)
    {
        var queryCloseForm = form.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "QueryCloseForm"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType == typeof(int))
            ?? throw new InvalidOperationException(
                "NavForm.QueryCloseForm(int) not found — Ncl shape changed; do not commit");

        int closeActionValue;
        try { closeActionValue = Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // No usable result means no defensible CloseAction to raise the trigger with, and
            // inventing one would be worse than the gap: skip and let CloseForm run.
            return true;
        }

        try
        {
            Invoke(queryCloseForm, form, new object?[] { closeActionValue });
            return true;
        }
        catch (Exception ex) when (ex.GetType().Name == "NavFormCloseNotAllowedException")
        {
            return false;
        }
    }

    /// <summary>
    /// The CloseAction BC's own client uses for a NON-modal page it closes on a
    /// [PageHandler]'s behalf: FormResult.OK, measured on real BC 28.4.53241.0 (corpus "MQC
    /// Tests" arm g). Read off the FormResult enum BC's own CloseForm declares rather than
    /// hardcoding 1, so a renumbering in a future Ncl cannot silently change which action the
    /// trigger sees.
    /// </summary>
    private static object? NonModalCloseResult(object form)
    {
        var closeForm = form.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "CloseForm" && m.GetParameters().Length == 1);
        var formResultType = closeForm?.GetParameters()[0].ParameterType;
        if (formResultType == null || !formResultType.IsEnum) return null;
        return Enum.TryParse(formResultType, "OK", out var ok) ? ok : null;
    }

    private static void TryCloseForm(object form, object? result)
    {
        var closeForm = form.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "CloseForm" && m.GetParameters().Length == 1);
        if (closeForm == null) return;
        var formResultType = closeForm.GetParameters()[0].ParameterType;
        var arg = result != null && formResultType.IsInstanceOfType(result)
            ? result
            : Enum.ToObject(formResultType, 0);
        try { Invoke(closeForm, form, new[] { arg }); }
        catch (Exception ex)
        {
            // Closing is cleanup, not the test's subject: a failure here must not replace the
            // handler's own outcome, but it must not vanish either.
            Console.Error.WriteLine($"[RunnerModalDispatch] CloseForm failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Guid FormHandleOf(object runRequest)
    {
        var data = runRequest.GetType()
            .GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(runRequest)
            ?? throw new InvalidOperationException(
                "FormRunModalRequest.Data not found — Ncl shape changed; do not commit");
        return data.GetType()
            .GetProperty("FormHandle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(data) is Guid handle
            ? handle
            : throw new InvalidOperationException(
                "FormRunData.FormHandle not found — Ncl shape changed; do not commit");
    }

    /// <summary>Invoke, surfacing the AL handler's own Error() rather than a reflection wrapper.</summary>
    private static object? Invoke(MethodInfo method, object target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }
}
