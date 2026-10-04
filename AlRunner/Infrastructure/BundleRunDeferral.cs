// BundleRunDeferral — whether the bundle loop loads every bundle before any runs tests (#4931, #5318).
//
// A run that can compile a bundle twice must not run tests in the pass the second compile throws away.
// --tdd's re-run (#5037) is that run: a member generated into another source bundle sends the cycle round
// again, and under --jobs a test codeunit a discarded pass ran is already claimed (UnitClaimQueue), so the
// second pass skips it. docs/jobs-unit-claiming.md § Source dependencies.

namespace AlRunner.Infrastructure;

internal static class BundleRunDeferral
{
    /// <summary>
    /// True when the tests of every bundle run after all of them have loaded: a bundled run that is not
    /// --affected, and either has more than one bundle to RUN (unless AL_RUNNER_SEQUENTIAL_BUNDLES=1, which
    /// keeps one pass per bundle), or is --tdd with more than one folder LISTED. The second arm is a --jobs
    /// worker that runs one bundle and only compiles a dependency-only folder (#5295): a generated member
    /// can still send it round again, and the sequential knob orders several bundles, so it has no say over one.
    /// </summary>
    public static bool Defers(bool bundledMode, bool watchAffected, bool sequentialBundles,
        int runBundleCount, int listedBundleCount, bool tddMode)
        => bundledMode && !watchAffected
           && (runBundleCount > 1 ? !sequentialBundles : tddMode && listedBundleCount > 1);
}
