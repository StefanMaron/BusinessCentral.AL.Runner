// EngineMinorMismatchWarningTests — proves BcArtifacts.DescribeExplicitEngineMinorMismatch,
// the pure core behind #2008's fix.
//
// #2008's root cause: every shipped al-runner binary's engine (Ncl.dll etc.) is compiled
// against BC 28.1 (bc-tests.yml's resolve-versions job hardcodes 28.1 as `required-version`
// for release/pack jobs). Ncl.dll's own AssemblyVersion is always MAJOR.0.0.0 (28.0.0.0 for
// every 28.x build), so VerifyEngineConsistency — which only compares Major — cannot see a
// same-major, different-MINOR selection. The reporter ran the v2.3.1 binary (engine built
// for 28.1.49838.50794) with `--bc-version 28.3` explicitly. That silently ran a BC-28.1
// engine against BC-28.3 artifacts and threw NullReferenceException deep inside BC's own
// FieldDataProvider ctor (NavGlobal.get_SystemTenant -> get_NCLMetadata) — confirmed by
// reproducing it against both the actual published v2.3.1 tool and a from-source rebuild
// pinned to the exact same engine version, and confirming a matched-minor rebuild (engine
// built for 28.3, run against 28.3) is clean.
//
// The runner ALREADY had an equivalent warning for the auto-select default path (no
// --bc-version/--artifact-path given) in Program.cs — this test proves the NEW check that
// makes it reachable from an EXPLICIT --bc-version/--artifact-path selection too, which is
// exactly the case that reproduced #2008 and went unwarned.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class EngineMinorMismatchWarningTests
{
    [Fact]
    public void DescribeExplicitEngineMinorMismatch_DifferentMinor_WarnsWithBothVersions()
    {
        var built = new Version("28.1.49838.50794");
        var selected = new Version("28.3.52162.53954");

        var message = BcArtifacts.DescribeExplicitEngineMinorMismatch(built, selected);

        Assert.NotNull(message);
        Assert.Contains("28.1.49838.50794", message);
        Assert.Contains("28.3.52162.53954", message);
        Assert.Contains("explicitly selected", message);
        Assert.Contains("KNOWN-DEGRADED", message);
        Assert.Contains("#2008", message);
        // Names the fix, not just the symptom, so the message is actionable.
        Assert.Contains("-p:_BCVersion=28.3.52162.53954", message);
    }

    [Fact]
    public void DescribeExplicitEngineMinorMismatch_SameMajorMinor_ExactBuildMatch_ReturnsNull()
    {
        var built = new Version("28.1.49838.50794");
        var selected = new Version("28.1.49838.50794");

        Assert.Null(BcArtifacts.DescribeExplicitEngineMinorMismatch(built, selected));
    }

    [Fact]
    public void DescribeExplicitEngineMinorMismatch_SameMinor_DifferentPatchBuild_TreatedAsTolerated()
    {
        // Patch-level skew within one minor (28.1.49838.53910 vs 28.1.49838.50794) is the
        // SAME tolerance VerifyEngineConsistency already applies for Major-only comparison —
        // this extends it one level deeper (Major.Minor) without narrowing what was already
        // accepted.
        var built = new Version("28.1.49838.50794");
        var selected = new Version("28.1.49838.53910");

        Assert.Null(BcArtifacts.DescribeExplicitEngineMinorMismatch(built, selected));
    }

    [Fact]
    public void DescribeExplicitEngineMinorMismatch_DifferentMajor_StillWarns()
    {
        // VerifyEngineConsistency throws before this ever runs for a real Major mismatch in
        // production, but the pure function itself must not silently approve one either —
        // Major is also part of "Major.Minor", so this is a genuine proving case, not
        // reachable-in-practice noise.
        var built = new Version("27.5.46862.53931");
        var selected = new Version("28.1.49838.53910");

        var message = BcArtifacts.DescribeExplicitEngineMinorMismatch(built, selected);

        Assert.NotNull(message);
        Assert.Contains("27.5.46862.53931", message);
        Assert.Contains("28.1.49838.53910", message);
    }

    [Fact]
    public void DescribeExplicitEngineMinorMismatch_UnstampedOlderBinary_ReturnsNull()
    {
        // BcEngineVersion is missing on a binary built before this attribute existed —
        // nothing to compare against, so this must not fabricate a warning.
        Assert.Null(BcArtifacts.DescribeExplicitEngineMinorMismatch(builtVersion: null, new Version("28.3.52162.53954")));
    }
}
