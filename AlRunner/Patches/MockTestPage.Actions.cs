// MockTestPage.Actions.cs — the live ITestAction: Invoke() runs the page's own OnAction trigger.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
/// <summary>
/// A page action driven live: Invoke() runs the page's own OnAction trigger, on the page
/// instance the TestPage is driving, so the trigger sees the current row.
///
/// Visible/Enabled come from the action's own declared properties, which are constants or
/// expressions evaluated against the page's live state — so an action gated on the current
/// row (<c>Enabled = RowEditable</c>) reports differently as the cursor moves.
/// </summary>
internal sealed class LiveNavTestAction : ITestAction
{
    private readonly LiveNavTestPage _testPage;
    private readonly RunnerPageInstance _page;
    private readonly int _actionId;

    public LiveNavTestAction(LiveNavTestPage testPage, RunnerPageInstance page, int actionId)
    {
        _testPage = testPage;
        _page = page;
        _actionId = actionId;
    }

    /// <summary>
    /// Save the current row, then run OnAction — BC's order, and the order matters. A real
    /// client sends the row it is on to the server before it invokes an action, so the AL in
    /// OnAction reads a <c>Rec</c> that exists in the table, with its AutoSplitKey field
    /// assigned. Dispatching straight to the trigger let the action see the field values a
    /// test had just set while the row itself was still nowhere.
    /// </summary>
    public void Invoke()
    {
        _testPage.SaveCurrentRow();

        // A DISABLED action's Invoke() does nothing, and does not raise. BC's own
        // Microsoft.Dynamics.Framework.UI.ActionControl.Invoke opens with
        // `if (!Enabled) return null;`, and TestActionProxy.Invoke — the ITestAction a real
        // tier hands NavTestAction — carries no gate of its own, so the refusal is silent all
        // the way up to AL. Measured on BC 28.4: invoking an action declared Enabled = false
        // leaves the OnAction trigger unrun, raises nothing, and leaves the page usable (the
        // next action still runs). Corpus codeunit 60583 "TPAR Tests" pins it, for a literal
        // Enabled = false and for an Enabled bound to an expression that is currently false.
        //
        // Running the trigger anyway was a silent wrong answer: AL got an effect it could not
        // have obtained on a real tier, so a test asserting the effect passed here and failed
        // there. Observably equivalent to BC now, with one honest difference — BC says nothing
        // and this says so on [warn], because the shape it produces (an assertion failing one
        // step later about a missing effect) is exactly what a reader cannot diagnose.
        //
        // SaveCurrentRow stays ahead of the gate: BC's TestActionProxy.Invoke calls
        // parent.ActivateControl(this) before actionControl.Invoke(), so the row reaches the
        // server whether or not the trigger fires. Not separately measured.
        // ActionEnabledForInvoke, not ActionEnabled: a gate the runner cannot evaluate must not
        // cost the OnAction trigger its run. See that method for the measurement and why the
        // READ below keeps refusing.
        if (!_page.ActionEnabledForInvoke(_actionId))
        {
            Console.Error.WriteLine(
                $"[warn] TestPage: action {_actionId} is not Enabled, so Invoke() did not run "
                + "its OnAction trigger — real BC refuses a disabled action silently "
                + "(ActionControl.Invoke: `if (!Enabled) return null;`)");
            return;
        }

        _page.RaiseOnAction(_actionId);
    }

    public bool Visible => _page.ActionVisible(_actionId);
    public bool Enabled => _page.ActionEnabled(_actionId);
}
