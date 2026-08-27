// Issue #1659: tools/DownloadArtifacts platform-apps (and its ServiceTier/TestApps/SystemApp
// siblings) crashed with an unhandled HttpRequestException + raw .NET stack trace when the
// requested BC version had no artifact on the CDN. ArtifactDownloader.TryHeadContentLength is
// the shared choke point all four download modes route through (HeadContentLength itself stays
// private and unchanged) — it must turn a 404 into a named, actionable log message instead of
// letting the exception propagate, and must not swallow a genuinely-sized successful response.
using System.Net;
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

public sealed class ArtifactDownloader404Tests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly long? _contentLength;

        public StubHandler(HttpStatusCode status, long? contentLength = null)
        {
            _status = status;
            _contentLength = contentLength;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_status) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            if (_contentLength is long len)
                resp.Content.Headers.ContentLength = len;
            return resp;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void TryHeadContentLength_404_ReturnsFalseWithNamedResolveVersionHint_NoUnhandledException()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.NotFound));
        var logs = new List<string>();

        // The exact repro from the issue: a real, plausible version+channel that simply has
        // no published artifact. This must not throw — that is the whole bug.
        var ok = ArtifactDownloader.TryHeadContentLength(
            http, "https://bcartifacts.example/sandbox/28.0.46665.47126/w1",
            "28.0.46665.47126", "w1", logs.Add, out long size);

        Assert.False(ok);
        Assert.Equal(0, size);
        Assert.Contains(logs, l => l == "Error: no BC artifact published for 28.0.46665.47126 (w1).");
        // Issue #2085: this hint must be tool-install-valid — `dotnet run --project` requires
        // a source checkout a `dotnet tool install -g` user never has.
        Assert.Contains(logs, l => l.Contains("al-runner provision --resolve-version 28.0"));
        Assert.DoesNotContain(logs, l => l.Contains("dotnet run --project"));
    }

    [Fact]
    public void TryHeadContentLength_ServerError_ReturnsFalseWithCdnReachabilityMessage_DistinctFrom404()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.InternalServerError));
        var logs = new List<string>();

        var ok = ArtifactDownloader.TryHeadContentLength(
            http, "https://bcartifacts.example/sandbox/28.1.0.0/platform",
            "28.1.0.0", "platform", logs.Add, out long size);

        Assert.False(ok);
        Assert.Equal(0, size);
        // Must NOT claim the version is unpublished — a 500 is a CDN problem, not a bad version.
        Assert.DoesNotContain(logs, l => l.Contains("no BC artifact published"));
        Assert.Contains(logs, l => l.Contains("could not reach the BC artifact CDN") && l.Contains("28.1.0.0"));
    }

    [Fact]
    public void TryHeadContentLength_Success_ReturnsTrueWithRealContentLength()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, contentLength: 123456789));
        var logs = new List<string>();

        var ok = ArtifactDownloader.TryHeadContentLength(
            http, "https://bcartifacts.example/sandbox/28.1.49838.53220/w1",
            "28.1.49838.53220", "w1", logs.Add, out long size);

        Assert.True(ok);
        Assert.Equal(123456789, size);
    }
}
