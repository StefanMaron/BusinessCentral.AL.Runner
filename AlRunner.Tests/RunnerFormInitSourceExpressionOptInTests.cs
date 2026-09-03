// RunnerFormInitSourceExpressionOptInTests — contract tests for the per-instance opt-in that
// lets a report's request page publish its control -> report-global bindings (issue #2442).
//
// This is deliberately NOT a claim about what Business Central does. That claim — a
// [RequestPageHandler]'s SetValue on a request-page control is what the report BODY reads
// back off the report global — is pinned upstream against a real service tier in
// StefanMaron/BusinessCentral.AL.Language.Tests (report 60751, codeunit 60752). What is
// provable here without a loaded BC runtime is the gate itself, and the gate is where the
// whole safety argument of the fix lives.
//
// The argument: NclCecilRewrite guards THREE NavForm methods behind RunnerFormInit —
// InitializeForm and CallInitializeComponentExtensionMethod, which reach into skeleton-session
// state that headless mode leaves unset, and RegisterSourceExpression, which is a pure
// "record this binding" step. A request page needs the third and must not get the first two.
// MarkSourceExpressionsWanted therefore feeds ShouldRegisterSourceExpressions ONLY.
//
// So both directions matter and both are pinned below. A regression that widened the mark to
// ShouldRunRealFormInit or ShouldResolveMasterPage would re-enable exactly the initialisation
// the request-page path was neutered for, and nothing in the AL suites would name that as the
// cause — it would surface as an unrelated NRE deep inside InitializeComponent.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerFormInitSourceExpressionOptInTests
{
    // A plain object stands in for a NavForm: every RunnerFormInit gate is keyed on instance
    // identity through a ConditionalWeakTable, and the two type-dependent branches of
    // ShouldResolveMasterPage (IsRequestPage, AlPageMetadataRegistry) are only reached for a
    // real NavForm — a non-NavForm falls through to false, which is what the negatives here
    // want to observe anyway.
    private static object NewForm() => new();

    [Fact]
    public void MarkSourceExpressionsWanted_AdmitsThatFormToSourceExpressionRegistration()
    {
        var form = NewForm();
        Assert.False(RunnerFormInit.ShouldRegisterSourceExpressions(form));

        RunnerFormInit.MarkSourceExpressionsWanted(form);

        Assert.True(RunnerFormInit.WantsSourceExpressions(form));
        Assert.True(RunnerFormInit.ShouldRegisterSourceExpressions(form));
    }

    // The load-bearing negative: the mark must NOT widen the other two gates. If this ever
    // goes green-with-true, a request page starts running BC's real InitializeForm /
    // CallInitializeComponentExtensionMethod against skeleton state that does not support it.
    [Fact]
    public void MarkSourceExpressionsWanted_DoesNotAdmitRealFormInitOrMasterPageResolution()
    {
        var form = NewForm();
        RunnerFormInit.MarkSourceExpressionsWanted(form);

        Assert.False(RunnerFormInit.ShouldRunRealFormInit(form));
        Assert.False(RunnerFormInit.ShouldResolveMasterPage(form));
    }

    // The opt-in is per INSTANCE, not global: marking one form must leave every other form on
    // the previous behaviour. A table keyed on anything coarser (page id, "is a request page")
    // would fail this.
    [Fact]
    public void MarkSourceExpressionsWanted_DoesNotLeakToOtherForms()
    {
        var marked = NewForm();
        var other = NewForm();

        RunnerFormInit.MarkSourceExpressionsWanted(marked);

        Assert.True(RunnerFormInit.ShouldRegisterSourceExpressions(marked));
        Assert.False(RunnerFormInit.WantsSourceExpressions(other));
        Assert.False(RunnerFormInit.ShouldRegisterSourceExpressions(other));
    }

    // MarkRealInit keeps its own, wider meaning — it admits all three gates, including
    // registration. Pinned here so the two marks cannot silently converge.
    [Fact]
    public void MarkRealInit_StillAdmitsSourceExpressionRegistrationToo()
    {
        var form = NewForm();
        RunnerFormInit.MarkRealInit(form);

        Assert.True(RunnerFormInit.ShouldRunRealFormInit(form));
        Assert.True(RunnerFormInit.ShouldRegisterSourceExpressions(form));
        // ...but it is not the mark the request-page path uses, so the narrow table stays empty.
        Assert.False(RunnerFormInit.WantsSourceExpressions(form));
    }

    // The guards run inside BC's own IL and must never throw — a null form is the cheapest
    // way that contract can be violated.
    [Fact]
    public void Gates_NullForm_AnswerFalseWithoutThrowing()
    {
        RunnerFormInit.MarkSourceExpressionsWanted(null!);

        Assert.False(RunnerFormInit.WantsSourceExpressions(null!));
        Assert.False(RunnerFormInit.ShouldRegisterSourceExpressions(null!));
    }
}
