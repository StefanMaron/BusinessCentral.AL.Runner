// Issue #2981. ArtifactDownloader.VersionExists answered a THREE-state question with a bool:
// the CDN said 404 (not published), the CDN said 200 (published), and the probe never got an
// answer at all (DNS failed, the connect timed out, this host has no route). The third
// collapsed into `false`, and BcArtifacts.ResolveProvisionTargetCore reads `false` as the
// first — so a five-second network blip walked a user from `cdn-exact` down to
// `major-fallback`, a configuration that file's own comment calls "the one genuinely degraded
// outcome" and #2020 measured at dozens of extra test failures from engine/artifact skew.
//
// #2926 fixed the reporting half: the notices stopped stating the thing the bool cannot
// support. What was left is that the runner still BEHAVED identically in both cases. These
// tests pin the behaviour.
//
// The design call, argued at the ResolveProvisionTargetCore call site: an undetermined probe
// never demotes a tier. Demotion is the only branch that can silently select a KNOWN-DEGRADED
// artifact, and it is the branch an unanswered question has no licence to take.
//
// None of these tests touch the network. Every probe result is injected, so the verdict is a
// property of the resolver and not of this box's connectivity.
using AlRunner.Infrastructure;
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

public sealed class UndeterminedCdnProbeTests : IDisposable
{
    private readonly string _root;
    private static readonly Version Engine = new("28.1.49838.50794");

