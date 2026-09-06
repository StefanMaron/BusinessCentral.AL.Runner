using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins the runner-side mechanism behind issue #3091: the record of "AL already closed this
/// page" is keyed on the FORM, not on any page wrapper.
///
/// That is the whole point of it, and it is not an implementation detail one could swap for a
/// field. A page that closes itself (<c>CurrPage.Close()</c> from an action) is closed through
/// BC's <c>NavTestExecution.ClosePage</c>, which asks
/// <c>RunnerTestClientSession.GetPage(handle, forClose: true)</c> for a page — and that method
/// constructs a BRAND-NEW <c>LiveNavTestPage</c>, over a brand-new <c>RunnerPageInstance</c>
/// (<c>RunnerPageInstance.Adopt</c> caches nothing). So the object that performs the close is
/// never the object the <c>[ModalPageHandler]</c> is holding. A flag on either one is
/// invisible to the other, and the close then runs a second time when the handler returns:
/// both close triggers fire twice, and a page persisting from <c>OnQueryClosePage</c> writes
/// twice.
///
/// The BC-behaviour half of this — that real BC runs each close trigger exactly once for that
/// shape — is pinned upstream in the corpus, codeunit 60296 "MQC Self Close Tests". These
/// tests pin only the runner's own bookkeeping, which needs no BC to be true.
/// </summary>
public class SelfClosedFormMarkTests
{
    [Fact]
    public void AFormNobodyClosed_IsNotReportedClosed()
    {
        // The common case by far, and the one that must not regress: a page a test opened
        // itself has a form nothing closed from AL, and every built-in action on it stays
        // usable. Reading BC's NavForm.IsOpen here instead of this mark refused four "TRT
        // Tests" whose OpenNew()/OK().Invoke() flow is entirely legitimate — their forms were
        // never opened, which is a different thing from having been closed.
        Assert.False(RunnerPageInstance.WasClosedFromAl(new object()));
    }

    [Fact]
    public void NoForm_IsNotReportedClosed()
    {
        // A page with no form behind it must not start refusing built-in actions.
        Assert.False(RunnerPageInstance.WasClosedFromAl(null));
    }

    [Fact]
    public void TheMarkIsKeyedOnTheFormIdentity_NotOnEquality()
    {
        // Two distinct form objects are two distinct pages, even when they compare equal by
        // value. ConditionalWeakTable keys on reference identity, and that is required: a
        // value-keyed map would let one closed page silently refuse actions on another.
        var one = new EqualByValueForm();
        var two = new EqualByValueForm();
        Assert.Equal(one, two);
        Assert.False(ReferenceEquals(one, two));

        Assert.False(RunnerPageInstance.WasClosedFromAl(one));
        Assert.False(RunnerPageInstance.WasClosedFromAl(two));
    }

    /// <summary>
    /// Every form is unmarked until something closes it, so repeated reads never drift — the
    /// query has to be side-effect free, or asking whether a page was closed would eventually
    /// answer yes.
    /// </summary>
    [Fact]
    public void AskingRepeatedly_DoesNotMarkTheForm()
    {
        var form = new object();

        for (var i = 0; i < 5; i++)
            Assert.False(RunnerPageInstance.WasClosedFromAl(form));
    }

    private sealed record EqualByValueForm
    {
        public string Name { get; init; } = "same";
    }
}
