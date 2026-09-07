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
}
