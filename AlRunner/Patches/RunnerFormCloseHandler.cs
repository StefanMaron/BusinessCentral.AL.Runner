// RunnerFormCloseHandler — stand in for the CLIENT half of BC's page-close round trip, the
// same way RunnerModalDispatch stands in for the client half of the page-OPEN round trip.
//
// THE PROBLEM IT SOLVES
//   Closing a page in BC is a client round trip, so an AL error raised inside
//   OnQueryClosePage does NOT propagate the way an error from a directly-called AL procedure
//   does. The server raises it out of NavForm.QueryCloseFormAsync; the CLIENT is what decides
//   what the caller ends up seeing. The runner has no client, so until #3057 the raw
//   exception went straight back to the AL that opened the page — an envelope real BC never
//   produces.
//
// HOW BC DOES IT
//   Microsoft.Dynamics.Nav.Client.UI.dll, NavFormCloseHandler.ExecuteCloseCore (decompiled,
//   BC 28.1.49838.53910), in catch order:
//
//       catch (NavFilterErrorOnQueryCloseException)  -> ShowError,   close refused
//       catch (NavErrorOnQueryCloseException)        -> ShowConfirm, then force-close on yes
//       catch (NavNSFormNotOpenedException)          -> force-close
//       catch (NavConnectionLostException) / (NavClientClosingException)
//       catch (NavFormCloseNotAllowedException)      -> LastClosePrevented = true, refuse
//       catch (NavOnClosePageException)              -> ShowError, force-close
//       catch (NavObjectDefinitionChangedException)  -> ShowError, force-close
//       catch (NavNCLMissingUIHandlerException)      -> ShowError, force-close
//       catch (NavTestWrappedException)              -> force-close, rethrow
//       catch (NavSqlConnectionLostException)        -> ShowError unless suppressed, force-close
//       catch (NavBaseException ex)                  -> if (!ex.SuppressMessage)
//                                                           displayMessage.ShowMessage(ex.Message);
//                                                       return false;      // close REFUSED
//
//   An AL Error() / TestField() failure inside OnQueryClosePage is none of the named types —
//   it is a plain NavBaseException, so it lands in that LAST catch. So BC shows the AL error
//   text as a MESSAGE and refuses the close.
//
//   In a test session `displayMessage.ShowMessage` is TestPageMessageHelper.ShowMessage
//   (Microsoft.Dynamics.Nav.Client.TestPageClient.dll), which forwards to the session's
//   OnShowMessageCallback — NavTestExecution.TestHandleMessage. That resolves a
//   [MessageHandler] or raises BC's own "Unhandled UI: Message {text}". Measured on a real BC
//   28.4.53241.0 service tier (issue #3057): an unfilled "Payment Registration Setup" closed
//   through its [ModalPageHandler] reports
//       Unhandled UI: Message Journal Template Name must have a value in Payment Registration
//       Setup: User ID=ADMIN. It cannot be zero or empty.
//   and not the raw NavTestFieldException.
//
// WHAT THIS CLASS DOES NOT DO
//   It does not invent a message channel. `Message` goes through BC's own
//   NavTestExecution.TestHandleMessage — the identical method BC's NavDialog.ALMessage calls
//   for an AL Message() statement — so which [MessageHandler] answers, and the exact wording
//   of the refusal when none does, stay BC's decisions.
//
//   The named exception types above are RETHROWN rather than reproduced. Every one of them
//   reaches the caller as an error in BC too (ShowError on the test client is
//   TestPageMessageHelper.ShowErrorMessage, which throws NavTestWrappedException carrying the
//   same text), so rethrowing the original preserves the observable AL outcome — the text —
//   without the runner claiming to model a force-close-and-continue it has no path to.
using System.Reflection;

namespace AlRunner.Patches;

internal static class RunnerFormCloseHandler
{
    /// <summary>
    /// Exception type names BC's own close handler catches BEFORE its general
    /// <c>NavBaseException</c> case, and turns into something other than a message. Matched by
    /// name because these types live across Ncl / Types / the client assemblies and the runner
    /// links none of them directly.
    /// </summary>
    private static readonly string[] NotAMessage =
    {
        "NavFilterErrorOnQueryCloseException",
        "NavErrorOnQueryCloseException",
        "NavNSFormNotOpenedException",
        "NavConnectionLostException",
        "NavClientClosingException",
        "NavOnClosePageException",
        "NavObjectDefinitionChangedException",
        "NavNCLMissingUIHandlerException",
        "NavTestWrappedException",
        "NavSqlConnectionLostException",
    };

