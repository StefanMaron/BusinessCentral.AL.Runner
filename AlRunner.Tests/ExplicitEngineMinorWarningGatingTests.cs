// ExplicitEngineMinorWarningGatingTests — issue #2037.
//
// #2008's DescribeExplicitEngineMinorMismatch/WarnIfExplicitEngineMinorMismatch (see
// EngineMinorMismatchWarningTests.cs) fire whenever this binary's OWN compiled-in engine
// minor differs from the explicitly-selected BC version. That was correct for the
// single-build install it was written for — there was only ever one engine, so a
// mismatched minor really was degraded.
//
// #2027 shipped per-BC-minor engine variants (variants/<build>/, see EngineVariants):
// a packaged install now carries several engines and swaps to whichever one matches the
// SELECTED version at startup. Program.cs called WarnIfExplicitEngineMinorMismatch
// BEFORE that variant swap ran (line 748, swap at 771-801), comparing THIS PROCESS's
// own compiled-in minor — which is irrelevant once a matching variant is about to be
// swapped in. Reported live against the published 2.5.0 package: `--bc-version
// 27.5.46862.53931` printed the KNOWN-DEGRADED warning and then immediately selected
// the correct 27.5 variant and ran clean.
//
// This file pins the decision of WHETHER to call the warning at all, gated on how many
// engine variants this install ships (EngineVariants.Discover(...).Count) — the same
// input Program.cs's variant-selection block already inspects. Once ANY variant is
// shipped, either one matches the selection (the warning's claim becomes false — this
// process is about to swap into the right engine) or none does (the variant-selection
// block itself exits with a sharper, version-naming error before a warning would even
// help) — so the old generic warning has nothing correct left to say. It is only still
// true for a single-build install with no variants/ directory at all, which is the case
// #2008 was filed against and MUST stay reachable.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExplicitEngineMinorWarningGatingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Structural guard on Program.cs's own source: the call site that decides whether
    /// to print WarnIfExplicitEngineMinorMismatch must route through
    /// ShouldWarnExplicitEngineMinorMismatch (which also inspects the shipped-variant
    /// count), not merely `!bcVersionAutoSelected`. A behavioural test on the pure
    /// function alone (the four cases above) would pass equally well whether Program.cs
    /// actually calls it or still gates on the old, narrower condition — reading the
    /// source is the only way to prove the call site itself was fixed, not just that a
    /// correct function exists unused beside it.
    /// </summary>
    [Fact]
    public void ProgramCs_GatesTheWarnCall_OnShouldWarnExplicitEngineMinorMismatch()
    {
        var programSource = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        var callIdx = programSource.IndexOf("WarnIfExplicitEngineMinorMismatch();", StringComparison.Ordinal);
        Assert.True(callIdx >= 0, "WarnIfExplicitEngineMinorMismatch() call not found in Program.cs");

        // The `if` guarding the call must be within a short lookbehind window and must
        // name ShouldWarnExplicitEngineMinorMismatch, not just bcVersionAutoSelected alone.
        var windowStart = Math.Max(0, callIdx - 400);
        var window = programSource[windowStart..callIdx];
        Assert.Contains("ShouldWarnExplicitEngineMinorMismatch", window);
    }

    /// <summary>Negative — the exact #2037 reproduction: an explicit --bc-version
    /// (bcVersionAutoSelected=false) with at least one shipped engine variant. The
    /// warning must NOT fire; the variant-selection block downstream is the sole
    /// authority once any variant is shipped.</summary>
    [Fact]
    public void ShippedVariantsPresent_ExplicitSelection_DoesNotWarn()
    {
        Assert.False(BcArtifacts.ShouldWarnExplicitEngineMinorMismatch(
            bcVersionAutoSelected: false, shippedVariantCount: 1));
    }

    /// <summary>Same negative, several variants shipped (the real packaged-install
    /// shape — one per bc-versions.txt entry) — count above 1 must not change the
    /// answer.</summary>
    [Fact]
    public void MultipleShippedVariants_ExplicitSelection_DoesNotWarn()
    {
        Assert.False(BcArtifacts.ShouldWarnExplicitEngineMinorMismatch(
            bcVersionAutoSelected: false, shippedVariantCount: 3));
    }

    /// <summary>Positive — the case the warning was written for (#2008): a single-build
    /// install with NO variants/ directory at all (EngineVariants.Discover returns
    /// empty), explicit --bc-version/--artifact-path. Must stay loud.</summary>
    [Fact]
    public void NoShippedVariants_ExplicitSelection_StillWarns()
    {
        Assert.True(BcArtifacts.ShouldWarnExplicitEngineMinorMismatch(
            bcVersionAutoSelected: false, shippedVariantCount: 0));
    }

    /// <summary>Negative control: the auto-select default path already prints its own,
    /// richer equivalent warning in Program.cs — this must never double-warn regardless
    /// of variant count.</summary>
    [Fact]
    public void AutoSelected_NeverWarns_RegardlessOfVariantCount()
    {
        Assert.False(BcArtifacts.ShouldWarnExplicitEngineMinorMismatch(
            bcVersionAutoSelected: true, shippedVariantCount: 0));
        Assert.False(BcArtifacts.ShouldWarnExplicitEngineMinorMismatch(
            bcVersionAutoSelected: true, shippedVariantCount: 2));
    }
}
