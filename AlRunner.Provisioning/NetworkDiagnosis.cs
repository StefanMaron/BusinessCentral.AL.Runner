// Issue #2926: every network failure in ArtifactDownloader used to be reported as
//
//     Error: could not reach the BC artifact CDN for 28.1.49838.53910 (platform):
//            Network is unreachable (bcartifacts-...azurefd.net:443)
//
// which reads as "Azure is down" or "this build was never published". Neither was true —
// the host had an AAAA record and no IPv6 route, and an IPv4 address of the same CDN
// answered a ranged GET seconds later. The reporter spent several minutes checking the CDN
// by hand before thinking to look at the address family.
//
// The defect is not the download. It is that one message was emitted for a set of
// observations that mean very different things, and it named a cause the observation did
// not support. This file separates them:
//
//   * we got an HTTP status back        -> the CDN answered; ONLY here may we talk about
//                                          the CDN's own health
//   * DNS said no                       -> a name-resolution fact
//   * the local kernel refused a route  -> a fact about THIS host's routing table
//   * something sent a RST              -> a fact about the far end
//   * nothing answered in time          -> tells us nothing about where the fault is
//   * anything else                     -> report what was seen, name no cause
//
// The rule the message text has to obey: a claim about the remote service requires bytes
// from the remote service. `.claude/rules/github-access.md` records the same class of
// defect one layer up (an unauthenticated 404 read as "this does not exist"), and
// `.claude/rules/loud-failures.md` is the general form — a signal must not be reported as
// a stronger conclusion than it supports.
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AlRunner.Provisioning;

/// <summary>
/// What was actually observed when an HTTP request failed. Ordered from "we know the most"
/// to "we know the least"; the message text for each kind may only claim what the kind
/// itself establishes.
/// </summary>
public enum NetworkFailureKind
{
    /// <summary>The server sent an HTTP response. Its status is a fact about the server.</summary>
    ServerResponded,

    /// <summary>The host name did not resolve. A DNS fact, not a fact about the server.</summary>
    NameResolution,

    /// <summary>
    /// A connect attempt was refused by the local routing stack (ENETUNREACH / EHOSTUNREACH).
    /// This is the local kernel saying it has no route — a fact about THIS host.
    /// </summary>
    NoRouteToAddress,

    /// <summary>Something at the address answered with a RST. A fact about the far end.</summary>
    ConnectionRefused,

    /// <summary>
    /// Nothing answered inside the time budget. Consistent with a transient local fault, a
    /// firewall, packet loss, or an unhealthy server — it does not distinguish between them.
    /// </summary>
    Timeout,

    /// <summary>The TCP connection came up but the TLS handshake failed.</summary>
    Tls,

    /// <summary>The caller cancelled. Not a network observation at all.</summary>
    Cancelled,

    /// <summary>Something else failed. Report the observation; name no cause.</summary>
    Unknown,
}

/// <summary>One address a connect was, or was not, attempted against.</summary>
/// <param name="Attempted">
/// False for an address that was resolved but never reached — an earlier address succeeded, or
/// the budget ran out. Reported so the message can say what was NOT ruled out.
/// </param>
public sealed record AddressAttempt(IPAddress Address, bool Attempted, SocketError? Error, string? Message)
{
    public bool IsIPv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    public static AddressAttempt NotTried(IPAddress address) => new(address, false, null, null);

    public static AddressAttempt Failed(IPAddress address, SocketException ex)
        => new(address, true, ex.SocketErrorCode, ex.Message);

    public static AddressAttempt Failed(IPAddress address, Exception ex)
        => ex is SocketException se ? Failed(address, se) : new(address, true, null, ex.Message);
}

/// <summary>
/// Thrown by <see cref="MultiAddressConnector"/> when no resolved address could be connected
/// to. Carries the per-address outcomes so the failure message can report what was tried
/// rather than collapsing every address into one <c>ex.Message</c> — the collapse is what
/// hid the address-family problem in #2926.
/// </summary>
public sealed class MultiAddressConnectException : Exception
{
    public MultiAddressConnectException(string host, int port,
        IReadOnlyList<AddressAttempt> attempts, Exception? resolutionError, string message)
        : base(message, resolutionError)
    {
        Host = host;
        Port = port;
        Attempts = attempts;
        ResolutionError = resolutionError;
    }

