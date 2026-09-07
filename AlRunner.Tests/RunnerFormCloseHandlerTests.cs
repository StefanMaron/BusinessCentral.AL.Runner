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
    // — not reworded, not prefixed by the runner.
    [Fact]
    public void AlError_IsHandedToTheMessageChannelVerbatim()
    {
        var probe = new FakeTestExecution(handled: true);

        // A [MessageHandler] consumed it, so BC's own `return false` is reached and the page
        // stays open — a shape the runner refuses loudly rather than modelling.
        var oos = Assert.Throws<RunnerOutOfScopeException>(() =>
            RunnerFormCloseHandler.RefuseCloseAfter(
                new NavNCLDialogException("close refused by OnQueryClosePage"), probe));

        Assert.Equal("close refused by OnQueryClosePage", probe.Seen);
        Assert.Contains("testpage-close-refused-after-message", oos.Message, StringComparison.Ordinal);
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
    // ── The refusal's CLASSIFICATION, not its text (#3179) ───────────────────────────────
    //
    // RunnerOutOfScopeException carries two kinds of refusal, and which kind decides whether an
    // AL [TryFunction] may swallow it (ApplicationObjectBasePatches.IsPermanentOutOfScope):
    //
    //   permanent ("SMTP does not exist here")  -> TryInvoke returns false, matching a real BC
    //                                              environment that also lacks the surface
    //   "not-yet-implemented" (an in-scope gap) -> tears through, so a gap can never read green
    //
    // The test is a STRING PREFIX on the reason, so a gap whose reason merely says
    // not-implemented in prose is classified permanent and silently swallowed. That is the
    // defect issue #2966 was filed and closed for; this surface is a later instance of it,
    // introduced with the close handler itself in #3057 and therefore not covered by that sweep.
    //
    // A page whose close BC refuses IS in scope -- #3179 is open and tracks building it -- so
    // the refusal must tear through a [TryFunction], not become `false`.

    [Fact]
    public void CloseRefusedAfterMessage_IsClassifiedAsAnInScopeGap_NotAPermanentRefusal()
    {
        var probe = new FakeTestExecution(handled: true);

        var oos = Assert.Throws<RunnerOutOfScopeException>(() =>
            RunnerFormCloseHandler.RefuseCloseAfter(new NavNCLDialogException("boom"), probe));

        // The prefix IS the classification. Asserting the surface name too, so a rewording that
        // kept the prefix but lost the surface still fails here.
        Assert.StartsWith("not-yet-implemented", oos.Reason, StringComparison.Ordinal);
        Assert.Contains("testpage-close-refused-after-message", oos.Reason, StringComparison.Ordinal);
    }

    // The property the classification exists to produce, asserted through the real decision
    // point rather than by re-reading the string: a [TryFunction] must NOT swallow this refusal.
    [Fact]
    public void CloseRefusedAfterMessage_TearsThroughATryFunction()
    {
        var probe = new FakeTestExecution(handled: true);

        var oos = Assert.Throws<RunnerOutOfScopeException>(() =>
            BcRuntime.NavApplicationObjectBase_TryInvoke(
                null,
                () => RunnerFormCloseHandler.RefuseCloseAfter(new NavNCLDialogException("boom"), probe)));

        Assert.Contains("testpage-close-refused-after-message", oos.Reason, StringComparison.Ordinal);
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
