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
//      the wiring, without which (1) proves a function nobody calls. That one is written to
//      hold on a box in EITHER state, and says which state it observed, so it cannot pass
//      vacuously in one of them (see WiredInto_TheRealFixture_SkipReasonProperty).

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
    /// than honest. It keeps the visible, counted skip TestArtifacts.SkipIf produces.
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
    /// It does NOT call TestArtifacts.SkipIf on the way in: that would read
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
        var getter = typeof(BcEngineFixture)
            .GetProperty(nameof(BcEngineFixture.SkipReason))!
            .GetGetMethod()!;

        var body = getter.GetMethodBody();
        Assert.NotNull(body);
        var il = body!.GetILAsByteArray();
        Assert.NotNull(il);

        var guard = typeof(BcEngineUnbootstrappedGuard)
            .GetMethod(nameof(BcEngineUnbootstrappedGuard.AssertBootstrapWasRun),
                       System.Reflection.BindingFlags.Static
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public)!;

        // 0x28 = call, followed by a 4-byte metadata token. Scanning for the token itself is
        // what makes this a statement about the emitted call and not about the source text.
        var wanted = BitConverter.GetBytes(guard.MetadataToken);
        var found = false;
        for (var i = 0; i + 4 < il!.Length && !found; i++)
        {
            if (il[i] != 0x28) continue;
            found = il[i + 1] == wanted[0] && il[i + 2] == wanted[1]
                 && il[i + 3] == wanted[2] && il[i + 4] == wanted[3];
        }

        Assert.True(found,
            $"BcEngineFixture.SkipReason does not call {nameof(BcEngineUnbootstrappedGuard)}."
            + $"{nameof(BcEngineUnbootstrappedGuard.AssertBootstrapWasRun)}. That call is what "
            + "converts a silent skip of the entire bc-engine-serial collection into a named "
            + "failure at all ~300 call sites, so without it issue #3835 is back and a "
            + "post-build `dotnet test` reports `Skipped: N` with exit 0 again.");
    }

    /// <summary>
    /// Fixture guard for the test above: a metadata-token scan that found nothing because
    /// the IL was empty, or because 0x28 never appears, would report the same "not found"
    /// as a genuinely deleted call. Pinning that the scan CAN fire — on a method known to
    /// call the guard — is what separates the two.
    /// </summary>
    [Fact]
    public void TheILScan_ItselfDetectsACall_SoItsNegativeIsMeaningful()
    {
        var body = typeof(BcEngineFixture)
            .GetProperty(nameof(BcEngineFixture.SkipReason))!
            .GetGetMethod()!
            .GetMethodBody()!;
        var il = body.GetILAsByteArray()!;

        Assert.NotEmpty(il);
        Assert.Contains((byte)0x28, il);
    }
}
