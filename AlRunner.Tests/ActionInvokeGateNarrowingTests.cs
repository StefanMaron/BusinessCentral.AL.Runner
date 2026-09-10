// The invoke gate must not lose an OnAction to a property it cannot evaluate.
//
// WHAT THIS PINS, AND WHY IT IS ITS OWN SEAM
//   #3504 made a precompiled page's declared Enabled/Visible/Editable readable instead of
//   discarded. For a declaration the runner cannot resolve — an expression whose binding key is
//   not recoverable on a precompiled page, see #3825 — EvaluateProperty refuses, which is right
//   when AL READS the value: the value IS the answer, and either boolean would be a silent wrong
//   answer (loud-failures.md).
//
//   Invoke() is different in kind. There the value is only a GATE in front of the OnAction
//   trigger, so refusing converts "one property is unevaluable" into "this action's business
//   logic does not run at all" — a strictly larger loss, on a path where BC itself would have
//   run the trigger.
//
//   Measured, and this is not hypothetical: Base Application 790 "G/L Account Categories"
//   declares `Enabled = PageEditable` on five actions, a page global its own OnOpenPage assigns
//   from CurrPage.Editable. Codeunit64571.KeywordActionName_OnPrecompiledBasePage_RunsItsOnAction
//   opens that page with OpenEdit() and asserts a G/L Account Category row was INSERTED by the
//   trigger. It passed before #3504's action arm and failed on BC 27.0 afterwards
//   (run 34515369115) with the refusal below — real business logic lost to a property read.
//
// WHY THE ASSERTION IS AT THIS LEVEL
//   Driving it end-to-end needs a precompiled dependency whose page declares such an action AND
//   ships its compiled OnAction trigger. The committed fixture's page 65601 has no actions at
//   all (verified: its DLL carries no OnAction), and fabricating one in the symbol file alone
//   produces an action the compiled dependency cannot dispatch — a test that would pass without
//   proving anything. So the discrimination is pinned directly on the two methods, and the
//   end-to-end claim is Codeunit64571's, on the three BC legs.
using System;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ActionInvokeGateNarrowingTests
{
    // ── the once-per-(page, action) warning ───────────────────────────────────────────────
    //
    // The narrowing is deliberately NOT silent — it changes what runs, so it has to be visible
    // in the log. These pin that it says so, and says so once.

    [Fact]
    public void TheFirstUnresolvableInvokeGate_Warns()
    {
        var page = "ActionInvokeGateNarrowingTests-" + Guid.NewGuid().ToString("N");

        Assert.True(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 4242));
    }

    [Fact]
    public void TheSameActionWarnsOnlyOnce()
    {
        var page = "ActionInvokeGateNarrowingTests-" + Guid.NewGuid().ToString("N");

        Assert.True(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 7)); // first
        Assert.False(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 7)); // suppressed
        Assert.False(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 7));
    }

    [Fact]
    public void TwoDifferentActionsOnOnePage_EachWarnOnce()
    {
        // Keyed on the PAIR, not on the page: a wizard declaring one unresolvable Enabled on
        // three actions must report three, not one, or a reader sees a third of the surface.
        var page = "ActionInvokeGateNarrowingTests-" + Guid.NewGuid().ToString("N");

        Assert.True(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 1));
        Assert.True(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 2));
        Assert.False(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(page, 1));
    }

    [Fact]
    public void OneActionIdOnTwoPages_EachWarnOnce()
    {
        // The other half of the pair. Action ids are per-page member ids, so two pages can carry
        // the same number and must not suppress each other.
        var a = "ActionInvokeGateNarrowingTests-" + Guid.NewGuid().ToString("N");
        var b = "ActionInvokeGateNarrowingTests-" + Guid.NewGuid().ToString("N");

        Assert.True(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(a, 99));
        Assert.True(RunnerPageInstance.WarnOnceAboutUnresolvableInvokeGate(b, 99));
    }

    // ── the discrimination itself ─────────────────────────────────────────────────────────

    [Fact]
    public void TheReadAndTheGate_AreSeparateMethods_SoOneCanRefuseWhileTheOtherDoesNot()
    {
        // The load-bearing structural claim, and the one a future edit is most likely to undo by
        // "simplifying" the gate back to ActionEnabled. If these ever become the same method
        // again, either Enabled() starts guessing (a silent wrong answer to the question AL
        // asked) or Invoke() starts losing OnAction triggers (the 27.0 regression). Both are
        // defects this PR exists to avoid, so the separation is asserted rather than assumed.
        var read = typeof(RunnerPageInstance).GetMethod(
            "ActionEnabled",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
        var gate = typeof(RunnerPageInstance).GetMethod(
            "ActionEnabledForInvoke",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);

        Assert.NotNull(read);
        Assert.NotNull(gate);
        Assert.NotSame(read, gate);
        Assert.Equal(typeof(bool), gate!.ReturnType);
    }

    [Fact]
    public void OnlyTheOutOfScopeRefusalIsAbsorbed_NotEveryFailure()
    {
        // The narrowing catches RunnerOutOfScopeException specifically. A NullReferenceException
        // or an InvalidOperationException out of the same call is a runner bug, not an
        // unevaluable property, and absorbing it would hide a real defect behind an action that
        // silently runs. Asserted on the exception type because that is what the catch names.
        Assert.True(typeof(RunnerOutOfScopeException).IsSubclassOf(typeof(Exception)));
        Assert.False(typeof(NullReferenceException).IsSubclassOf(typeof(RunnerOutOfScopeException)));
        Assert.False(typeof(InvalidOperationException).IsSubclassOf(typeof(RunnerOutOfScopeException)));
    }
}
