// RunnerFormCloseHandlerTests — the runner-side mechanism behind issue #3057.
//
// WHAT IS PINNED HERE, AND WHAT IS NOT
//   That real BC turns an error raised in OnQueryClosePage into a MESSAGE and refuses the
//   close is a plain BC-behaviour claim, and it is asserted upstream in the al-language corpus
//   (codeunit "QCE Query Close Error Tests"), where a real service tier adjudicates it. None
//   of that is re-asserted here.
//
//   What these tests pin is the runner's own classifier: RunnerFormCloseHandler reproduces the
//   catch ORDER of NavFormCloseHandler.ExecuteCloseCore rather than turning every failed close
//   into a message. That ordering is the part a corpus test cannot see — the corpus can only
//   observe the one branch an AL Error() lands in, and a classifier that collapsed every
//   branch into "show a message" would look identical to it while quietly rewriting the
//   envelope of a connection loss, a missing UI handler, or a runner-internal failure.
using System;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;
using Xunit;

namespace AlRunner.Tests;

public class RunnerFormCloseHandlerTests
{
    /// <summary>
    /// Stands in for BC's NavTestExecution. RunnerFormCloseHandler finds
    /// <c>TestHandleMessage(string)</c> by reflection, so a double with the same shape is
    /// enough — and it lets the test observe exactly what text was handed to the message
    /// channel, which a real NavTestExecution would swallow into a handler lookup.
    /// </summary>
    private sealed class FakeTestExecution
    {
        private readonly bool _handled;
        internal string? Seen { get; private set; }
        internal FakeTestExecution(bool handled) => _handled = handled;
        internal bool TestHandleMessage(string message)
        {
            Seen = message;
            return _handled;
        }
    }

    // Positive: the veto branch. BC's NavFormCloseNotAllowedException case shows NO message and
    // refuses the close, and it is checked before everything else. A classifier that reached
    // the message branch here would have to consult the (null) message channel and rethrow.
    [Fact]
    public void Veto_RefusesTheCloseWithoutTouchingTheMessageChannel()
    {
        var probe = new FakeTestExecution(handled: true);

        var mayClose = RunnerFormCloseHandler.RefuseCloseAfter(
            new NavFormCloseNotAllowedException("the page said no"), probe);

        Assert.False(mayClose);
        Assert.Null(probe.Seen);
    }

    // Positive: the general case. An AL error is handed to BC's own message channel, verbatim
    // — not reworded, not prefixed by the runner — and the close is then REFUSED, which is
    // BC's own `return false`. Measured on a real service tier: corpus codeunit 60602
    // "QCM Query Close Msg Tests", green on all eight cloud legs and on the Windows nightly.
    [Fact]
    public void AlError_IsHandedToTheMessageChannelVerbatim_AndTheCloseIsRefused()
    {
        var probe = new FakeTestExecution(handled: true);

        var mayClose = RunnerFormCloseHandler.RefuseCloseAfter(
            new NavNCLDialogException("close refused by OnQueryClosePage"), probe);

        Assert.Equal("close refused by OnQueryClosePage", probe.Seen);
        Assert.False(mayClose);
    }

    // Negative: no message channel means the text has nowhere to go. Losing it would be a
    // silent no-op, so the ORIGINAL exception is rethrown — the same object, not a copy and not
    // a runner-invented wrapper.
    [Fact]
    public void AlError_WithNoTestExecution_RethrowsTheOriginalUntouched()
    {
        var original = new NavNCLDialogException("nowhere to show this");

        var thrown = Assert.Throws<NavNCLDialogException>(() =>
            RunnerFormCloseHandler.RefuseCloseAfter(original, testExecution: null));

        Assert.Same(original, thrown);
    }

    // Negative: a [MessageHandler] that is not there. TestHandleMessage returning false means
    // no [Test] was executing, so nothing received the text — same outcome as no channel at all.
    [Fact]
    public void AlError_WhenTheMessageChannelDeclinesIt_RethrowsTheOriginal()
    {
        var probe = new FakeTestExecution(handled: false);
        var original = new NavNCLDialogException("declined");

        var thrown = Assert.Throws<NavNCLDialogException>(() =>
            RunnerFormCloseHandler.RefuseCloseAfter(original, probe));

        Assert.Same(original, thrown);
        Assert.Equal("declined", probe.Seen);
    }

