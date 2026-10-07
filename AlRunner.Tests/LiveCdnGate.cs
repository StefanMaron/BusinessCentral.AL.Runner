// LiveCdnGate — what a test that genuinely needs the live BC artifact CDN does when it cannot be
// reached (#5420). Such a test spawned the runner with a 180 s cap and, on an unreachable or slow
// CDN, failed minutes later with "did not exit within 180s ... this will hang".
//
// The CDN answering is a precondition of those tests, not a claim they make, so it is probed once
// per process, with its own short cap, before the first spawn:
//   * reachable       -> run.
//   * unreachable, dev box -> a visible skip naming what was seen (an offline laptop is not a defect).
//   * unreachable, CI -> a fast failure naming the same thing. A CI leg provisions from this CDN
//     before the suite starts, so it is never a legitimate skip there, and a leg whose live-CDN
//     tests all skipped would be green having measured nothing (TestArtifacts.CiMissingArtifactsMessage
//     makes the same argument for the artifact cache).
// A fact that only needs to RESOLVE a version does not use this: it runs against an offline CDN
// (ProvisionExplicitModesTests.OfflineCdnEnvironment) and never depends on the real one.
using System.Net.Http;
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

internal static class LiveCdnGate
{
    /// <summary>The cap on the reachability probe itself, kept far below a spawn's 180 s.</summary>
    internal const int ProbeTimeoutSeconds = 15;

    private static readonly Lazy<string?> ProbeFailure = new(ProbeOnce);

    /// <summary>Throws a skip or a failure when the live CDN is not reachable; returns when it is.</summary>
    internal static void Require() => Require(ProbeFailure.Value, TestArtifacts.RunningOnCi);

    internal static void Require(string? probeFailure, bool runningOnCi)
    {
        if (probeFailure == null) return;

        var what = $"the live BC artifact CDN ({ArtifactDownloader.CdnBase}) did not answer a HEAD for its " +
            $"index within {ProbeTimeoutSeconds}s: {probeFailure}";
        if (runningOnCi)
            Assert.Fail("this test needs the live CDN, which a CI leg provisions from by construction, so an " +
                "unreachable CDN is a failure and not a skip: " + what);
        throw new SkipException(what);
    }

    // Any HTTP response, a 404 included, means the CDN is reachable; only a transport failure or a
    // timeout is "unreachable". Returns null for reachable, the observation otherwise.
    private static string? ProbeOnce()
    {
        try
        {
            using var http = ArtifactHttpClient.Create(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Head, $"{ArtifactDownloader.CdnBase}/indexes/w1.json");
            using var response = http.Send(request);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
