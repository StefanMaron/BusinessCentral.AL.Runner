using System;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

// CurrPage.Update() under a TestPage — issue #3373. The runner subscribes to NavForm's
// UpdateRequest event in the absent client's place.
// See docs/testpage-currpage-update.md — the mechanism, the measured BC trigger orders, and
// what is deliberately not reproduced.
internal sealed partial class RunnerPageInstance
{
    private bool _updateRequestSubscribed;
    private bool _updateRequested;
    private bool _realisingUpdate;
    private int _triggerDepth;

    /// <summary>
    /// Subscribe to the form's UpdateRequest event, once per page instance.
    ///
    /// Only <c>NavFormUpdateTypes.Update</c> arms the refresh. BC raises the same event from
    /// <c>SaveRecordAsync</c> with <c>RecordSaved</c> alone, which is a plain "the record was
    /// written" notification and not a request to re-load the form — treating the two alike
    /// would refresh after every <c>CurrPage.SaveRecord</c>, which nothing has measured.
    ///
    /// <para><c>UpdateParent</c> is read as a no-op ON PURPOSE, not overlooked. BC ORs it in
    /// when the page declares <c>UpdatePropagation = Both</c>, and it asks the client to
    /// refresh the HOST of this page as well. The runner has no parent link to walk — a
    /// TestPage part reaches its host through the test's own variable, not through a field on
    /// the page instance — so there is nothing here that could carry the refresh upward. It is
    /// not refused loudly either: the flag always arrives ALONGSIDE <c>Update</c>, so this
    /// page's own refresh still happens and the divergence is confined to the host not also
    /// refreshing. Throwing would turn a partial answer into no answer for every
    /// <c>UpdatePropagation = Both</c> page. Unmeasured, and tracked in
    /// docs/testpage-currpage-update.md § "What is deliberately not reproduced".</para>
    /// </summary>
    private void EnsureUpdateRequestSubscription()
    {
        if (_updateRequestSubscribed) return;
        _updateRequestSubscribed = true;
        if (_form is not NavForm form) return;
        form.UpdateRequest += (_, e) =>
        {
            if ((e.UpdateRequestType & NavFormUpdateTypes.Update) == 0) return;
            // A request raised while the refresh itself is running is dropped. Realising it
            // would set the flag again from inside the refresh and refresh again, without
            // bound — a hang, which is a far worse outcome than the divergence of not
            // reproducing whatever BC does for a page whose OnAfterGetCurrRecord calls
            // CurrPage.Update on itself. Nothing measures that shape.
            if (_realisingUpdate) return;
            // A request with NO OWNING TRIGGER is dropped rather than carried. BC's own
            // NavForm internals raise this event from paths the TestPage layer drives
            // directly — NewRecordAsync and the SaveRecordAsync inside it, reached from
            // MockTestPage without any AL trigger on the stack. Arming there would leave the
            // flag set with nothing to consume it, and the refresh would then fire at the end
            // of the NEXT, unrelated trigger: an OnAfterGetCurrRecord attributed to a
            // CurrPage.Update that some earlier operation made. Requiring an owning trigger is
            // what keeps the realisation attributable to the call that armed it.
            if (_triggerDepth == 0) return;
            _updateRequested = true;
        };
    }

    private void BeginTrigger()
    {
        EnsureUpdateRequestSubscription();
        _triggerDepth++;
    }

    /// <summary>
    /// Realise a pending refresh when the OUTERMOST AL trigger returns NORMALLY.
    ///
    /// The timing is measured, not chosen: on BC 28.4 a SetValue whose OnValidate calls
    /// CurrPage.Update(true) produces ValidateBegin, ValidateEnd, THEN the host's
    /// OnAfterGetRecord and OnAfterGetCurrRecord — never between the two Validate markers. The
    /// depth counter is what puts it after the trigger rather than at the event, and it also
    /// keeps one refresh from being raised per nested trigger.
    ///
    /// <paramref name="completed"/> is false when the trigger raised. A trigger that ends in an
    /// AL Error() does not get its refresh: BC's request dies with the failed trigger, and
    /// running OnAfterGetCurrRecord here would run page code during an unwinding AL error — and
    /// if THAT trigger raised, its exception would replace the error the test is asserting on,
    /// which is the masking the TargetInvocationException unwrap at both call sites exists to
    /// prevent. The pending flag is dropped rather than carried, so a request armed by a failed
    /// trigger cannot fire on the next trigger's return either.
    /// </summary>
    private void EndTrigger(bool completed)
    {
        _triggerDepth--;
        if (_triggerDepth > 0) return;
        if (!completed) { _updateRequested = false; return; }
        if (!_updateRequested) return;
        _updateRequested = false;
        _realisingUpdate = true;
        // RaiseOnAfterGetRecord raises OnAfterGetRecord and then OnAfterGetCurrRecord, which is
        // the pair BC produces here — both were observed on 28.4, in that order.
        try { RaiseOnAfterGetRecord(); }
        finally { _realisingUpdate = false; }
    }
}
