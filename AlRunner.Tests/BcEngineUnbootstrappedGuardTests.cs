// BcEngineUnbootstrappedGuardTests — issue #3835.
//
// The defect, measured on this repository
// ---------------------------------------
// After ANY build, `dotnet test` over the bc-engine-serial collection reported:
//
//     Skipped! - Failed: 0, Passed: 0, Skipped: 5, Total: 5   (exit code 0)
//
// and at default verbosity printed no skip reason at all. A build restores a pristine
// bin/Microsoft.Dynamics.Nav.Ncl.dll, so the prerequisite is lost after every build rather
// than once per checkout — which is what makes this recur. That summary is indistinguishable
// from a passing mutation check, and it invalidated a real RED baseline on PR #3829.
//
// #3078 named two remedies and said which was stronger:
//
//   > Either a small tool … or a test-time assertion that fails rather than skips when the
//   > engine prerequisites are absent. The second is stronger …
//
// tools/engine-test-bootstrap.sh was built. The assertion was not. This file proves the
// assertion.
//
// Why these tests can be proven at all
// ------------------------------------
// The guard's subject is "a prerequisite on disk is absent", which a test cannot construct by
// deleting the prerequisite: bin/Microsoft.Dynamics.Nav.Ncl.dll is shared by the whole test
// host, the bootstrap already ran in a [ModuleInitializer] before any test executed, and
// mutating BcEngineBootstrap's private state by reflection would corrupt every other class in
// the bc-engine-serial collection running in the same process.
//
// So the proof is split, and BOTH halves are needed — the first alone would leave the guard
// unwired, the second alone could not distinguish the states it must distinguish:
//
//   1. BcEngineUnbootstrappedGuard.AssertBootstrapWasRun is a PURE function of
//      (ready, reason). Every state it must discriminate is constructed here, exactly as
//      BcEngineReadinessGuardTests does for AssertReadyOnCi, and none of it reads whatever
//      this box happens to have provisioned.
//   2. The REAL BcEngineFixture.SkipReason property is then asserted to route through it —
//      the wiring, without which (1) proves a function nobody calls. Two tests, because on a
//      bootstrapped box (CI's state, always) "the property did not throw" is also what an
//      unwired property does: WiredInto_TheRealFixture_SkipReasonProperty exercises the
//      behaviour in whichever state this box is in, and TheWiring_IsPresent_InEveryState
//      reads the emitted IL, which answers the same in every state. The mutation check
//      measured that split rather than assuming it — see that test's own comment.

using Xunit;

namespace AlRunner.Tests;

public sealed class BcEngineUnbootstrappedGuardTests
{
    // ---- the RED direction: a recoverable prerequisite gap must FAIL, not skip ----