    /// <summary>
    /// Classify an exception that escaped a page's OnQueryClosePage, reproducing
    /// <c>NavFormCloseHandler.ExecuteCloseCore</c>'s decision.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the close must be refused and the caller must NOT treat the close as
    /// having happened. Never returns <c>true</c>: an exception this method does not classify
    /// as a message either propagates or is replaced by a louder one.
    /// </returns>
    /// <remarks>
    /// The common case does not return at all. With no <c>[MessageHandler]</c> declared,
    /// <c>TestHandleMessage</c> raises BC's own "Unhandled UI: Message {text}" refusal from
    /// inside the call — exactly the outcome a real service tier produces for this shape.
    /// </remarks>
    internal static bool RefuseCloseAfter(Exception ex, object? testExecution)
    {
        var name = ex.GetType().Name;

        // BC's NavFormCloseNotAllowedException case: the trigger vetoed by returning false.
        // No message, close prevented — the runner already relied on this and it stays.
        if (name == "NavFormCloseNotAllowedException") return false;

        // Anything BC classifies ahead of its general case, or anything that is not a BC
        // exception at all (a runner-internal failure), keeps its own identity.
        if (Array.IndexOf(NotAMessage, name) >= 0 || !IsNavBaseException(ex))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

        // BC: `if (!ex.SuppressMessage) displayMessage.ShowMessage(ex.Message);` — a suppressed
        // message is shown nowhere on a real tier either, so refusing the close is the whole of
        // BC's behaviour here and nothing is lost.
        if (SuppressMessage(ex)) return false;

        if (!TryShowMessage(ex.Message, testExecution))
            // No message channel means the text would vanish, and a silently refused close is
            // exactly the silent no-op .claude/rules/loud-failures.md forbids. Let the original
            // through instead — worse envelope, but nothing is lost.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

        // A [MessageHandler] consumed the text, so BC's `return false` is reached: the close is
        // refused and the page is STILL OPEN, waiting for a user who does not exist here. That
        // is the same boundary MockTestPage.Close already names for an OnQueryClosePage veto
        // (#2999) — the runner has no model for a page that refuses to close and keeps running.
        // Throwing says so; returning false would let the caller force the page shut and report
        // a close BC did not perform. Tracked in #3179.
        throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            "TestPage page close (OnQueryClosePage)",
            "testpage-close-refused-after-message — OnQueryClosePage raised an error, a "
            + "[MessageHandler] consumed it, and BC then leaves the page OPEN. The runner has no "
            + "model for a page that outlives its own close. See docs/scope.md");
    }

    private static bool IsNavBaseException(Exception ex)
    {
        for (var t = ex.GetType(); t != null; t = t.BaseType)
            if (t.Name == "NavBaseException") return true;
        return false;
    }

    /// <summary>
    /// <c>NavBaseException.SuppressMessage</c> — BC skips the message entirely when it is set.
    /// Read reflectively (the property is on a type in Types.dll the runner does not link) and
    /// defaulted to false, which is the value every AL-raised error carries.
    /// </summary>
    private static bool SuppressMessage(Exception ex)
        => ex.GetType().GetProperty("SuppressMessage", BindingFlags.Public | BindingFlags.Instance)?
               .GetValue(ex) is bool suppress && suppress;

    /// <summary>
    /// BC's <c>displayMessage.ShowMessage</c> on the test client: hand the text to
    /// <c>NavTestExecution.TestHandleMessage</c>, the same method an AL <c>Message()</c>
    /// statement reaches. Returns whether the text was delivered — false only when there is no
    /// test-execution context to deliver it to, which the caller treats as a reason to let the
    /// original exception through rather than lose it.
    /// </summary>
    private static bool TryShowMessage(string message, object? testExecution)
    {
        if (testExecution == null) return false;

        var handle = testExecution.GetType().GetMethod(
            "TestHandleMessage",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null);
        if (handle == null)
            throw new InvalidOperationException(
                "NavTestExecution.TestHandleMessage(string) not found — Ncl shape changed; do not commit");

        try
        {
            // TestHandleMessage returns false when no [Test] is executing (BC's FindHandler is
            // `if (executingTestMethod == null) return null;`). That is not a delivery.
            return handle.Invoke(testExecution, new object?[] { message }) is bool handled && handled;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // The "Unhandled UI: Message …" refusal, and any NavTestWrappedException a
            // [MessageHandler] itself raised. Both are BC's own answer and must reach the test
            // with BC's own stack, not a reflection wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable; satisfies the compiler
        }
    }
}