    public string Host { get; }
    public int Port { get; }
    public IReadOnlyList<AddressAttempt> Attempts { get; }

    /// <summary>Set when DNS itself failed, in which case <see cref="Attempts"/> is empty.</summary>
    public Exception? ResolutionError { get; }
}

/// <summary>A classified failure plus the lines that report it.</summary>
public sealed record NetworkFailureReport(NetworkFailureKind Kind, string Headline, IReadOnlyList<string> Detail)
{
    /// <summary>Headline first, then indented detail — the shape the existing log messages use.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string> { "Error: " + Headline };
            lines.AddRange(Detail.Select(d => "       " + d));
            return lines;
        }
    }

    public void WriteTo(Action<string> logf)
    {
        foreach (var line in Lines) logf(line);
    }
}

public static class NetworkDiagnosis
{
    /// <summary>
    /// Classify a failed HTTP attempt and produce a message that states what was observed.
    /// </summary>
    /// <param name="ex">The exception the request threw.</param>
    /// <param name="what">
    /// What was being fetched, in the caller's own words — e.g. "BC 28.1.49838.53910 (platform)".
    /// </param>
    /// <param name="url">The URL, used to name the host and port. Optional.</param>
    public static NetworkFailureReport Describe(Exception ex, string what, string? url = null)
    {
        var (host, port) = HostAndPort(url);
        var target = host is null ? "the server" : $"{host}:{port}";
        var inner = Unwrap(ex);

        // 1. The server answered. This is the ONLY branch entitled to say anything about the
        //    CDN's own health, because it is the only one holding bytes the CDN sent.
        if (FirstOfType<HttpRequestException>(inner) is { StatusCode: { } status } http)
        {
            return new NetworkFailureReport(NetworkFailureKind.ServerResponded,
                $"the BC artifact CDN answered HTTP {(int)status} ({status}) for {what}.",
                new[]
                {
                    $"Request: {url ?? "(url not recorded)"}",
                    "The server was reached and responded, so this is the CDN's own answer,",
                    "not a network problem on this host.",
                });
        }

        // 2. The caller cancelled, or the client's own timeout elapsed. .NET reports both as
        //    TaskCanceledException; only the timeout has a TimeoutException inside it.
        if (FirstOfType<OperationCanceledException>(inner) is { } canceled)
        {
            if (FirstOfType<TimeoutException>(canceled) is not null || FirstOfType<SocketException>(inner)?.SocketErrorCode == SocketError.TimedOut)
                return TimeoutReport(what, url, target);
            return new NetworkFailureReport(NetworkFailureKind.Cancelled,
                $"the request for {what} was cancelled before it completed.",
                new[] { $"Request: {url ?? "(url not recorded)"}" });
        }

        // 3. Our own multi-address connector ran and every address failed. This is the
        //    richest observation available: we know each address and why each one failed.
        if (FirstOfType<MultiAddressConnectException>(inner) is { } multi)
            return DescribeMultiAddress(multi, what, url);

        // 4. A raw socket failure, with no per-address record (a client not built by
        //    ArtifactDownloader.CreateClient, or a failure outside the connect callback).
        if (FirstOfType<SocketException>(inner) is { } socket)
            return DescribeSocketError(socket.SocketErrorCode, socket.Message, what, url, target,
                addressLines: Array.Empty<string>());

        // 5. TLS came up short. The TCP connection existed, so the host is reachable.
        if (FirstOfType<AuthenticationException>(inner) is { } tls)
            return new NetworkFailureReport(NetworkFailureKind.Tls,
                $"the TLS handshake with {target} failed while fetching {what}.",
                new[]
                {
                    $"Request: {url ?? "(url not recorded)"}",
                    $"Reported: {tls.Message}",
                    "The TCP connection was established, so the host is reachable; the failure is",
                    "in the handshake (certificate, protocol version, or an intercepting proxy).",
                });

        // 6. Nothing recognizable. Say exactly what was seen and stop. Naming a cause here is
        //    how #2926 happened in the first place.
        return new NetworkFailureReport(NetworkFailureKind.Unknown,
            $"the request for {what} failed before any response was received.",
            new[]
            {
                $"Request: {url ?? "(url not recorded)"}",
                $"Observed: {inner.GetType().Name}: {inner.Message}",
                "Nothing was received from the server, so this does not show the CDN is down;",
                "there is not enough information here to name a cause.",
            });
    }

