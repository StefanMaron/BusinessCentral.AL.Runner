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
    /// "major-fallback" tier: neither the engine's exact build nor its minor could be obtained
    /// from cache or the CDN. Genuinely degraded — but not necessarily Microsoft's doing.
    /// </summary>
    internal static string MajorFallbackWarning(string engineVersion, string engineMajorMinor, string engineMajor)
        => $"[bc] warning: BC {engineMajorMinor}.x is not cached and could not be obtained " +
           $"from the CDN — this binary's engine was built for {engineVersion}, so a different minor is " +
           $"a KNOWN-DEGRADED configuration (measured: dozens of extra failures from engine/artifact " +
           $"minor skew). Falling back to the latest {engineMajor}.x. Fix with: al-runner provision " +
           $"--bc-version {engineMajorMinor}";
}
