// Issue #2981: the three-state answer ArtifactDownloader's CDN probes were returning as a
// bool (and as a nullable string). See docs/provisioning-tiers.md#undetermined for the full
// argument; the short version, which is the part that would change what you TYPE here:
//
//   "the CDN said no" and "we could not ask the CDN" are different answers, and the second
//   one must never be widened into the first. Collapsing them is what let a five-second
//   network blip select a KNOWN-DEGRADED BC artifact.
using System.Diagnostics.CodeAnalysis;

namespace AlRunner.Provisioning;

/// <summary>
/// The answer to "is this exact build published on the CDN?" — three states, not two.
/// <see cref="Undetermined"/> carries the <see cref="NetworkFailureKind"/> that
/// <see cref="NetworkDiagnosis"/> already computes, so a caller can say WHY it could not ask
/// rather than inventing a reason.
/// </summary>
public readonly record struct CdnProbeResult
{
    private CdnProbeResult(bool published, bool undetermined, NetworkFailureKind? kind)
    {
        IsPublished = published;
        IsUndetermined = undetermined;
        FailureKind = kind;
    }

    /// <summary>The CDN answered, and the answer was yes.</summary>
    public static CdnProbeResult Published { get; } = new(published: true, undetermined: false, kind: null);

    /// <summary>The CDN answered, and the answer was no. Only a response licenses this.</summary>
    public static CdnProbeResult NotPublished { get; } = new(published: false, undetermined: false, kind: null);

    /// <summary>The probe never got an answer. Not a "no" — a missing answer.</summary>
    public static CdnProbeResult Undetermined(NetworkFailureKind kind)
        => new(published: false, undetermined: true, kind: kind);

    public bool IsPublished { get; }
    public bool IsUndetermined { get; }

    /// <summary>Non-null exactly when <see cref="IsUndetermined"/> — what was observed.</summary>
    public NetworkFailureKind? FailureKind { get; }
}

/// <summary>
/// The answer to "what is the latest build matching this prefix?". Same three states, with the
/// resolved version on the positive one. <see cref="NoMatch"/> means the index was fetched and
/// read and contained nothing matching; <see cref="Undetermined"/> means the index was never
/// read at all, which the old <c>string?</c> return could not distinguish from it.
/// </summary>
public readonly record struct CdnPrefixResult
{
    private CdnPrefixResult(string? version, bool undetermined, NetworkFailureKind? kind)
    {
        Version = version;
        IsUndetermined = undetermined;
        FailureKind = kind;
    }

    public static CdnPrefixResult Resolved(string version)
        => new(version ?? throw new ArgumentNullException(nameof(version)), undetermined: false, kind: null);

    /// <summary>The index was read and nothing matched the prefix.</summary>
    public static CdnPrefixResult NoMatch { get; } = new(version: null, undetermined: false, kind: null);

    /// <summary>The index could not be fetched or parsed. No statement about its contents.</summary>
    public static CdnPrefixResult Undetermined(NetworkFailureKind kind)
        => new(version: null, undetermined: true, kind: kind);

    public string? Version { get; }

    [MemberNotNullWhen(true, nameof(Version))]
    public bool IsResolved => Version is not null;

    public bool IsUndetermined { get; }
    public NetworkFailureKind? FailureKind { get; }
}
