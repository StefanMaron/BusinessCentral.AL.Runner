// Issue #2926: the nine `new HttpClient { Timeout = ... }` sites in ArtifactDownloader each
// got the stock connect behaviour, so a host with a broken IPv6 route failed at every one of
// them and the tool blamed the CDN. One factory now owns connect behaviour for all of them —
// two code paths reaching the same CDN should not be able to disagree about how to reach it.
using System.Collections.Concurrent;
using System.Net.Http;

namespace AlRunner.Provisioning;

public static class ArtifactHttpClient
{
    /// <summary>
    /// Escape hatch: set to 1 to use .NET's stock connect instead of the address-walking one.
    /// Here so a bug in the walk can be ruled out without a rebuild, not because it is expected.
    /// </summary>
    public const string DisableEnvVar = "AL_RUNNER_DISABLE_MULTI_ADDRESS_CONNECT";

    // A host with no IPv6 route fails the first address on EVERY connection, and this pool
    // opens many. Report the fallback once per distinct observation, or the useful line is
    // buried under hundreds of copies of itself.
    private static readonly ConcurrentDictionary<string, byte> Reported = new();

    /// <summary>An <see cref="HttpClient"/> that tries every resolved address, the way curl does.</summary>
    public static HttpClient Create(TimeSpan? timeout = null, Action<string>? log = null)
    {
        if (Environment.GetEnvironmentVariable(DisableEnvVar) == "1")
        {
            var stock = new HttpClient();
            if (timeout is { } st) stock.Timeout = st;
            return stock;
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (context, ct) => MultiAddressConnector.ConnectAnyAsync(
                context.DnsEndPoint.Host,
                context.DnsEndPoint.Port,
                MultiAddressConnector.ResolveWithDnsAsync,
                MultiAddressConnector.ConnectWithSocketAsync,
                MultiAddressConnector.DefaultPerAddressTimeout,
                ct,
                note: log is null ? null : m => NoteOnce(log, m)),
        };

        var client = new HttpClient(handler, disposeHandler: true);
        if (timeout is { } t) client.Timeout = t;
        return client;
    }

    private static void NoteOnce(Action<string> log, string message)
    {
        if (Reported.TryAdd(message, 0)) log(message);
    }

    /// <summary>Test seam: forget which fallback notes have already been reported.</summary>
    internal static void ResetNoteDedupeForTests() => Reported.Clear();
}