    public UndeterminedCdnProbeTests()
    {
        _root = TestScratch.Dir("al-runner-undetermined-probe");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------------------
    // The three states are distinguishable in the type system, not collapsed to bool.
    // ---------------------------------------------------------------------------

    [Fact]
    public void ProbeResult_DistinguishesAllThreeStates()
    {
        var published = CdnProbeResult.Published;
        var notPublished = CdnProbeResult.NotPublished;
        var undetermined = CdnProbeResult.Undetermined(NetworkFailureKind.Timeout);

        Assert.True(published.IsPublished);
        Assert.False(published.IsUndetermined);

        Assert.False(notPublished.IsPublished);
        Assert.False(notPublished.IsUndetermined);

        Assert.False(undetermined.IsPublished);
        Assert.True(undetermined.IsUndetermined);

        // The kind #2926 already computes is carried, not discarded — that is what lets a
        // caller say WHY it could not ask.
        Assert.Equal(NetworkFailureKind.Timeout, undetermined.FailureKind);
        Assert.Null(published.FailureKind);
        Assert.Null(notPublished.FailureKind);

        // NotPublished and Undetermined must not compare equal. Collapsing them is the bug.
        Assert.NotEqual(notPublished, undetermined);
    }

    // ---------------------------------------------------------------------------
    // THE #2981 proving case, both directions.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The exact-build probe could not be answered. The engine's exact build must still be
    /// the target: an unanswered question is not a 404, and demoting on it is what took a
    /// user from `cdn-exact` to `major-fallback` on a five-second blip.
    /// </summary>
    [Fact]
    public void EmptyCache_ExactProbeUndetermined_KeepsTheEngineExactBuild_NeverMajorFallback()
    {
        var result = BcArtifacts.ResolveProvisionTargetCore(
            Engine, _root,
            cdnHasExactVersion: _ => CdnProbeResult.Undetermined(NetworkFailureKind.Timeout),
            cdnResolvePrefix: p => throw new InvalidOperationException(
                $"must not fall through to prefix resolution when the exact probe was never answered (asked for '{p}')"),
            out var tier);

        Assert.Equal("28.1.49838.50794", result);
        Assert.Equal("cdn-exact-undetermined", tier);

        // ...and specifically NOT the degraded outcome the bool produced.
        Assert.NotEqual("major-fallback", tier);
        Assert.NotEqual("28", result);
    }

    /// <summary>
    /// The negative direction, and the one that keeps this from being a rubber stamp: a
    /// GENUINE 404 must still demote exactly as before. #2010 (Microsoft withdrew a build) is
    /// a real scenario and the fix must not blunt it.
    /// </summary>
    [Fact]
    public void EmptyCache_ExactProbeSaysNotPublished_StillDemotes()
    {
        var result = BcArtifacts.ResolveProvisionTargetCore(
            Engine, _root,
            cdnHasExactVersion: _ => CdnProbeResult.NotPublished,
            cdnResolvePrefix: p => p == "28.1" ? CdnPrefixResult.Resolved("28.1.55555.66666") : CdnPrefixResult.NoMatch,
            out var tier);

        Assert.Equal("28.1.55555.66666", result);
        Assert.Equal("cdn-minor", tier);
    }

    /// <summary>
    /// A 404 on the exact build AND an unanswerable prefix resolution. The 404 licenses the
    /// first demotion; the unanswered index fetch does not license the second. Target the
    /// engine's own minor prefix — the tier the probe was asking about — rather than the bare
    /// major, which is the KNOWN-DEGRADED one.
    /// </summary>
    [Fact]
    public void EmptyCache_ExactNotPublished_PrefixUndetermined_StopsAtTheEngineMinor()
    {
        var result = BcArtifacts.ResolveProvisionTargetCore(
            Engine, _root,
            cdnHasExactVersion: _ => CdnProbeResult.NotPublished,
            cdnResolvePrefix: _ => CdnPrefixResult.Undetermined(NetworkFailureKind.NameResolution),
            out var tier);

        Assert.Equal("28.1", result);
        Assert.Equal("cdn-minor-undetermined", tier);
        Assert.NotEqual("28", result);
    }

    /// <summary>
    /// Both probes answered, and both said no. This is the only route to `major-fallback`
    /// left, and it must still work — the tier exists for #2010 and removing it would be a
    /// different bug.
    /// </summary>
    [Fact]
    public void EmptyCache_BothProbesAnsweredNo_StillFallsBackToMajor()
    {
        var result = BcArtifacts.ResolveProvisionTargetCore(
            Engine, _root,
            cdnHasExactVersion: _ => CdnProbeResult.NotPublished,
            cdnResolvePrefix: _ => CdnPrefixResult.NoMatch,
            out var tier);

        Assert.Equal("28", result);
        Assert.Equal("major-fallback", tier);
    }

    /// <summary>
    /// An undetermined probe must not defeat the cache. A cached exact build wins without the
    /// CDN being consulted at all, exactly as before — the fix must not make a working offline
    /// run depend on a reachable CDN.
    /// </summary>
    [Fact]
    public void CachedExactBuild_StillWinsWithoutProbingAtAll()
    {
        Directory.CreateDirectory(Path.Combine(_root, "28.1.49838.50794"));

        var result = BcArtifacts.ResolveProvisionTargetCore(
            Engine, _root,
            cdnHasExactVersion: _ => throw new InvalidOperationException("must not probe the CDN when already cached"),
            cdnResolvePrefix: _ => throw new InvalidOperationException("must not probe the CDN when already cached"),
            out var tier);

        Assert.Equal("28.1.49838.50794", result);
        Assert.Equal("cached-exact", tier);
    }

    // ---------------------------------------------------------------------------
    // The probe retries once before answering Undetermined (#2981's suggested shape 2).
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The reproducer in #2981 was intermittent — 4 of 15 TCP connects black-holed while curl
    /// succeeded 5/5 moments later — so a single retry clears the common case outright. Proves
    /// the retry actually re-probes and that the SECOND answer is the one returned.
    /// </summary>
    [Fact]
    public void ProbeWithRetry_TransientFailureThenSuccess_AnswersPublished()
    {
        var attempts = 0;
        var result = ArtifactDownloader.ProbeWithRetry(() =>
        {
            attempts++;
            return attempts == 1
                ? CdnProbeResult.Undetermined(NetworkFailureKind.Timeout)
                : CdnProbeResult.Published;
        });

        Assert.Equal(2, attempts);
        Assert.True(result.IsPublished);
    }

    /// <summary>
    /// A definite answer is never retried — a 404 is a fact, and re-asking would double the
    /// cost of the common "this build was withdrawn" path for nothing.
    /// </summary>
    [Fact]
    public void ProbeWithRetry_DefiniteAnswer_IsNotRetried()
    {
        var attempts = 0;
        var result = ArtifactDownloader.ProbeWithRetry(() => { attempts++; return CdnProbeResult.NotPublished; });

        Assert.Equal(1, attempts);
        Assert.False(result.IsPublished);
        Assert.False(result.IsUndetermined);
    }

    /// <summary>
    /// A persistent fault stays Undetermined and carries the kind through — the retry must not
    /// launder a real outage into a definite "not published".
    /// </summary>
    [Fact]
    public void ProbeWithRetry_PersistentFailure_StaysUndetermined_AndKeepsTheKind()
    {
        var attempts = 0;
        var result = ArtifactDownloader.ProbeWithRetry(() =>
        {
            attempts++;
            return CdnProbeResult.Undetermined(NetworkFailureKind.NoRouteToAddress);
        });

        Assert.Equal(2, attempts);
        Assert.True(result.IsUndetermined);
        Assert.False(result.IsPublished);
        Assert.Equal(NetworkFailureKind.NoRouteToAddress, result.FailureKind);
    }

    // ---------------------------------------------------------------------------
    // The user-facing notice for an undetermined tier says which of the two happened.
    // #2981's shape 3: "the tier name should distinguish 'the CDN says no' from 'we could
    // not ask', so the warning text can stop hedging."
    // ---------------------------------------------------------------------------

    [Fact]
    public void UndeterminedNotice_NamesTheNetworkFailure_AndDoesNotBlameMicrosoft()
    {
        var notice = AlRunner.ProgramSupport.UndeterminedProbeNotice("28.1.49838.50794", "28.1.49838.50794");

        // It must not repeat #2926's defect in a new spelling.
        Assert.DoesNotContain("is not published", notice);
        Assert.DoesNotContain("not available", notice);
        // It must say plainly that the question went unanswered...
        Assert.Contains("could not be reached", notice);
        // ...and that the runner deliberately did NOT demote because of it, which is the whole
        // behavioural change and the thing a reader needs to know to interpret a later failure.
        Assert.Contains("28.1.49838.50794", notice);
        Assert.DoesNotContain("KNOWN-DEGRADED", notice);
    }

    /// <summary>
    /// The mirror case: a tier that demoted on a real 404 must still carry the degraded
    /// warning. Proving the notices did not all become reassuring.
    /// </summary>
    [Fact]
    public void MajorFallbackWarning_IsStillLoudlyDegraded()
    {
        var notice = AlRunner.ProgramSupport.MajorFallbackWarning("28.1.49838.50794", "28.1", "28");
        Assert.Contains("KNOWN-DEGRADED", notice);
        Assert.Contains("warning:", notice);
    }
}
