using System;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

// CurrPage.Update() under a TestPage — issue #3373.
//
// BC's own NavForm.UpdateCoreAsync already runs unmodified here: it saves the record and then
// raises the form's public UpdateRequest event. On a real service tier the CLIENT is what
// subscribes, and answering that request is what re-loads the current row and raises the
// page's OnAfterGetRecord/OnAfterGetCurrRecord. Headless there is no subscriber, so the save
// happened and the trigger never did — and a page that derives state in OnAfterGetCurrRecord
// (a page global, or a value pushed into a FactBox part) kept answering the pre-edit value.
//
// This subscribes in the client's place. See docs/testpage-currpage-update.md for the measured
// BC trigger orders and what is deliberately not reproduced.
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
