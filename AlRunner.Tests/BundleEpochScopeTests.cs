// BundleEpochScopeTests — issue #4222, the per-invocation bundle marker itself.
//
// WHAT THIS PINS, AND WHY IT IS NOT THE AL FIXTURE'S JOB
//   The AL-observable claim — "Event Subscription (2000000140) must not list a previous
//   bundle's subscriptions" — is proved end-to-end by
//   AlRunner.Tests/Fixtures/EventSubscriptionMultiBundle, driven as a two-bundle runner
//   invocation. That fixture is the proof of the FIX; this file pins the MARKER the fix rests
//   on, whose three interesting states an AL fixture cannot reach from outside:
//
//     * the fail-open answer for an assembly that was never stamped (Base/System Application,
//       and anything loaded before the first BeginBundleEpoch). Getting this wrong drops
//       ~3,300 Microsoft subscriber rows rather than the one this issue is about, and the AL
//       fixture would still pass, because its own bundles ARE stamped.
//     * re-stamping on re-note, which is what a dependency module reused across two bundles
//       depends on (#4100 re-notes it deliberately).
//     * the reset dropping stamps, which no single-bundle invocation reaches at all.
//
// WHY A MECHANISM TEST RATHER THAN A CORPUS TEST
//   Every claim here is about which bundle of ONE runner process an assembly belongs to —
//   multi-bundle wiring, which .claude/rules/bc-behavior-tests-go-upstream.md names as
//   explicitly runner-specific. Real BC serves one application and has no bundle iteration to
//   ask about, so there is no service-tier assertion to make: the AL a corpus test could run
//   is identical on both sides of this fix. See the PR body for the full corpus decision.
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class BundleEpochScopeTests
{
    // Any assembly object works: the marker keys on reference identity and never reads
    // anything out of the assembly. Using assemblies that are already loaded keeps the test
    // free of fixture-emission cost, which is the whole reason these three states are
    // cheap to pin here and expensive to reach through AL.
    private static Assembly A => typeof(BundleEpochScopeTests).Assembly;
    private static Assembly B => typeof(object).Assembly;

    private static readonly MethodInfo Begin = Bind("BeginBundleEpoch");
    private static readonly MethodInfo Stamp = Bind("StampBundleEpoch");
    private static readonly MethodInfo IsCurrent = Bind("IsCurrentBundleAssembly");

    /// <summary>
    /// Bind one member of <c>BcRuntime</c>'s bundle-epoch surface, REQUIRED.
    ///
    /// <para>A null here means the runner's own shape moved, which is unmeasurable rather than
    /// absent: carrying on would leave every assertion below passing against a marker that no
    /// longer exists (.claude/rules/guards-need-a-third-state.md).</para>
    /// </summary>
    private static MethodInfo Bind(string name)
        => typeof(BcRuntime).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException(
               $"BcRuntime.{name} not found. The #4222 bundle-epoch marker is what the Event "
               + "Subscription virtual table's bundle scoping rests on; if it was renamed or "
               + "removed, this test is asserting nothing and must be updated, not deleted.");

    private static void BeginBundle() => Begin.Invoke(null, null);
    private static void StampFor(Assembly asm) => Stamp.Invoke(null, new object[] { asm });
    private static bool Current(Assembly asm) => (bool)IsCurrent.Invoke(null, new object[] { asm })!;

    [Fact]
    public void AnAssemblyStampedForThePreviousBundle_IsNotCurrent()
    {
        BeginBundle();
        StampFor(A);
        Assert.True(Current(A), "an assembly stamped for the bundle now running must read as current");

        BeginBundle();

        Assert.False(Current(A),
            "after the next bundle begins, the previous bundle's assembly must stop reading as "
            + "current — this is the whole marker #4222 adds, and what scopes 2000000140.");
    }

    [Fact]
    public void AnUnstampedAssembly_ReadsAsCurrent_SoMicrosoftsSubscribersSurvive()
    {
        // The partner of the arm above, and the one that bounds the blast radius. The stamp is
        // written only for bundle and dependency modules, so Base/System Application chunks
        // have no entry. Failing CLOSED here would drop every Microsoft subscriber from the
        // inventory — measured at 3,323 rows on a Base-App-dependent two-bundle run — which is
        // a far larger wrong answer than the single foreign row this issue reports.
        BeginBundle();
        BeginBundle();

        Assert.True(Current(B),
            "an assembly the marker never stamped must read as current; see "
            + "IsCurrentBundleAssembly's fail-open note.");
    }

    [Fact]
    public void ReStampingMovesAnAssemblyToTheNewBundle()
    {
        // A dependency module reused as-is across two bundles is re-noted deliberately (#4100),
        // and must then count as the NEW bundle's. An add-if-absent stamp would leave it
        // pinned to whichever bundle happened to load it first, and its subscribers would
        // vanish from the inventory of every later bundle that legitimately depends on it.
        BeginBundle();
        StampFor(A);

        BeginBundle();
        Assert.False(Current(A), "precondition: the stamp must have gone stale before re-noting");

        StampFor(A);

        Assert.True(Current(A),
            "re-noting an assembly must re-stamp it for the bundle now loading, not skip it "
            + "because the epoch map already holds an entry.");
    }
}
