// Issue #2926: the two version-selection notices below are the downstream readers of
// ArtifactDownloader.VersionExists, and both used to state as fact something that boolean
// cannot establish.
//
// VersionExists returns false for "the CDN answered 404" AND for "the probe failed" — a DNS
// failure, a five-second timeout, or the address-family problem this issue was filed about.
// BcArtifacts.ResolveProvisionTargetCore treats false as "not published" and walks down a
// tier, and these two lines then told the user "BC x.y.z is not published on the CDN" and
// "BC x.y.x is not cached and not available from the CDN". On a transient network fault both
// sentences are false, and both send the reader to check Microsoft's publishing rather than
// their own network — the same wrong turn #2926 was filed for one layer down.
//
// The signal cannot carry the third state without changing ResolveProvisionTargetCore's
// contract, which is tracked separately. What these notices can do, and now do, is state the
// weaker thing that is true either way: the artifact could not be obtained. When the cause was
// a network failure, ArtifactDownloader has already printed the classified observation above.
//
// Extracted from the inline switch in Program.cs so the claim is directly assertable; the
// switch, its deferred-line ordering and its comments are untouched.
namespace AlRunner;

internal static partial class ProgramSupport
{
    /// <summary>
    /// "cdn-minor" tier: the engine's exact build could not be obtained, so provisioning falls
    /// back to the latest build of the engine's own minor.
    /// </summary>
    internal static string CdnMinorProvisionNotice(string engineVersion, string engineMajorMinor)
        => $"[bc] no --bc-version given and BC {engineVersion} could not be obtained from " +
           $"the CDN — provisioning the latest {engineMajorMinor}.x instead (still this binary's own " +
           $"engine minor). Build-level skew within a minor can still fail to load " +
           $"Microsoft.Dynamics.Nav.CodeAnalysis. Fix with: al-runner provision --bc-version {engineVersion}";

    /// <summary>
    /// The "cdn-exact-undetermined"/"cdn-minor-undetermined" tiers — issue #2981. The CDN was
    /// never reached, so nothing here may state what it does or does not hold; the whole point
    /// of the tier is that the runner declined to demote on a question that went unanswered.
    /// Deliberately NOT a KNOWN-DEGRADED warning: the target is still the one version
    /// selection wants, and if the fault persists the download that follows fails with
    /// NetworkDiagnosis's classified observation rather than quietly running on the wrong
    /// artifact.
    /// </summary>
    /// <param name="target">What provisioning will now fetch — the held tier's version or prefix.</param>
    internal static string UndeterminedProbeNotice(string engineVersion, string target)
        => $"[bc] no --bc-version given and the CDN could not be reached to check BC {engineVersion} " +
           $"— see the observation above. Keeping BC {target} as the provisioning target rather than " +
           $"falling back to an older minor: a probe that went unanswered is not evidence the build " +
           $"was withdrawn. If the fault persists the download below will fail and name it.";

    /// <summary>
    /// The two tiers that fell back to the bare major: "major-fallback" (the CDN answered no for
    /// both the exact build and the engine's minor) and "major-fallback-offline" (cache only).
    /// Either one arms the post-selection minor-mismatch warning (#4691).
    /// </summary>
    internal static bool IsMajorFallbackTier(string tier)
        => tier is "major-fallback" or "major-fallback-offline";

    /// <summary>
    /// "major-fallback" tier: neither the engine's exact build nor its minor could be obtained
    /// from cache or the CDN. Reaching this tier requires both CDN probes to have answered (#2981).
    /// No degradation claim here: the landed minor is not known yet, and a CI-measured one is not
    /// degraded (#4691). BcArtifacts.DescribeDefaultFallbackMinorMismatch warns after selection.
    /// Printed immediately, so a selection failure that follows is explained.
    /// </summary>
    internal static string MajorFallbackWarning(string engineVersion, string engineMajorMinor, string engineMajor)
        => $"[bc] BC {engineMajorMinor}.x is not cached and could not be obtained from the CDN " +
           $"(this binary's engine was built for {engineVersion}) — falling back to the latest {engineMajor}.x. " +
           $"To run the engine's own minor: al-runner provision --bc-version {engineMajorMinor}";

    /// <summary>
    /// "major-fallback-offline" tier: <see cref="MajorFallbackWarning"/> without a network step,
    /// so it speaks only to the cache and never mentions the CDN.
    /// </summary>
    internal static string MajorFallbackOfflineNotice(string engineVersion, string engineMajorMinor, string engineMajor)
        => $"[bc] no cached BC {engineMajorMinor}.x (this binary's engine was built for {engineVersion}) — " +
           $"falling back to the latest cached {engineMajor}.x. To run the engine's own minor: " +
           $"al-runner provision --bc-version {engineMajorMinor}";
}
