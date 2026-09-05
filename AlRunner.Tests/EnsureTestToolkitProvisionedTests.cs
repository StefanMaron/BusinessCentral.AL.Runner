// EnsureTestToolkitProvisionedTests — issue #2558.
//
// `al-runner provision --test-apps` (Program.cs's EnsureTestToolkitProvisioned, now a
// thin wrapper over AlRunner.Infrastructure.ProvisioningCheck.EnsureTestToolkitProvisioned)
// had two silent-degradation bugs:
//
// 1. The entry guard accepted ANY single .app file in the destination directory as proof
//    of a complete toolkit. An interrupted extraction that landed one country test app but
//    not "Business Foundation Test Libraries" (the real, well-known sentinel — see
//    ProvisioningCheck.TestToolkitSentinelApp) read as present forever; re-running never
//    re-attempted the download.
// 2. The post-download check only inspected the download delegate's exit code.
//    ArtifactDownloader.TestApps reports success (rc == 0) as soon as ANY file extracted
//    and skips entries it could not fetch silently — so rc == 0 does not mean the sentinel
//    app actually landed. `provision --test-apps` could exit 0 over a partial toolkit.
//
// Both are exercised here via a FAKE download delegate rather than a real network call
// (ProvisionExplicitModesTests.cs already covers the real end-to-end download path) —
// this is what lets the "reports success without landing the sentinel" shape be proven
// deterministically instead of depending on a real, flaky partial download.

using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class EnsureTestToolkitProvisionedTests : IDisposable
{
    private readonly string _dir;

    public EnsureTestToolkitProvisionedTests()
    {
        _dir = TestScratch.Dir("al-runner-ensure-toolkit");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── minimal NAVX .app writer (mirrors ProvisioningCheckTests.cs's own helpers) ────────

    private static byte[] MakeMinimalNavxApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private void WriteApp(string fileName, string appId, string name, string publisher, string version)
        => File.WriteAllBytes(Path.Combine(_dir, fileName), MakeMinimalNavxApp(appId, name, publisher, version));

    private void WriteSentinel() =>
        WriteApp("microsoft_business foundation test libraries_28.1.0.0.app",
            "bee8cf2f-494a-42f4-aabd-650e87934d39", ProvisioningCheck.TestToolkitSentinelApp, "Microsoft", "28.1.0.0");

    private void WriteUnrelatedApp() =>
        WriteApp("nl_some country test app_28.1.0.0.app",
            "00000000-0000-0000-0000-0000000000aa", "NL Some Country Test App", "Microsoft", "28.1.0.0");

    // ── entry guard ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SentinelAlreadyPresent_ReportsAlreadyPresent_NeverCallsDownload()
    {
        WriteSentinel();
        var downloadCalled = false;
        var logs = new List<string>();

        var ok = ProvisioningCheck.EnsureTestToolkitProvisioned(
            "28.1.49838.53910", _dir,
            (v, d, l) => { downloadCalled = true; return 0; },
            logs.Add);

        Assert.True(ok);
        Assert.False(downloadCalled, "already-complete toolkit must not trigger a download");
        Assert.Contains(logs, l => l.Contains("already present") && l.Contains(_dir));
    }

    [Fact]
    public void OnlyUnrelatedAppPresent_DoesNotReportAlreadyPresent_CallsDownload()
    {
        // The bug: a directory holding ONE .app that is NOT the sentinel (e.g. left behind
        // by an earlier interrupted extraction) used to satisfy the old "any .app exists"
        // guard forever.
        WriteUnrelatedApp();
        var downloadCalled = false;
        var logs = new List<string>();

        var ok = ProvisioningCheck.EnsureTestToolkitProvisioned(
            "28.1.49838.53910", _dir,
            (v, d, l) =>
            {
                downloadCalled = true;
                // Simulate a successful, COMPLETE download this time: land the sentinel.
                File.WriteAllBytes(Path.Combine(d, "microsoft_business foundation test libraries_28.1.0.0.app"),
                    MakeMinimalNavxApp("bee8cf2f-494a-42f4-aabd-650e87934d39",
                        ProvisioningCheck.TestToolkitSentinelApp, "Microsoft", "28.1.0.0"));
                return 0;
            },
            logs.Add);

        Assert.True(ok);
        Assert.True(downloadCalled, "an unrelated leftover .app must not short-circuit the download");
        Assert.DoesNotContain(logs, l => l.Contains("already present"));
        Assert.Contains(logs, l => l.Contains("fetching"));
    }

    // ── post-download re-check ──────────────────────────────────────────────────────────

    [Fact]
    public void DownloadReturnsZero_ButSentinelNeverLands_FailsLoudly_NamesTheSentinel()
    {
        // The exact silent-skip shape #2558 reports: ArtifactDownloader.TestApps returns 0
        // (something extracted) but the specific sentinel app never made it to disk.
        var logs = new List<string>();

        var ok = ProvisioningCheck.EnsureTestToolkitProvisioned(
            "28.1.49838.53910", _dir,
            (v, d, l) =>
            {
                // Simulate a partial extraction: writes an unrelated file, "succeeds" (rc=0),
                // but never writes the sentinel.
                File.WriteAllText(Path.Combine(d, "some-other-file.app"), "not a real app");
                return 0;
            },
            logs.Add);

        Assert.False(ok, "rc == 0 without the sentinel landing must NOT be treated as success");
        Assert.Contains(logs, l => l.Contains("still missing") && l.Contains(ProvisioningCheck.TestToolkitSentinelApp) && l.Contains(_dir));
    }

    [Fact]
    public void DownloadReturnsNonZero_FailsLoudly_NamesTheVersion()
    {
        var logs = new List<string>();

        var ok = ProvisioningCheck.EnsureTestToolkitProvisioned(
            "28.1.49838.53910", _dir,
            (v, d, l) => 1,
            logs.Add);

        Assert.False(ok);
        Assert.Contains(logs, l => l.Contains("warning") && l.Contains("could not fetch") && l.Contains("28.1.49838.53910"));
    }

    [Fact]
    public void DownloadThrows_FailsLoudly_DoesNotPropagateTheException()
    {
        var logs = new List<string>();

        var ok = ProvisioningCheck.EnsureTestToolkitProvisioned(
            "28.1.49838.53910", _dir,
            (v, d, l) => throw new InvalidOperationException("network exploded"),
            logs.Add);

        Assert.False(ok);
        Assert.Contains(logs, l => l.Contains("download failed") && l.Contains("network exploded"));
    }

    [Fact]
    public void DownloadReturnsZero_AndSentinelLands_Succeeds()
    {
        var logs = new List<string>();

        var ok = ProvisioningCheck.EnsureTestToolkitProvisioned(
            "28.1.49838.53910", _dir,
            (v, d, l) =>
            {
                File.WriteAllBytes(Path.Combine(d, "microsoft_business foundation test libraries_28.1.0.0.app"),
                    MakeMinimalNavxApp("bee8cf2f-494a-42f4-aabd-650e87934d39",
                        ProvisioningCheck.TestToolkitSentinelApp, "Microsoft", "28.1.0.0"));
                return 0;
            },
            logs.Add);

        Assert.True(ok);
        Assert.DoesNotContain(logs, l => l.Contains("still missing"));
    }
}
