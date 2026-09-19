// HandlerStackPreservation — keep the AL handler's own stack when BC rethrows it (#3500).
//
// BC's NavTestExecution.InvokeHandler is the single frame every [HandlerFunctions] callback
// is invoked through. Its catch arm is:
//
//     catch (TargetInvocationException ex) { throw ex.GetBaseException(); }
//
// `throw <existing exception object>` RESETS that object's stack trace, so everything the AL
// handler did before failing is discarded and the reported origin becomes InvokeHandler
// itself. Measured on Microsoft BaseApp surface run 34169134540: 126 failures whose top frame
// is InvokeHandler and which therefore name no origin (#3500).
//
// This is a DIAGNOSTIC change. It does not make a failing handler pass — the same exception
// object, of the same type, with the same message, still propagates to the same caller. What
// changes is that the trace it carries is the one from where it was raised.

using System;
using System.Runtime.ExceptionServices;

namespace AlRunner.Patches;

/// <summary>
/// The replacement BC's <c>NavTestExecution.InvokeHandler</c> catch arm calls in place of
/// <see cref="Exception.GetBaseException"/>, so the rethrow preserves the original trace.
/// </summary>
public static class HandlerStackPreservation
{
    /// <summary>
    /// Unwrap as <see cref="Exception.GetBaseException"/> does, then rethrow preserving the
    /// original stack trace. Never returns.
    ///
    /// <para>SCOPE AUDIT — observably equivalent to BC's <c>throw ex.GetBaseException()</c>
    /// for in-scope test code, except in the one respect this exists to change.</para>
    ///
    /// <list type="bullet">
    /// <item><description><b>Which object propagates</b> — identical. Both compute
    /// <c>GetBaseException()</c> on the same argument and propagate exactly that reference,
    /// so the exception's runtime type, message, <c>Data</c>, <c>InnerException</c> chain and
    /// reference identity are unchanged. An AL <c>asserterror</c> matching on message text,
    /// and any <c>catch (SpecificType)</c> above this frame, behave as before.</description></item>
    /// <item><description><b>Where it is caught</b> — identical.
    /// <c>ExceptionDispatchInfo.Throw()</c> raises from this frame just as <c>throw</c> does
    /// from BC's, so every enclosing handler in <c>NavTestExecution</c> and above still sees
    /// it.</description></item>
    /// <item><description><b>The stack trace</b> — the deliberate difference. BC's <c>throw</c>
    /// resets it to this frame; <c>ExceptionDispatchInfo</c> preserves the frames already on
    /// the object and appends the rethrow point. So the trace grows a prefix naming the
    /// handler's own frames, and the frames BC's version reported are still present after
    /// the <c>--- End of stack trace from previous location ---</c> marker. Nothing that was
    /// visible before is removed.</description></item>
    /// </list>
    ///
    /// <para>Citation: the same idiom one frame below, at
    /// <c>RunnerModalDispatch.Invoke</c>, for the runner's own reflection call; and
    /// <c>TestExecutor.InvokeWithTimeout</c>, which carries a test-body exception across a
    /// thread boundary the same way. The measurement behind the change is in
    /// docs/handler-stack-preservation.md.</para>
    ///
    /// <para>Trap: the declared return type is <see cref="Exception"/> and the method never
    /// returns. That is required, not cosmetic — the Cecil rewrite substitutes this call for
    /// a <c>callvirt GetBaseException</c> whose result BC's next instruction (<c>throw</c>)
    /// consumes, so a <c>void</c> helper would unbalance the evaluation stack and emit invalid
    /// IL that only fails when the JIT reaches the method
    /// (<c>NclCecilRewrite.AssertHelperArityMatches</c>'s doc comment has that failure mode in
    /// full). Keep the signature <c>Exception -> Exception</c> if this is ever rewritten.</para>
    /// </summary>
    public static Exception RethrowPreservingStack(Exception ex)
    {
        // Null is not reachable from BC's body — the argument is the caught
        // TargetInvocationException, and `callvirt` on null would already have thrown before
        // reaching here — but a silent NullReferenceException from inside a diagnostics fix
        // would be the worst possible failure mode, so say what happened instead.
        if (ex == null)
            throw new ArgumentNullException(
                nameof(ex),
                "NavTestExecution.InvokeHandler's catch arm reached HandlerStackPreservation "
                + "with a null exception. BC's body cannot produce that; the Cecil rewrite in "
                + "NclCecilRewrite.Runtime.cs is wired to the wrong instruction. See #3500.");

        ExceptionDispatchInfo.Capture(ex.GetBaseException()).Throw();

        // Unreachable. Present so the method satisfies its declared return type; the IL the
        // rewrite emits never reaches it either, because Throw() above does not return.
        throw new InvalidOperationException(
            "ExceptionDispatchInfo.Throw() returned, which cannot happen.");
    }
}