    private static NetworkFailureReport TimeoutReport(string what, string? url, string target)
        => new(NetworkFailureKind.Timeout,
            $"the request for {what} timed out with no response from {target}.",
            new[]
            {
                $"Request: {url ?? "(url not recorded)"}",
                "A timeout does not say where the fault is: a transient network problem on this",
                "host, a firewall, packet loss and an unhealthy server all look identical from",
                "here. This is not evidence that the CDN is down. Retrying is usually the next step.",
            });

    private static NetworkFailureReport DescribeMultiAddress(
        MultiAddressConnectException multi, string what, string? url)
    {
        var target = $"{multi.Host}:{multi.Port}";

        if (multi.ResolutionError is not null || multi.Attempts.Count == 0)
        {
            var socketError = FirstOfType<SocketException>(multi.ResolutionError)?.SocketErrorCode;
            return new NetworkFailureReport(NetworkFailureKind.NameResolution,
                $"could not resolve {multi.Host} while fetching {what}.",
                new[]
                {
                    $"Request: {url ?? "(url not recorded)"}",
                    $"Observed: DNS returned no usable address" +
                        (socketError is null ? "." : $" ({socketError})."),
                    "No connection was attempted, so nothing here is a statement about the CDN —",
                    "check this host's DNS resolver first.",
                });
        }

        var addressLines = new List<string> { "Addresses tried:" };
        foreach (var a in multi.Attempts)
        {
            var family = a.IsIPv6 ? "IPv6" : "IPv4";
            var outcome = !a.Attempted
                ? "not tried"
                : a.Error is { } err ? $"{err} — {a.Message}" : a.Message ?? "failed";
            addressLines.Add($"  {a.Address} ({family}) {outcome}");
        }

        // Only claim the address family is the problem when the addresses say so: an IPv6
        // address the local routing table rejected, AND an IPv4 address of the same host that
        // either was never tried or failed for a different reason. If every address failed the
        // same way, the address family is not what distinguishes them and saying so would be
        // the same defect in a new spelling.
        var v6NoRoute = multi.Attempts.Any(a => a.IsIPv6 && a.Attempted && IsNoRoute(a.Error));
        var v4Present = multi.Attempts.Any(a => !a.IsIPv6);
        var v4NoRoute = multi.Attempts.Any(a => !a.IsIPv6 && a.Attempted && IsNoRoute(a.Error));

        if (v6NoRoute && v4Present && !v4NoRoute)
        {
            addressLines.Add("");
            addressLines.Add("The IPv6 address was rejected by this host's own routing table while an IPv4");
            addressLines.Add("address of the same host was resolved: this host most likely has no working");
            addressLines.Add("IPv6 route. Retry with DOTNET_SYSTEM_NET_DISABLEIPV6=1 to confirm.");
        }

        var errors = multi.Attempts.Where(a => a.Attempted).Select(a => a.Error).Distinct().ToList();
        var single = errors.Count == 1 ? errors[0] : null;

        if (single is not null && IsNoRoute(single))
            return DescribeSocketError(single.Value, multi.Message, what, url, target, addressLines);

        if (single == SocketError.TimedOut)
        {
            var timeout = TimeoutReport(what, url, target);
            return timeout with { Detail = timeout.Detail.Concat(addressLines).ToList() };
        }

        if (single == SocketError.ConnectionRefused)
            return DescribeSocketError(single.Value, multi.Message, what, url, target, addressLines);

        // Mixed or unrecognized failures across the addresses: report them all, claim nothing.
        return new NetworkFailureReport(NetworkFailureKind.Unknown,
            $"could not connect to {target} while fetching {what}.",
            new[] { $"Request: {url ?? "(url not recorded)"}" }
                .Concat(addressLines)
                .Concat(new[]
                {
                    "Nothing was received from the server, so this does not show the CDN is down;",
                    "the addresses failed for different reasons and there is not enough here to",
                    "name a single cause.",
                }).ToList());
    }

