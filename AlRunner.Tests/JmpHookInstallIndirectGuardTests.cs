using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #3281 — <see cref="JmpHook.InstallIndirect"/> consulted NEITHER of the two guards
/// <see cref="JmpHook.Apply"/> has, so it wrote a native precode patch on top of a method whose
/// body a Cecil IL rewrite already owns, on a runtime where the whole JmpHook layer is supposed
/// to be off.
///
/// The two guards, and why each matters here:
///
///   1. <c>_disabled</c> — the documented default is "Cecil-only on EVERY runtime (JmpHooks
///      OFF)", because the native-precode parsing "is tuned to .NET 10's layout and SEGFAULTS
///      on .NET 8 — which is BC28's REAL runtime". <c>Apply</c> honours that switch;
///      <c>InstallIndirect</c> did not, so <c>AL_RUNNER_NO_JMPHOOK=1</c> did not actually
///      disable every JmpHook and the escape hatch could not be used to bisect one.
///
///   2. <c>NclCecilRewrite.CecilOwned</c> — "a method migrated to a Cecil IL rewrite must be
///      owned by EXACTLY ONE mechanism. Installing a JmpHook on top of the Cecil body recreates
///      the coexistence double-dispatch spin." <c>Apply</c> checks the registry;
///      <c>InstallIndirect</c> did not.
///
/// Those two facts met on <c>RecordImplementation.CalcFieldsAsync</c>. Both overloads are
/// registered in <c>CecilOwned</c> (NclCecilRewrite.Records.cs), under a comment saying the keys
/// are there "so FlowFieldPatches.Register's JmpHook.Apply fallback becomes a no-op" — but
/// FlowFieldPatches.Register never reaches <c>Apply</c> for them. It calls
/// <c>InstallIndirect</c> FIRST and only falls back to <c>Apply</c> when that returns false, so
/// the guarded path was unreachable and the registration was inert for exactly the two methods
/// it names.
///
/// It is also invisible: <c>Apply</c> records every skip into the orphaned/redundant sets that
/// <c>AL_RUNNER_HOOK_AUDIT=1</c> prints, and <c>InstallIndirect</c> recorded nothing. Measured
/// on the #3281 reproducer bundle, <c>CalcFieldsAsync/2</c> and <c>/3</c> appear in neither
/// audit list while the runner is patching both — the audit that exists to make hook ownership
/// measurable had its blind spot precisely where the double ownership was.
///
/// These tests pin the DECISION (a pure function over the same two inputs Apply uses), not the
/// native memory write, for the same reason <see cref="JmpHookRegionSizeTests"/> pins the page
/// arithmetic: the behavioural proof is a process kill, which is not CI-runnable.
/// </summary>
public class JmpHookInstallIndirectGuardTests
{
    // A real MethodBase/MethodInfo pair to hand the decision function. Nothing is patched --
    // ClassifyIndirectInstall never touches native memory.
    private static readonly MethodInfo SomeMethod =
        typeof(JmpHookInstallIndirectGuardTests).GetMethod(
            nameof(SampleTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void SampleTarget() { }

    /// <summary>
    /// Guard 1. With the JmpHook layer disabled -- the shipped default on every runtime -- an
    /// indirect install must SKIP. Before the fix InstallIndirect ignored the switch entirely
    /// and this returned <see cref="JmpHook.IndirectInstallDecision.Install"/>.
    /// </summary>
    [Fact]
    public void WhenLayerDisabled_SkipsInsteadOfPatching()
    {
        var decision = JmpHook.ClassifyIndirectInstall(
            SomeMethod, jmpHookDisabled: true, cecilOwnsTarget: false);

        Assert.Equal(JmpHook.IndirectInstallDecision.SkipDisabled, decision);
    }

    /// <summary>
    /// Guard 2. Even with the layer explicitly re-enabled (AL_RUNNER_ENABLE_JMPHOOK=1), a method
    /// a Cecil rewrite already owns must not be patched a second time -- that is the coexistence
    /// double-dispatch the CecilOwned registry exists to prevent. This is the assertion the
    /// escape hatch makes load-bearing: it is the ONLY configuration in which the old code could
    /// double-own a method, and it is exactly the configuration an agent reaches for when
    /// bisecting.
    /// </summary>
    [Fact]
    public void WhenCecilOwnsTheMethod_SkipsEvenWithLayerEnabled()
    {
        var decision = JmpHook.ClassifyIndirectInstall(
            SomeMethod, jmpHookDisabled: false, cecilOwnsTarget: true);

        Assert.Equal(JmpHook.IndirectInstallDecision.SkipCecilOwned, decision);
    }

    /// <summary>
    /// The negative direction, and the reason neither assertion above is satisfied by an
    /// implementation that just always skips: with the layer enabled and no Cecil owner, the
    /// install must still proceed. A blanket "never install" would break every hook the escape
    /// hatch exists to restore.
    /// </summary>
    [Fact]
    public void WhenEnabledAndNotCecilOwned_StillInstalls()
    {
        var decision = JmpHook.ClassifyIndirectInstall(
            SomeMethod, jmpHookDisabled: false, cecilOwnsTarget: false);

        Assert.Equal(JmpHook.IndirectInstallDecision.Install, decision);
    }

    /// <summary>
    /// Disabled AND Cecil-owned reports the disabled reason, matching Apply's ordering (its
    /// <c>_disabled</c> branch runs first and classifies into redundant-vs-orphaned from there).
    /// Pinned so the two mechanisms cannot drift on which reason they attribute a skip to.
    /// </summary>
    [Fact]
    public void DisabledAndCecilOwned_ReportsDisabled_MatchingApplyOrdering()
    {
        var decision = JmpHook.ClassifyIndirectInstall(
            SomeMethod, jmpHookDisabled: true, cecilOwnsTarget: true);

        Assert.Equal(JmpHook.IndirectInstallDecision.SkipDisabled, decision);
    }

    /// <summary>
    /// The call-site contract, which is where a naive fix goes wrong. FlowFieldPatches.Register
    /// reads a <c>false</c> return as "the precode had a shape I could not patch" and falls back
    /// to <see cref="JmpHook.Apply"/>. So a SKIP must report <c>true</c> -- "handled, do not fall
    /// back" -- or the guard just reroutes the same install through the other entry point.
    ///
    /// Returning false for a skip would leave the observable behaviour of #3281 completely
    /// unchanged while looking like a fix, which is why this is asserted separately from the
    /// classification above.
    /// </summary>
    [Fact]
    public void ASkipReportsHandled_SoTheCallerDoesNotFallBackToApply()
    {
        const string why =
            "a skipped indirect install must return true, or FlowFieldPatches.Register falls "
            + "back to JmpHook.Apply and the native patch lands anyway";

        Assert.True(
            JmpHook.IndirectInstallReportsHandled(JmpHook.IndirectInstallDecision.SkipDisabled), why);
        Assert.True(
            JmpHook.IndirectInstallReportsHandled(JmpHook.IndirectInstallDecision.SkipCecilOwned), why);
    }

    /// <summary>
    /// And the converse: only a real install reports handled-via-patching. Without this the
    /// theory above is satisfied by a function returning true unconditionally.
    /// </summary>
    [Fact]
    public void AnInstallAlsoReportsHandled_ButIsADistinctDecision()
    {
        Assert.True(JmpHook.IndirectInstallReportsHandled(JmpHook.IndirectInstallDecision.Install));
        Assert.NotEqual(JmpHook.IndirectInstallDecision.Install,
            JmpHook.IndirectInstallDecision.SkipDisabled);
        Assert.NotEqual(JmpHook.IndirectInstallDecision.Install,
            JmpHook.IndirectInstallDecision.SkipCecilOwned);
    }

    /// <summary>
    /// The registry fact the whole defect rests on: both CalcFieldsAsync overloads really are
    /// Cecil-owned, so guard 2 really does fire for them. If someone ever removes those keys,
    /// this fails and names the reason rather than silently re-opening the double-ownership.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::CalcFieldsAsync/2")]
    [InlineData("Microsoft.Dynamics.Nav.Runtime.RecordImplementation::CalcFieldsAsync/3")]
    public void BothCalcFieldsAsyncOverloads_AreCecilOwned(string key)
    {
        Assert.Contains(key, NclCecilRewrite.CecilOwned);
    }

    /// <summary>
    /// A skipped indirect install must be VISIBLE to <c>AL_RUNNER_HOOK_AUDIT=1</c>, the way a
    /// skipped <c>Apply</c> already is. The audit's entire purpose is that hook ownership be
    /// measurable rather than invisible; InstallIndirect recorded nothing, so on the #3281
    /// bundle these two methods appeared in neither the orphaned nor the redundant list while
    /// the runner was in fact patching both.
    /// </summary>
    [Fact]
    public void ASkippedIndirectInstall_IsRecordedForTheHookAudit()
    {
        var label = "AuditProbe.InstallIndirect/" + Guid.NewGuid().ToString("N");

        JmpHook.RecordIndirectInstallSkip(
            JmpHook.IndirectInstallDecision.SkipCecilOwned, SomeMethod, label);

        Assert.Contains(JmpHook.RedundantHooks, e => e.Contains(label, StringComparison.Ordinal));
    }

    /// <summary>
    /// The other audit bucket, and the one that carries real information: a target owned by
    /// NEITHER mechanism is an orphan -- the patch is supposed to act and silently does not.
    /// Recording it as "redundant" instead would file a live gap under "safe to delete".
    /// </summary>
    [Fact]
    public void ADisabledSkipOfANonCecilTarget_IsRecordedAsOrphanedNotRedundant()
    {
        var label = "AuditProbe.InstallIndirect.Orphan/" + Guid.NewGuid().ToString("N");

        JmpHook.RecordIndirectInstallSkip(
            JmpHook.IndirectInstallDecision.SkipDisabled, SomeMethod, label);

        Assert.Contains(JmpHook.OrphanedHooks, e => e.Contains(label, StringComparison.Ordinal));
        Assert.DoesNotContain(JmpHook.RedundantHooks, e => e.Contains(label, StringComparison.Ordinal));
    }
}
