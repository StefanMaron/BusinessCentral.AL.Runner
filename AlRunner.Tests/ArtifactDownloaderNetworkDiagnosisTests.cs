// Issue #2926: tools/DownloadArtifacts reported "could not reach the BC artifact CDN" on a host
// whose only fault was having no IPv6 route to an AAAA record. The CDN was up; curl downloaded
// 893 MB from it seconds later over IPv4.
//
// Everything below runs against CAPTURED resolver and HTTP outcomes, never the live network.
// That is deliberate: a test that reads the real network passes for the wrong reason on a
// healthy box and cannot be made to fail on demand, so it would prove nothing about the one
// thing this fix is for — telling a transient timeout, an address-family problem and a real
// outage apart. The one exception is the loopback test at the bottom, which talks to a
// TcpListener on 127.0.0.1 to prove the connect callback is actually reached by the SYNCHRONOUS
// HttpClient.Send path that ArtifactDownloader uses everywhere.
using System.Net;
using System.Net.Sockets;
using System.Text;
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

public sealed class ArtifactDownloaderNetworkDiagnosisTests
{
    // The exact addresses from the issue report: `getent hosts` returned only the AAAA, and
    // curl's successful ranged GET came back from this IPv4 address.
    private static readonly IPAddress CdnV6 = IPAddress.Parse("2603:1061:14:34::1");
    private static readonly IPAddress CdnV4 = IPAddress.Parse("150.171.109.53");

    private const string CdnHost = "bcartifacts-exdbf9fwegejdqak.b02.azurefd.net";
    private const string CdnUrl = "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/28.1.49838.53910/platform";

    private static SocketException Sock(SocketError error) => new((int)error);

    private static string Text(NetworkFailureReport r) => string.Join("\n", r.Lines);

    /// <summary>
    /// The single claim this whole issue is about: nothing may assert the CDN is unhealthy
    /// unless the CDN actually sent us something.
    /// </summary>
    private static void AssertMakesNoClaimAboutTheCdn(NetworkFailureReport report)
    {
        var text = Text(report);
        Assert.DoesNotContain("could not reach the BC artifact CDN", text);
        Assert.DoesNotContain("the BC artifact CDN answered", text);
    }

    // ------------------------------------------------------------------
    // Classification: the same failure text used to cover all of these.
    // ------------------------------------------------------------------