    /// <summary>
    /// The measured defect itself. NclPreloaded is what this box reported on the second
    /// post-build `dotnet test` run: `Cause (NclPreloaded)`, DOTNET_STARTUP_HOOKS unwired,
    /// artifacts present. Before this guard that produced `Skipped: 5` and exit 0; it must
    /// now fail, and the failure must name the tool that fixes it.
    /// </summary>
    [Fact]
    public void ARecoverableBootstrapGap_Fails_RatherThanSkipping()
    {
        var reason = BcEngineSkipReason.Format(BcEngineSkipCause.NclPreloaded, "SENTINEL-3835");

        var ex = Record.Exception(() =>
            BcEngineUnbootstrappedGuard.AssertBootstrapWasRun(ready: false, reason));

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(ex);
        // Not a SkipException: that is the whole point — a skip is what this replaces.
        Assert.IsNotType<SkipException>(ex);
        Assert.Contains(BcEngineSkipReason.BootstrapTool, ex!.Message, StringComparison.Ordinal);
        Assert.Contains("REFUSING TO SKIP", ex.Message, StringComparison.Ordinal);
        // The recorded diagnosis survives into the failure rather than being replaced by it.
        Assert.Contains("SENTINEL-3835", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every locally recoverable cause, not just the one that happens to fire on this box.
    /// A cause added later and classified recoverable is covered the day it is added; one
    /// added and left unclassified fails IsRecoverableLocally's own switch instead.
    /// </summary>
    [Theory]
    [InlineData(BcEngineSkipCause.NclPreloaded)]
    [InlineData(BcEngineSkipCause.BinRewrittenThisProcess)]
    [InlineData(BcEngineSkipCause.CecilCacheCold)]
    [InlineData(BcEngineSkipCause.BootstrapThrew)]
    [InlineData(BcEngineSkipCause.BootstrapDidNotRun)]
    public void EveryLocallyRecoverableCause_Fails(BcEngineSkipCause cause)
    {
        var ex = Record.Exception(() => BcEngineUnbootstrappedGuard.AssertBootstrapWasRun(
            ready: false, BcEngineSkipReason.Format(cause, "d")));

        Assert.NotNull(ex);
        Assert.Contains(BcEngineSkipReason.BootstrapTool, ex!.Message, StringComparison.Ordinal);
    }

    // ---- the distinction that must survive: a legitimately engine-less box still skips ----

    /// <summary>
    /// The constraint guards-need-a-third-state.md sets on this class of fix: a genuinely
    /// absent thing stays a pass. A box with no BC service tier provisioned cannot run these
    /// tests for a correct reason, and failing there would make the suite unrunnable rather
    /// than honest. It keeps the visible, counted skip the shared test-artifacts gate
    /// produces.
    /// </summary>
    [Theory]
    [InlineData(BcEngineSkipCause.ArtifactsMissing)]
    [InlineData(BcEngineSkipCause.ArtifactsIncomplete)]
    public void AnEngineLessBox_StillSkips_RatherThanFailing(BcEngineSkipCause cause)
    {
        Assert.Null(Record.Exception(() => BcEngineUnbootstrappedGuard.AssertBootstrapWasRun(
            ready: false, BcEngineSkipReason.Format(cause, "no artifacts here"))));
    }

    /// <summary>Negative direction for the guard itself: a ready engine is never refused.</summary>
    [Fact]
    public void AReadyEngine_IsNotRefused()
    {
        Assert.Null(Record.Exception(() =>
            BcEngineUnbootstrappedGuard.AssertBootstrapWasRun(ready: true, reason: null)));
    }

    /// <summary>
    /// The third state, and it must not be spelled as either of the other two: a reason
    /// carrying no recognisable cause token could be an unprovisioned box or a missing
    /// bootstrap, and nothing here has measured which. Skipping would claim the first;
    /// staying silent would be the defect itself.
    /// </summary>
    [Theory]
    [InlineData("some reason from somewhere else entirely")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnclassifiableReason_Fails_NamingThatItCouldNotTell(string? reason)
    {
        var ex = Record.Exception(() =>
            BcEngineUnbootstrappedGuard.AssertBootstrapWasRun(ready: false, reason));

        Assert.NotNull(ex);
        Assert.Contains("no recognisable cause token", ex!.Message, StringComparison.Ordinal);
    }

    // ---- the classification, in both directions ----

    [Fact]
    public void EveryDeclaredCause_IsClassified()
    {
        // No cause may reach the guard unclassified: an unclassified one would otherwise
        // pick up whichever answer the switch's default happened to be, which is how a
        // fail-rather-than-skip guard silently reverts to skipping.
        foreach (var cause in Enum.GetValues<BcEngineSkipCause>())
        {
            Assert.Null(Record.Exception(() => BcEngineSkipReason.IsRecoverableLocally(cause)));
        }
    }

    [Fact]
    public void AnUndeclaredCause_Throws_RatherThanDefaultingToSkip()
    {
        var ex = Record.Exception(() => BcEngineSkipReason.IsRecoverableLocally((BcEngineSkipCause)9999));

        Assert.NotNull(ex);
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    /// <summary>
    /// Both directions of the split, asserted as a partition rather than per value, so a
    /// future cause moved from one side to the other cannot leave both sides empty.
    /// </summary>
    [Fact]
    public void TheClassification_SplitsTheCauses_IntoBothKinds()
    {
        var all = Enum.GetValues<BcEngineSkipCause>();
        var recoverable = all.Where(BcEngineSkipReason.IsRecoverableLocally).ToArray();
        var not = all.Where(c => !BcEngineSkipReason.IsRecoverableLocally(c)).ToArray();

        Assert.Equal(5, recoverable.Length);
        Assert.Equal(2, not.Length);
        Assert.Contains(BcEngineSkipCause.ArtifactsMissing, not);
        Assert.Contains(BcEngineSkipCause.ArtifactsIncomplete, not);
    }

    // ---- CauseOf: the reason string round-trips to its cause ----

    [Theory]
    [MemberData(nameof(BcEngineSkipAttributionTests.AllCauses), MemberType = typeof(BcEngineSkipAttributionTests))]
    public void CauseOf_RecoversEveryCause_FromTheReasonFormatWrites(BcEngineSkipCause cause)
        => Assert.Equal(cause, BcEngineSkipReason.CauseOf(BcEngineSkipReason.Format(cause, "d")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Cause (SomethingElse): not one of ours")]
    public void CauseOf_AnswersNull_ForAStringItDidNotWrite(string? reason)
        => Assert.Null(BcEngineSkipReason.CauseOf(reason));

    // ---- the wiring: the real fixture property routes through the guard ----

    /// <summary>
    /// Without this, everything above proves a function nobody calls. It reads the REAL
    /// BcEngineFixture.SkipReason property — unmodified, on whatever state this box is
    /// actually in — and asserts the behaviour appropriate to that state.
    ///
    /// Deliberately written to hold in BOTH states and to assert something different in
    /// each: an unbootstrapped box must see the property THROW (that is #3835's fix, and
    /// the state a post-build run is in), and a bootstrapped one must see it answer.
    ///
    /// It does NOT route through the shared artifacts gate on the way in: that would read
    /// _engine.SkipReason and so trip the very guard under test before asserting anything.
    ///
    /// Measured caveat, and why <see cref="TheWiring_IsPresent_InEveryState"/> exists beside
    /// it: on a BOOTSTRAPPED box this test can only assert that the property does not throw,
    /// which is also what a completely unwired property does. The mutation check confirmed
    /// exactly that — with the guard call deleted, this test failed unbootstrapped and
    /// PASSED bootstrapped. CI is always bootstrapped, so this test alone would prove
    /// nothing there.
    /// </summary>
    [Fact]
    public void WiredInto_TheRealFixture_SkipReasonProperty()
    {
        var fixture = new BcEngineFixture();

        if (fixture.Ready)
        {
            // A bootstrapped box: the property answers, and the guard stays out of the way.
            // A guard that fired on a working box would make the collection unrunnable
            // everywhere, CI included.
            Assert.Null(Record.Exception(() => _ = fixture.SkipReason));
            Assert.NotEmpty(fixture.SkipReason);
            return;
        }

        var cause = BcEngineSkipReason.CauseOf(BcEngineBootstrap.SkipReason);
        var ex = Record.Exception(() => _ = fixture.SkipReason);

        if (cause is not null && !BcEngineSkipReason.IsRecoverableLocally(cause.Value))
        {
            // The engine-less box: the property still answers, so the ~300 call sites in
            // this collection go on producing their visible, counted skips.
            Assert.Null(ex);
            Assert.Contains(cause.Value.ToString(), fixture.SkipReason, StringComparison.Ordinal);
            return;
        }

        // The unbootstrapped box — the state a post-build `dotnet test` is in, and the one
        // this issue is about. The property must refuse.
        Assert.NotNull(ex);
        Assert.Contains(BcEngineSkipReason.BootstrapTool, ex!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wiring, asserted in a way that does not depend on which state this box is in —
    /// which the test above cannot do, because on a bootstrapped box "the property did not
    /// throw" is indistinguishable from "the property has no guard in it".
    ///
    /// Deleting the guard call from BcEngineFixture.SkipReason is the mutation this catches
    /// on EVERY box, including CI. It reads the property's IL for the call rather than
    /// invoking it, because invoking it is precisely what cannot discriminate here: a
    /// source scan would be the cheaper spelling but would match the call in a comment
    /// (#3813 is the live instance of that defect in this suite), and the IL carries only
    /// what the compiler actually emitted.
    /// </summary>
    [Fact]
    public void TheWiring_IsPresent_InEveryState()
    {
        Assert.True(
            Calls(SkipReasonGetter(), GuardMethod()),
            $"BcEngineFixture.SkipReason does not call {nameof(BcEngineUnbootstrappedGuard)}."
            + $"{nameof(BcEngineUnbootstrappedGuard.AssertBootstrapWasRun)}. That call is what "
            + "converts a silent skip of the entire bc-engine-serial collection into a named "
            + "failure at all ~300 call sites, so without it issue #3835 is back and a "
            + "post-build `dotnet test` reports `Skipped: N` with exit 0 again.");
    }

    /// <summary>
    /// The scan itself, in both directions, against methods whose answer is known
    /// independently of anything this PR changed — so that a "not found" from
    /// <see cref="TheWiring_IsPresent_InEveryState"/> means the call is absent, and not that
    /// <see cref="Calls"/> is broken.
    ///
    /// Both directions are needed and neither alone suffices: a <see cref="Calls"/> that
    /// always answered false would pass the negative row, and one that always answered true
    /// would pass the positive row. Only the pair pins that the four-byte token comparison
    /// actually discriminates — which is exactly what a check for "the 0x28 opcode appears
    /// somewhere" does not do, since 0x28 appears in almost every method body ever compiled.
    /// </summary>
    [Fact]
    public void TheILScan_Discriminates_SoItsNegativeIsMeaningful()
    {
        // Positive: a method this file controls, which calls the guard on every path.
        var probe = typeof(BcEngineUnbootstrappedGuardTests)
            .GetMethod(nameof(CallsTheGuardOnce),
                       System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True(Calls(probe, GuardMethod()),
            "the IL scan failed to find a call it is looking straight at, so a negative from it "
            + "says nothing about whether BcEngineFixture.SkipReason calls the guard.");

        // Negative: same shape, same file, one call — to something else. Without this row a
        // Calls() that ignored the token and answered "yes" for any method containing a call
        // opcode would look correct.
        var decoy = typeof(BcEngineUnbootstrappedGuardTests)
            .GetMethod(nameof(CallsSomethingElseOnce),
                       System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.False(Calls(decoy, GuardMethod()),
            "the IL scan reported a call to the guard from a method that does not call it, so it "
            + "is matching the call opcode rather than the target and would report the wiring "
            + "present after the guard call was deleted.");
    }

    // ---- the scan, and the two probes that prove it discriminates -------------------

    private static System.Reflection.MethodInfo SkipReasonGetter() =>
        typeof(BcEngineFixture).GetProperty(nameof(BcEngineFixture.SkipReason))!.GetGetMethod()!;

    private static System.Reflection.MethodInfo GuardMethod() =>
        typeof(BcEngineUnbootstrappedGuard)
            .GetMethod(nameof(BcEngineUnbootstrappedGuard.AssertBootstrapWasRun),
                       System.Reflection.BindingFlags.Static
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public)!;

    /// <summary>
    /// True when <paramref name="caller"/>'s body emits a call to <paramref name="target"/>.
    ///
    /// One implementation, used by the production assertion AND by its own self-check, so the
    /// two cannot drift into checking different things — the failure mode where a guard is
    /// verified by a copy of itself that has since diverged.
    ///
    /// 0x28 is <c>call</c>, followed by a 4-byte metadata token. Matching the TOKEN is what
    /// makes this a statement about the emitted call rather than about the source text: a
    /// source scan would be cheaper and would match the name in a comment (#3813 is the live
    /// instance of that defect in this suite, and it fired on this very file).
    /// </summary>
    private static bool Calls(System.Reflection.MethodInfo caller, System.Reflection.MethodInfo target)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray();
        // An absent body cannot be scanned, and answering "no call here" would report that
        // unmeasurable state as the guard being unwired (guards-need-a-third-state.md).
        Assert.True(il is { Length: > 0 },
            $"{caller.DeclaringType?.Name}.{caller.Name} has no readable IL body, so nothing about "
            + "which methods it calls has been measured.");

        var wanted = BitConverter.GetBytes(target.MetadataToken);
        for (var i = 0; i + 4 < il!.Length; i++)
        {
            if (il[i] != 0x28) continue;
            if (il[i + 1] == wanted[0] && il[i + 2] == wanted[1]
                && il[i + 3] == wanted[2] && il[i + 4] == wanted[3]) return true;
        }
        return false;
    }

    /// <summary>Positive probe for <see cref="Calls"/>: calls the guard, and nothing else.</summary>
    private static void CallsTheGuardOnce()
        => BcEngineUnbootstrappedGuard.AssertBootstrapWasRun(ready: true, reason: null);

    /// <summary>
    /// Negative probe: one call, to a method that is not the guard. Deliberately still a
    /// call, so the only thing separating it from the probe above is the token.
    /// </summary>
    private static void CallsSomethingElseOnce()
        => BcEngineSkipReason.IsRecoverableLocally(BcEngineSkipCause.ArtifactsMissing);
}
