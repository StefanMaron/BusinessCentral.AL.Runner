// ReexecExplanationVisibilityTests — issue #2034.
//
// PR #2026 added NclShadowRuntime: when Ncl.dll is not shipped in the install (the
// packaged-tool default since #2023/#2026), the runner builds a shadow runtime
// directory that has it and re-execs into it. The explanatory line for that ("[Cecil]
// Ncl.dll not shipped in this install — re-execing into a shadow runtime dir that has
// it") was tagged `[Cecil]`, which Log's component filter suppresses by default (see
// LogUserFacingTagsTests' negative control pinning `[Cecil] rewriting NavDialog` as
// suppressed) — the exact same class of bug the `[bc]` swallow was (see Log.cs's own
// comment on that history). Confirmed on a real clean install: the shadow dir was
// built, the re-exec fired, and the explanatory line never reached stderr.
//
// The fix retags the process-relaunch explanations with `[reexec]`, an exempted tag
// (see LogUserFacingTagsTests), leaving the ~280 other `[Cecil]`-tagged lines in
// NclCecilRewrite.cs (per-method IL-rewrite diagnostics) suppressed exactly as Log.cs's
// own docstring says they should be — `[Cecil]` is its canonical example of an internal
// diagnostic tag.
//
// This file is the structural half: LogUserFacingTagsTests proves the FILTER exempts
// `[reexec]`; this file proves the actual re-exec call sites in Program.cs and
// NclShadowRuntime.cs were retagged to use it, not merely that a correctly-exempted tag
// exists unused. A test that only spawned the runner and grepped stdout/stderr for a
// hardcoded fixed BC version would be far slower (real artifact provisioning) and no
// more conclusive than reading the source directly — see FindBucketRootDedupeTests.cs
// and TddModeTests.ComputeAlCacheKey_HashesTheTddFlag for the same technique already
// established in this suite.
using Xunit;

namespace AlRunner.Tests;

public sealed class ReexecExplanationVisibilityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>Acceptance #1 (source half): the Ncl-shadow / variant-swap re-exec
    /// explanation in Program.cs — the exact line named in the issue — must be tagged
    /// `[reexec]`, not `[Cecil]`.</summary>
    [Fact]
    public void ProgramCs_NclShadowReexecExplanation_UsesReexecTag()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        Assert.Contains(
            "[reexec] Ncl.dll not shipped in this install — re-execing into a shadow runtime dir that has it",
            src);
        Assert.Contains(
            "[reexec] Re-execing into a shadow runtime dir with the matching BC-minor engine variant",
            src);
        Assert.DoesNotContain(
            "[Cecil] Ncl.dll not shipped in this install", src);
    }

    /// <summary>Acceptance #3 (audit): the OTHER re-exec explanation in Program.cs — the
    /// "fresh Cecil rewrite done, re-execing for a clean Ncl load" line — is the same
    /// class of silently-swallowed line and must also surface.</summary>
    [Fact]
    public void ProgramCs_FreshRewriteReexecExplanation_UsesReexecTag()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Program.cs"));
        Assert.Contains("[reexec] Fresh rewrite done — re-execing for a clean Ncl load", src);
    }

    /// <summary>Acceptance #3 (audit): NclShadowRuntime's own WARN lines (symlink
    /// fallback, stale-shadow-dir prune failure) report genuinely unexpected conditions
    /// during the shadow-dir build, not routine per-method rewrite noise — they were
    /// swallowed by the same `[Cecil]` filter and must also surface.</summary>
    [Fact]
    public void NclShadowRuntime_WarnLines_UseReexecTag()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclShadowRuntime.cs"));
        Assert.Contains("[reexec] Symlink for", src);
        Assert.Contains("[reexec] WARN: failed to prune stale shadow dir", src);
    }

    /// <summary>Negative control: the routine, high-volume per-method IL-rewrite
    /// diagnostics in NclCecilRewrite.cs must stay on `[Cecil]` (suppressed by default)
    /// — this issue is about the re-exec explanation specifically, not a licence to
    /// surface every internal Cecil diagnostic.</summary>
    [Fact]
    public void NclCecilRewrite_RoutineDiagnostics_StillUseCecilTag()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Infrastructure", "NclCecilRewrite.cs"));
        Assert.Contains("[Cecil] Rewriting", src);
        Assert.DoesNotContain("[reexec]", src);
    }
}
