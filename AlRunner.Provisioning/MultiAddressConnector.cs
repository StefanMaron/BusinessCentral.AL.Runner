// Issue #2926: .NET's default connect did not do what curl does. On a host whose DNS returns
// an AAAA record for the BC artifact CDN and which has no working IPv6 route, curl tried the
// IPv6 address, got `Network is unreachable`, moved on to the A record and downloaded 893 MB.
// tools/DownloadArtifacts stopped at the first address and reported a CDN outage.
//
// This is the "walks all resolved addresses the way curl does" half of the fix. It is a plain
// function over an injected resolver and an injected connect, so the interesting cases (an
// IPv6 address the kernel refuses, an address that black-holes, DNS returning nothing) are
// unit-testable from captured outcomes instead of from whatever the live network happens to be
// doing — a test that reads the real network passes for the wrong reason on a healthy box.
using System.Net;
using System.Net.Sockets;

namespace AlRunner.Provisioning;

public static class MultiAddressConnector
{
    /// <summary>Resolve a host name to candidate addresses, in the order they should be tried.</summary>
    public delegate ValueTask<IPAddress[]> ResolveDelegate(string host, CancellationToken ct);

    /// <summary>Open a stream to one specific address.</summary>
    public delegate ValueTask<Stream> ConnectDelegate(IPAddress address, int port, CancellationToken ct);

    /// <summary>
    /// Per-address connect budget. A black-holed address (firewall dropping SYNs, which is the
    /// common shape of a broken IPv6 path) never returns an error — it just goes quiet — so
    /// without a per-address cap one dead address consumes the caller's entire timeout and the
    /// remaining addresses are never tried. That failure looks exactly like #2926 from outside.
    /// </summary>
    public static readonly TimeSpan DefaultPerAddressTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Try every resolved address in order and return the first connection that comes up.
    /// Throws <see cref="MultiAddressConnectException"/> carrying the per-address outcomes when
    /// none does — the caller reports those instead of collapsing them into one message.
    /// </summary>
    /// <param name="note">
    /// Called when an address fails but a later one succeeds. Without it the fallback is
    /// invisible, and a host with a broken IPv6 route silently pays a connect timeout on every
    /// single connection while looking perfectly healthy.
    /// </param>
    public static async ValueTask<Stream> ConnectAnyAsync(
        string host, int port,
        ResolveDelegate resolve,
        ConnectDelegate connect,
        TimeSpan perAddressTimeout,
        CancellationToken ct,
        Action<string>? note = null)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await resolve(host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MultiAddressConnectException(host, port, Array.Empty<AddressAttempt>(), ex,
                $"could not resolve {host}: {ex.Message}");
        }

        if (addresses.Length == 0)
            throw new MultiAddressConnectException(host, port, Array.Empty<AddressAttempt>(), null,
                $"could not resolve {host}: DNS returned no addresses");

        var attempts = new List<AddressAttempt>(addresses.Length);
        for (var i = 0; i < addresses.Length; i++)
        {
            var address = addresses[i];

            if (ct.IsCancellationRequested)
            {
                // Record the rest as untried rather than as failures. "not tried" and "failed"
                // are different observations and the message must not conflate them.
                for (var j = i; j < addresses.Length; j++) attempts.Add(AddressAttempt.NotTried(addresses[j]));
                break;
            }

            using var perAddress = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perAddress.CancelAfter(perAddressTimeout);
            try
            {
                var stream = await connect(address, port, perAddress.Token).ConfigureAwait(false);
                if (attempts.Count > 0 && note is not null)
                    note($"[provision] {host}: {Describe(attempts)}; connected over {Family(address)} ({address}).");
                return stream;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The per-address budget elapsed, not the caller's token: a timed-out connect.
                attempts.Add(new AddressAttempt(address, Attempted: true, SocketError.TimedOut,
                    $"no response within {perAddressTimeout.TotalSeconds:0.#}s"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempts.Add(AddressAttempt.Failed(address, ex));
            }
        }

        ct.ThrowIfCancellationRequested();

        throw new MultiAddressConnectException(host, port, attempts, null,
            $"could not connect to {host}:{port}: {Describe(attempts)}");
    }

    private static string Describe(IReadOnlyList<AddressAttempt> attempts)
        => string.Join("; ", attempts.Select(a => a.Attempted
            ? $"{a.Address} ({Family(a.Address)}) {a.Error?.ToString() ?? a.Message ?? "failed"}"
            : $"{a.Address} ({Family(a.Address)}) not tried"));

    private static string Family(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

    /// <summary>The real resolver: DNS, in the order the platform returns.</summary>
    public static async ValueTask<IPAddress[]> ResolveWithDnsAsync(string host, CancellationToken ct)
        => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

    /// <summary>
    /// The real connect. One socket per address, created in that address's OWN family rather
    /// than as a dual-mode socket, so each attempt is independent and a failure on one carries
    /// no state into the next.
    /// </summary>
    public static async ValueTask<Stream> ConnectWithSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