    [Fact]
    public void NoRouteOnIPv6WithIPv4Resolved_NamesTheRoutingFact_AndNeverBlamesTheCdn()
    {
        // The captured shape of the issue: the kernel refused the IPv6 route, and the IPv4
        // address of the same host was resolved but never reached.
        var attempts = new[]
        {
            AddressAttempt.Failed(CdnV6, Sock(SocketError.NetworkUnreachable)),
            AddressAttempt.NotTried(CdnV4),
        };
        var ex = new HttpRequestException("connection failure",
            new MultiAddressConnectException(CdnHost, 443, attempts, null, "could not connect"));

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.NoRouteToAddress, report.Kind);
        // This is the whole point: the old message sent the reporter to check Azure by hand.
        AssertMakesNoClaimAboutTheCdn(report);
        Assert.Contains("not a CDN outage", text);
        // It must name what was actually observed, per address, including the one NOT tried —
        // "not tried" and "failed" are different observations.
        Assert.Contains("2603:1061:14:34::1 (IPv6)", text);
        Assert.Contains("150.171.109.53 (IPv4) not tried", text);
        Assert.Contains("NetworkUnreachable", text);
        // ...and only here, where the evidence supports it, may it name the address family.
        Assert.Contains("no working", text);
        Assert.Contains("IPv6 route", text);
        Assert.Contains("DOTNET_SYSTEM_NET_DISABLEIPV6=1", text);
    }

    [Fact]
    public void NoRouteOnBothFamilies_ReportsTheRoutingFact_ButNotTheIPv6Story()
    {
        // Both families refused: the address family is not what distinguishes them, so
        // pointing at IPv6 would be the same defect in a new spelling.
        var attempts = new[]
        {
            AddressAttempt.Failed(CdnV6, Sock(SocketError.NetworkUnreachable)),
            AddressAttempt.Failed(CdnV4, Sock(SocketError.NetworkUnreachable)),
        };
        var ex = new MultiAddressConnectException(CdnHost, 443, attempts, null, "could not connect");

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.NoRouteToAddress, report.Kind);
        Assert.DoesNotContain("DOTNET_SYSTEM_NET_DISABLEIPV6", text);
        Assert.DoesNotContain("no working", text);
        Assert.Contains("150.171.109.53 (IPv4) NetworkUnreachable", text);
        AssertMakesNoClaimAboutTheCdn(report);
    }

    [Fact]
    public void MixedFailuresAcrossAddresses_NamesNoCauseAtAll()
    {
        // One address had no route, the other actively refused. There is no single cause here
        // and the tool must not invent one — it reports both and stops. In particular the IPv6
        // advice must not appear: the IPv4 address was tried and failed too, so turning IPv6
        // off would not have helped. This assertion caught the first version of the fix doing
        // exactly that.
        var attempts = new[]
        {
            AddressAttempt.Failed(CdnV6, Sock(SocketError.NetworkUnreachable)),
            AddressAttempt.Failed(CdnV4, Sock(SocketError.ConnectionRefused)),
        };
        var ex = new MultiAddressConnectException(CdnHost, 443, attempts, null, "could not connect");

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.Unknown, report.Kind);
        Assert.Contains("not enough here to", text);
        Assert.Contains("NetworkUnreachable", text);
        Assert.Contains("ConnectionRefused", text);
        Assert.DoesNotContain("DOTNET_SYSTEM_NET_DISABLEIPV6", text);
        AssertMakesNoClaimAboutTheCdn(report);
    }

    [Fact]
    public void ClientTimeout_SaysItCannotTellWhereTheFaultIs()
    {
        // How .NET reports HttpClient.Timeout elapsing. On this machine these happen for real
        // and intermittently, so the message has to be honest about proving nothing.
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException("A task was canceled."));

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.Timeout, report.Kind);
        Assert.Contains("timed out", text);
        Assert.Contains("does not say where the fault is", text);
        Assert.Contains("Retrying", text);
        // A timeout is consistent with all of these and evidence for none of them.
        AssertMakesNoClaimAboutTheCdn(report);
        Assert.DoesNotContain("no route", text);
        Assert.DoesNotContain("routing problem", text);
        Assert.DoesNotContain("resolve", text);
    }

    [Fact]
    public void AggregateExceptionFromResolveVersion_IsUnwrapped_NotEchoed()
    {
        // The literal second failure in the issue: ResolveVersion blocks on .Result, so the
        // user was shown "Error fetching index: One or more errors occurred. (A task was
        // canceled.)" — a sentence with no information in it whatsoever.
        var ex = new AggregateException(
            new TaskCanceledException("canceled", new TimeoutException("A task was canceled.")));

        var report = NetworkDiagnosis.Describe(ex, "the BC version index (prefix '28.1')",
            "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/indexes/w1.json");
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.Timeout, report.Kind);
        Assert.DoesNotContain("One or more errors occurred", text);
        Assert.Contains("the BC version index (prefix '28.1')", text);
        Assert.Contains("indexes/w1.json", text);
    }

    [Fact]
    public void DnsFailure_PointsAtTheResolver_NotAtTheCdn()
    {
        var ex = new MultiAddressConnectException(CdnHost, 443, Array.Empty<AddressAttempt>(),
            Sock(SocketError.HostNotFound), "could not resolve");

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.NameResolution, report.Kind);
        Assert.Contains("could not resolve " + CdnHost, text);
        Assert.Contains("DNS", text);
        Assert.Contains("No connection was attempted", text);
        AssertMakesNoClaimAboutTheCdn(report);
        Assert.DoesNotContain("DOTNET_SYSTEM_NET_DISABLEIPV6", text);
    }

    [Fact]
    public void RawSocketException_WithNoPerAddressRecord_StillClassifies()
    {
        // A client built somewhere other than ArtifactHttpClient.Create still has to produce a
        // sane message — the classifier cannot depend on our connector having run.
        var ex = new HttpRequestException("name resolution failure", Sock(SocketError.HostNotFound));

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);

        Assert.Equal(NetworkFailureKind.NameResolution, report.Kind);
        Assert.Contains("DNS", Text(report));
        AssertMakesNoClaimAboutTheCdn(report);
    }

    [Fact]
    public void ServerResponded_IsTheOnlyKindAllowedToTalkAboutTheCdn()
    {
        var ex = new HttpRequestException("Response status code does not indicate success: 503",
            null, HttpStatusCode.ServiceUnavailable);

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.ServerResponded, report.Kind);
        // We hold bytes the CDN sent, so here — and only here — naming it is honest.
        Assert.Contains("the BC artifact CDN answered HTTP 503", text);
        Assert.Contains("The server was reached", text);
        // ...and it must not be confused with the unpublished-version case (#1659/#2236).
        Assert.DoesNotContain("no BC artifact published", text);
    }

    [Fact]
    public void ConnectionRefused_NamesTheFarEnd_WithoutClaimingTheCdnIsUnhealthy()
    {
        var attempts = new[] { AddressAttempt.Failed(CdnV4, Sock(SocketError.ConnectionRefused)) };
        var ex = new MultiAddressConnectException(CdnHost, 443, attempts, null, "refused");

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.ConnectionRefused, report.Kind);
        Assert.Contains("refused the connection", text);
        Assert.Contains("proxy", text);
        AssertMakesNoClaimAboutTheCdn(report);
    }

    [Fact]
    public void UnrecognizedFailure_ReportsTheObservation_AndExplicitlyNamesNoCause()
    {
        var ex = new IOException("The response ended prematurely.");

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        var text = Text(report);

        Assert.Equal(NetworkFailureKind.Unknown, report.Kind);
        Assert.Contains("Observed: IOException: The response ended prematurely.", text);
        Assert.Contains("not enough information here to name a cause", text);
        AssertMakesNoClaimAboutTheCdn(report);
    }

    // ------------------------------------------------------------------
    // The connector: does it walk the addresses the way curl does?
    // ------------------------------------------------------------------

    private sealed class FakeStream : MemoryStream
    {
        public FakeStream(IPAddress address) => Address = address;
        public IPAddress Address { get; }
    }

    private static MultiAddressConnector.ResolveDelegate Resolves(params IPAddress[] addresses)
        => (_, _) => ValueTask.FromResult(addresses);

    [Fact]
    public async Task IPv6NoRouteThenIPv4_ConnectsOverIPv4_LikeCurlDoes()
    {
        var tried = new List<IPAddress>();
        var notes = new List<string>();

        var stream = await MultiAddressConnector.ConnectAnyAsync(
            CdnHost, 443,
            Resolves(CdnV6, CdnV4),
            (address, _, _) =>
            {
                tried.Add(address);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    throw Sock(SocketError.NetworkUnreachable);
                return ValueTask.FromResult<Stream>(new FakeStream(address));
            },
            TimeSpan.FromSeconds(1), CancellationToken.None, notes.Add);

        // The whole reported failure: the tool stopped at the first address.
        Assert.Equal(new[] { CdnV6, CdnV4 }, tried);
        Assert.Equal(CdnV4, Assert.IsType<FakeStream>(stream).Address);
        // A silent fallback is its own problem — every connection then pays for a broken IPv6
        // route while the host looks healthy. It has to be said once.
        var note = Assert.Single(notes);
        Assert.Contains("NetworkUnreachable", note);
        Assert.Contains("connected over IPv4 (150.171.109.53)", note);
    }

    [Fact]
    public async Task FirstAddressSucceeds_SaysNothingAboutAFallbackThatDidNotHappen()
    {
        var notes = new List<string>();

        await MultiAddressConnector.ConnectAnyAsync(
            CdnHost, 443, Resolves(CdnV6, CdnV4),
            (address, _, _) => ValueTask.FromResult<Stream>(new FakeStream(address)),
            TimeSpan.FromSeconds(1), CancellationToken.None, notes.Add);

        Assert.Empty(notes);
    }

    [Fact]
    public async Task BlackHoledAddress_IsCappedPerAddress_SoTheNextOneIsStillTried()
    {
        // A firewall dropping SYNs is the common shape of a broken IPv6 path: no error, just
        // silence. Without a per-address cap, one dead address eats the entire budget and the
        // working address is never reached — which looks exactly like the reported bug.
        var tried = new List<IPAddress>();

        var stream = await MultiAddressConnector.ConnectAnyAsync(
            CdnHost, 443, Resolves(CdnV6, CdnV4),
            async (address, _, ct) =>
            {
                tried.Add(address);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    await Task.Delay(Timeout.Infinite, ct);
                return new FakeStream(address);
            },
            TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.Equal(new[] { CdnV6, CdnV4 }, tried);
        Assert.Equal(CdnV4, Assert.IsType<FakeStream>(stream).Address);
    }

    [Fact]
    public async Task LastAddressIsNotCapped_SoASlowConnectStillSucceeds()
    {
        // The cap exists to stop a dead address starving the ones behind it. On the last
        // address nothing is behind it, so capping there could only turn a slow-but-working
        // connect into a failure — a regression against the stock client, which would have
        // waited. Measured on the development box: its IPv6 address fails instantly and its
        // IPv4 address intermittently black-holes for ~135s, so this distinction is not
        // hypothetical.
        var tried = new List<IPAddress>();

        var stream = await MultiAddressConnector.ConnectAnyAsync(
            CdnHost, 443, Resolves(CdnV6, CdnV4),
            async (address, _, ct) =>
            {
                tried.Add(address);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    throw Sock(SocketError.NetworkUnreachable);
                // Comfortably longer than the per-address cap below.
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                return new FakeStream(address);
            },
            TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(new[] { CdnV6, CdnV4 }, tried);
        Assert.Equal(CdnV4, Assert.IsType<FakeStream>(stream).Address);
    }

    [Fact]
    public async Task SoleAddressIsNotCapped_EitherWay()
    {
        // Degenerate case of the same rule: with one address the walk must behave exactly like
        // the stock client, or this fix would make single-homed hosts worse than before.
        var stream = await MultiAddressConnector.ConnectAnyAsync(
            CdnHost, 443, Resolves(CdnV4),
            async (address, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                return new FakeStream(address);
            },
            TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(CdnV4, Assert.IsType<FakeStream>(stream).Address);
    }

    [Fact]
    public async Task EveryAddressFails_RecordsEachOutcome_ForTheMessageToReport()
    {
        var ex = await Assert.ThrowsAsync<MultiAddressConnectException>(async () =>
            await MultiAddressConnector.ConnectAnyAsync(
                CdnHost, 443, Resolves(CdnV6, CdnV4),
                (address, _, _) => throw Sock(address.AddressFamily == AddressFamily.InterNetworkV6
                    ? SocketError.NetworkUnreachable
                    : SocketError.ConnectionRefused),
                TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(2, ex.Attempts.Count);
        Assert.All(ex.Attempts, a => Assert.True(a.Attempted));
        Assert.Equal(SocketError.NetworkUnreachable, ex.Attempts[0].Error);
        Assert.Equal(SocketError.ConnectionRefused, ex.Attempts[1].Error);
        Assert.Null(ex.ResolutionError);

        // ...and the classification of that exception is the mixed case: no single cause.
        Assert.Equal(NetworkFailureKind.Unknown,
            NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl).Kind);
    }

    [Fact]
    public async Task ResolverReturnsNothing_IsAResolutionFailure_NotAConnectFailure()
    {
        var ex = await Assert.ThrowsAsync<MultiAddressConnectException>(async () =>
            await MultiAddressConnector.ConnectAnyAsync(
                CdnHost, 443, Resolves(),
                (_, _, _) => throw new InvalidOperationException("must not be reached"),
                TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Empty(ex.Attempts);
        Assert.Equal(NetworkFailureKind.NameResolution,
            NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl).Kind);
    }

    [Fact]
    public async Task ResolverThrows_KeepsTheDnsErrorInsteadOfReportingAConnectProblem()
    {
        var ex = await Assert.ThrowsAsync<MultiAddressConnectException>(async () =>
            await MultiAddressConnector.ConnectAnyAsync(
                CdnHost, 443,
                (_, _) => throw Sock(SocketError.HostNotFound),
                (_, _, _) => throw new InvalidOperationException("must not be reached"),
                TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Empty(ex.Attempts);
        Assert.Equal(SocketError.HostNotFound, Assert.IsType<SocketException>(ex.ResolutionError).SocketErrorCode);

        var report = NetworkDiagnosis.Describe(ex, "BC 28.1.49838.53910 (platform)", CdnUrl);
        Assert.Equal(NetworkFailureKind.NameResolution, report.Kind);
        Assert.Contains("HostNotFound", Text(report));
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsANetworkFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MultiAddressConnector.ConnectAnyAsync(
                CdnHost, 443, Resolves(CdnV6, CdnV4),
                (_, _, _) => throw new InvalidOperationException("must not be reached"),
                TimeSpan.FromSeconds(1), cts.Token));
    }

    // ------------------------------------------------------------------
    // The connect callback has to be reached by the SYNCHRONOUS Send path.
    // ------------------------------------------------------------------

    [Fact]
    public void CreateClient_ConnectCallbackIsUsedBySynchronousSend_OverLoopback()
    {
        // ArtifactDownloader calls http.Send(...), not SendAsync. If SocketsHttpHandler's
        // ConnectCallback were async-only, the whole fix would be dead code on every path that
        // matters and nothing above would notice. Loopback only — no external network.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(() =>
        {
            using var client = listener.AcceptTcpClient();
            using var netStream = client.GetStream();
            var buffer = new byte[4096];
            netStream.Read(buffer, 0, buffer.Length); // the request line + headers
            var body = "ok";
            var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            var bytes = Encoding.ASCII.GetBytes(response);
            netStream.Write(bytes, 0, bytes.Length);
            netStream.Flush();
        });

        using var http = ArtifactHttpClient.Create(TimeSpan.FromSeconds(30));
        using var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/probe"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok", new StreamReader(resp.Content.ReadAsStream()).ReadToEnd());
        Assert.True(server.Wait(TimeSpan.FromSeconds(30)));
    }

    // ------------------------------------------------------------------
    // The tool-level surface: TryHeadContentLength is where the message came from.
    // ------------------------------------------------------------------

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _make;
        public ThrowingHandler(Func<Exception> make) => _make = make;

        protected override HttpResponseMessage Send(HttpRequestMessage r, CancellationToken ct) => throw _make();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(_make());
    }

    [Fact]
    public void TryHeadContentLength_AddressFamilyFailure_ReportsTheAddresses_NotACdnOutage()
    {
        var attempts = new[]
        {
            AddressAttempt.Failed(CdnV6, Sock(SocketError.NetworkUnreachable)),
            AddressAttempt.NotTried(CdnV4),
        };
        using var http = new HttpClient(new ThrowingHandler(() => new HttpRequestException(
            "connection failure",
            new MultiAddressConnectException(CdnHost, 443, attempts, null, "could not connect"))));
        var logs = new List<string>();

        var ok = ArtifactDownloader.TryHeadContentLength(
            http, CdnUrl, "28.1.49838.53910", "platform", logs.Add, out long size);

        var text = string.Join("\n", logs);
        Assert.False(ok);
        Assert.Equal(0, size);
        // The line the issue was filed about.
        Assert.DoesNotContain("could not reach the BC artifact CDN", text);
        Assert.Contains("no route", text);
        Assert.Contains("2603:1061:14:34::1", text);
        Assert.Contains("150.171.109.53", text);
        Assert.Contains("DOTNET_SYSTEM_NET_DISABLEIPV6=1", text);
        // ...and it must still not be confused with an unpublished version.
        Assert.DoesNotContain("no BC artifact published", text);
    }

    [Fact]
    public void TryHeadContentLength_Timeout_IsHandled_NotThrown()
    {
        // A sibling of #1659: this method caught only HttpRequestException, so the single most
        // common transient failure escaped it as an unhandled exception with a raw stack trace.
        using var http = new HttpClient(new ThrowingHandler(() => new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing.",
            new TimeoutException("A task was canceled."))));
        var logs = new List<string>();

        var ok = ArtifactDownloader.TryHeadContentLength(
            http, CdnUrl, "28.1.49838.53910", "platform", logs.Add, out long size);

        var text = string.Join("\n", logs);
        Assert.False(ok);
        Assert.Equal(0, size);
        Assert.Contains("timed out", text);
        Assert.Contains("does not say where the fault is", text);
        Assert.DoesNotContain("could not reach the BC artifact CDN", text);
    }
}