    private static NetworkFailureReport DescribeSocketError(
        SocketError error, string message, string what, string? url, string target,
        IReadOnlyList<string> addressLines)
    {
        var request = new[] { $"Request: {url ?? "(url not recorded)"}" };

        switch (error)
        {
            case SocketError.HostNotFound:
            case SocketError.NoData:
            case SocketError.TryAgain:
                return new NetworkFailureReport(NetworkFailureKind.NameResolution,
                    $"could not resolve the host for {what}.",
                    request.Concat(new[]
                    {
                        $"Observed: {error} — {message}",
                        "No connection was attempted, so nothing here is a statement about the CDN —",
                        "check this host's DNS resolver first.",
                    }).ToList());

            case SocketError.NetworkUnreachable:
            case SocketError.HostUnreachable:
                return new NetworkFailureReport(NetworkFailureKind.NoRouteToAddress,
                    $"this host has no route to {target} for {what}.",
                    request.Concat(new[] { $"Observed: {error} — {message}" })
                        .Concat(addressLines)
                        .Concat(new[]
                        {
                            "The local network stack refused the connection before any packet reached",
                            "the server, so this is a routing problem on this host, not a CDN outage.",
                        }).ToList());

            case SocketError.ConnectionRefused:
                return new NetworkFailureReport(NetworkFailureKind.ConnectionRefused,
                    $"{target} refused the connection for {what}.",
                    request.Concat(new[] { $"Observed: {error} — {message}" })
                        .Concat(addressLines)
                        .Concat(new[]
                        {
                            "Something answered at that address and rejected the connection. That is a",
                            "fact about the far end, but not about the CDN's own health — a proxy or a",
                            "captive network can produce it too.",
                        }).ToList());

            case SocketError.TimedOut:
                var timeout = TimeoutReport(what, url, target);
                return addressLines.Count == 0
                    ? timeout
                    : timeout with { Detail = timeout.Detail.Concat(addressLines).ToList() };

            default:
                return new NetworkFailureReport(NetworkFailureKind.Unknown,
                    $"could not connect to {target} while fetching {what}.",
                    request.Concat(new[] { $"Observed: {error} — {message}" })
                        .Concat(addressLines)
                        .Concat(new[]
                        {
                            "Nothing was received from the server, so this does not show the CDN is down;",
                            "there is not enough information here to name a cause.",
                        }).ToList());
        }
    }

    private static bool IsNoRoute(SocketError? error)
        => error is SocketError.NetworkUnreachable or SocketError.HostUnreachable;

    /// <summary>
    /// Peel <see cref="AggregateException"/> off the front. <c>ResolveVersion</c> blocks on
    /// <c>.Result</c>, which is exactly how "Error fetching index: One or more errors occurred.
    /// (A task was canceled.)" reached a user in #2926 — a message with no content at all.
    /// </summary>
    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
            ex = agg.InnerExceptions[0];
        return ex;
    }

    /// <summary>First exception of type <typeparamref name="T"/> in the inner chain, if any.</summary>
    private static T? FirstOfType<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is T match) return match;
            if (e is AggregateException agg)
                foreach (var i in agg.InnerExceptions)
                    if (FirstOfType<T>(i) is { } nested) return nested;
        }
        return null;
    }

    private static (string? Host, int Port) HostAndPort(string? url)
    {
        if (string.IsNullOrEmpty(url)) return (null, 0);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? (uri.Host, uri.Port) : (null, 0);
    }
}