    // Negative: an exception BC's close handler classifies BEFORE its general case keeps its
    // own identity. BC calls ShowError for this one, which on the test client is an error, not
    // a message — collapsing it into the message branch would change what the test sees.
    [Fact]
    public void MissingUiHandler_IsNotConvertedIntoAMessage()
    {
        var probe = new FakeTestExecution(handled: true);
        var original = new NavNCLMissingUIHandlerException("Unhandled UI: Message something");

        var thrown = Assert.Throws<NavNCLMissingUIHandlerException>(() =>
            RunnerFormCloseHandler.RefuseCloseAfter(original, probe));

        Assert.Same(original, thrown);
        Assert.Null(probe.Seen);
    }

    // Negative: a runner-internal failure is not a BC exception at all and must never be
    // rewritten into a page message — that would hide a runner bug behind an AL-looking error.
    [Fact]
    public void NonBcException_IsRethrownAndNeverShownAsAMessage()
    {
        var probe = new FakeTestExecution(handled: true);
        var original = new InvalidOperationException("NavForm.QueryCloseForm(int) not found");

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            RunnerFormCloseHandler.RefuseCloseAfter(original, probe));

        Assert.Same(original, thrown);
        Assert.Null(probe.Seen);
    }
    // ── What a [MessageHandler]-consumed close error leaves behind (#3179) ──────────────
    //
    // MEASURED, not reasoned: corpus codeunit 60602 "QCM Query Close Msg Tests"
    // (StefanMaron/BusinessCentral.AL.Language.Tests#272, merged bd168356), green on all eight
    // cloud legs and confirmed by the Windows nightly reference tier. Three facts, two of which
    // contradicted what the test author predicted:
    //
    //   1. the caller REGAINS CONTROL -- the close-time error does not propagate;
    //   2. RunModal() reports Action::None, not the OK the [ModalPageHandler] chose, because a
    //      refused close completed no action (the same codeunit's negative control, where the
    //      close SUCCEEDS, does report OK -- so None is the refusal, not a lost action);
    //   3. the page's uncommitted write SURVIVES. Codeunit 60677 asserts the opposite and also
    //      passes: it measures behind asserterror, where what rolls the write back is the
    //      framework unwinding a PROPAGATED error. A consumed message propagates nothing.
    //
    // Fact 1 is what made the previous RunnerOutOfScopeException refusal wrong rather than
    // merely conservative: the runner was raising an error on a path real BC completes without
    // one. This pair pins that it no longer does.

    [Fact]
    public void CloseRefusedAfterMessage_ReturnsFalse_AndDoesNotRaise()
    {
        var probe = new FakeTestExecution(handled: true);

        // No Assert.Throws: the whole claim is that control comes back.
        var mayClose = RunnerFormCloseHandler.RefuseCloseAfter(
            new NavNCLDialogException("boom"), probe);

        Assert.False(mayClose);
        Assert.Equal("boom", probe.Seen);
    }

    // A [TryFunction] must see the refusal the same way it sees any completed call: as a
    // SUCCESS, because nothing was raised. The previous behaviour tore an out-of-scope signal
    // through here, which is what a TryFunction cannot swallow -- so this is the negative of
    // the test it replaces, and it fails against the old code with the refusal escaping.
    [Fact]
    public void CloseRefusedAfterMessage_LeavesATryFunctionReportingSuccess()
    {
        var probe = new FakeTestExecution(handled: true);

        var ok = BcRuntime.NavApplicationObjectBase_TryInvoke(
            null,
            () => RunnerFormCloseHandler.RefuseCloseAfter(new NavNCLDialogException("boom"), probe));

        Assert.True(ok);
        Assert.Equal("boom", probe.Seen);
    }

    // Negative control for the pair above, and the reason they are two tests rather than one:
    // a genuinely permanent refusal must still be trapped into `false`. An "everything tears
    // through" change would pass both tests above and fail this one.
    [Fact]
    public void APermanentRefusal_IsStillTrappedByATryFunction()
    {
        var trapped = BcRuntime.NavApplicationObjectBase_TryInvoke(
            null,
            () => throw new RunnerOutOfScopeException(
                "NavEmail.Send", "email-smtp — see docs/scope.md#email", "email"));

        Assert.False(trapped);
    }
}
