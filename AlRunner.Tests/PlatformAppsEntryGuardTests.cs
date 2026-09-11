// PlatformAppsEntryGuardTests — issue #2661.
//
// `provision --platform-apps` and `--service-tier` kept the pre-#2558 entry guard: "does at
// least one file matching a glob exist in the canonical directory". #2558 replaced exactly
// that shape for `--test-apps` with a real manifest parse; these two modes were left behind
// because, as the issue says, they "have no equivalent ready-made predicate".
//
// Both predicates now exist, and they are DIFFERENT questions — the measurement that decided
// it is in the PR body and docs/provisioning.md#platform-apps-completeness. On the reporting
// box, one artifact directory holds a complete 501-DLL engine closure AND a platform-apps
// directory containing exactly one file, System.app:
//
//   28.1.49838.53910   dlls=501  ncl=Y  platform-apps=1   <- complete engine, short apps
//   28.0.46665.54452   dlls=0    ncl=n  platform-apps=6   <- no engine, complete apps
//
// So ArtifactDirState (the engine closure) cannot answer for the platform-app half, and
// PlatformAppsComplete is its sibling rather than a reuse of it.
//
// Fake .app writer rather than a network download: ProvisionExplicitModesTests covers the
// real end-to-end path against the CDN. This is what lets the false-green shape be proven
// deterministically.

using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class PlatformAppsEntryGuardTests : IDisposable
{
    private readonly string _dir;

    public PlatformAppsEntryGuardTests()
    {
        _dir = TestScratch.Dir("al-runner-platform-apps-guard");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    private void WriteApp(string name, string publisher = "Microsoft", string version = "28.1.49838.53910")
        => File.WriteAllBytes(
            Path.Combine(_dir, $"{publisher}_{name}_{version}.app"),
            MakeMinimalNavxApp(Guid.NewGuid().ToString(), name, publisher, version));

    private void WriteCompleteW1Set()
    {
        foreach (var n in ProvisioningCheck.W1PlatformAppNames) WriteApp(n);
    }

    // ── the reported false green ────────────────────────────────────────────────────────

    /// <summary>
    /// The exact on-disk shape #2661 reports, taken from a real directory rather than
    /// invented: 28.1.49838.53910/platform-apps holds one file, System.app, and nothing
    /// else. The old glob ("does any *.app exist") accepts it as a complete provision
    /// forever, so a re-run never re-attempts the download.
    /// </summary>
    [Fact]
    public void OnlySystemAppPresent_IsNotComplete_AndNamesEveryMissingApp()
    {
        // System.app carries no NavxManifest at all — it is the platform symbol package,
        // not one of the five core apps. That is why the glob and a manifest parse disagree.
        File.WriteAllText(Path.Combine(_dir, "System.app"), "not a real NAVX package");

        var verdict = ProvisioningCheck.PlatformAppsComplete(_dir, "w1", out var missing);

        Assert.False(verdict);
        // The old predicate's answer, asserted directly so the two are visibly different:
        Assert.True(Directory.EnumerateFiles(_dir, "*.app").Any(),
            "the glob the entry guard used to apply must still match — that IS the defect");
        Assert.Equal(ProvisioningCheck.W1PlatformAppNames.OrderBy(x => x),
                     missing.OrderBy(x => x));
        Assert.False(ProvisioningCheck.PlatformAppsPresent(_dir, "w1"));
    }

    /// <summary>
    /// The complete w1 core set is accepted — the guard must not force a re-download of a
    /// directory that is genuinely provisioned, which is the failure mode a fix that simply
    /// hard-errors would introduce.
    /// </summary>
    [Fact]
    public void CompleteW1Set_IsComplete_AndNothingIsMissing()
    {
        WriteCompleteW1Set();

        var verdict = ProvisioningCheck.PlatformAppsComplete(_dir, "w1", out var missing);

        Assert.True(verdict);
        Assert.Empty(missing);
        Assert.True(ProvisioningCheck.PlatformAppsPresent(_dir, "w1"));
    }

    /// <summary>
    /// One app short is short. Asserts the specific absent name, not merely "false" — a
    /// predicate that answered false for everything would pass a bare assertion here.
    /// </summary>
    [Fact]
    public void OneCoreAppMissing_IsNotComplete_AndNamesExactlyThatApp()
    {
        foreach (var n in ProvisioningCheck.W1PlatformAppNames)
            if (n != "Business Foundation") WriteApp(n);

        var verdict = ProvisioningCheck.PlatformAppsComplete(_dir, "w1", out var missing);

        Assert.False(verdict);
        Assert.Equal(new[] { "Business Foundation" }, missing);
    }

    /// <summary>
    /// Publisher is load-bearing: a third party shipping an app called "Base Application"
    /// does not provision Microsoft's. Mirrors TestToolkitPresent's own publisher filter.
    /// </summary>
    [Fact]
    public void NonMicrosoftAppsOfTheRightNames_DoNotCount()
    {
        foreach (var n in ProvisioningCheck.W1PlatformAppNames) WriteApp(n, publisher: "Contoso");

        var verdict = ProvisioningCheck.PlatformAppsComplete(_dir, "w1", out var missing);

        Assert.False(verdict);
        Assert.Equal(ProvisioningCheck.W1PlatformAppNames.Length, missing.Count);
    }

    [Fact]
    public void MissingDirectory_IsNotComplete_AndNamesTheWholeSet()
    {
        var gone = Path.Combine(_dir, "does-not-exist");

        var verdict = ProvisioningCheck.PlatformAppsComplete(gone, "w1", out var missing);

        Assert.False(verdict);
        Assert.Equal(ProvisioningCheck.W1PlatformAppNames.Length, missing.Count);
        Assert.False(ProvisioningCheck.PlatformAppsPresent(gone, "w1"));
    }

    // ── the third state ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// guards-need-a-third-state.md: a country channel has no curated set to compare
    /// against — ArtifactDownloader.IsWantedPlatformAppEntry takes EVERY Microsoft-published
    /// app a country artifact ships, deliberately, so nothing enumerates what "complete"
    /// means there. The predicate says "could not measure" rather than guessing, and both
    /// guesses would be wrong in a different direction: false re-downloads a complete set on
    /// every run, true restores the glob's false green as a measured-looking answer.
    /// </summary>
    [Fact]
    public void CountryChannel_IsUnmeasurable_ReturnsNull_NotAVerdict()
    {
        WriteCompleteW1Set();

        Assert.Null(ProvisioningCheck.PlatformAppsComplete(_dir, "us", out _));
        Assert.Null(ProvisioningCheck.PlatformAppsComplete(_dir, "de", out _));
        // ...and w1 on the same directory IS measurable, so null is about the channel and
        // not about the directory being unreadable.
        Assert.True(ProvisioningCheck.PlatformAppsComplete(_dir, "w1", out _));
    }

    /// <summary>
    /// The unmeasurable case falls back to the pre-#2661 glob rather than to a verdict:
    /// that is the honest answer for a country channel, and it must still discriminate an
    /// empty directory from a populated one.
    /// </summary>
    [Fact]
    public void CountryChannel_FallsBackToTheGlob_WhichStillSeparatesEmptyFromPopulated()
    {
        Assert.False(ProvisioningCheck.PlatformAppsPresent(_dir, "us"));

        File.WriteAllText(Path.Combine(_dir, "anything.app"), "x");

        Assert.True(ProvisioningCheck.PlatformAppsPresent(_dir, "us"));
    }

    // ── the entry guard actually uses it ─────────────────────────────────────────────────

    /// <summary>
    /// ForceProvisionMode is the shared helper both modes route through. Wired with the new
    /// predicate, a directory holding only System.app must NOT short-circuit — the download
    /// has to run. This is the end #2661 reports: "the entry-guard precision" is all that was
    /// left, the post-download re-check having already been generalized by #2558.
    /// </summary>
    [Fact]
    public void EntryGuard_OnlySystemApp_DoesNotShortCircuit_RunsTheDownload()
    {
        File.WriteAllText(Path.Combine(_dir, "System.app"), "not a real NAVX package");
        var downloadCalled = false;

        var rc = ProgramSupport.ForceProvisionMode(
            "Microsoft platform apps", _dir, "28.1.49838.53910", force: false,
            isPresent: d => ProvisioningCheck.PlatformAppsPresent(d, "w1"),
            download: (v, d, log) =>
            {
                downloadCalled = true;
                foreach (var n in ProvisioningCheck.W1PlatformAppNames) WriteApp(n);
                return 0;
            });

        Assert.Equal(0, rc);
        Assert.True(downloadCalled,
            "a directory holding only System.app must not read as a complete provision (#2661)");
    }

    /// <summary>
    /// The inverse, so the guard is not simply "always download": a genuinely complete set
    /// short-circuits and the download never runs.
    /// </summary>
    [Fact]
    public void EntryGuard_CompleteSet_ShortCircuits_NeverCallsDownload()
    {
        WriteCompleteW1Set();
        var downloadCalled = false;

        var rc = ProgramSupport.ForceProvisionMode(
            "Microsoft platform apps", _dir, "28.1.49838.53910", force: false,
            isPresent: d => ProvisioningCheck.PlatformAppsPresent(d, "w1"),
            download: (v, d, log) => { downloadCalled = true; return 0; });

        Assert.Equal(0, rc);
        Assert.False(downloadCalled, "a complete set must not be re-downloaded");
    }

    /// <summary>
    /// #2558's post-download re-check, now biting for platform apps too: a download that
    /// reports rc == 0 without landing the core set must fail loudly rather than exit 0 over
    /// a short directory. Under the old glob this passed, because the partial download left
    /// one .app behind.
    /// </summary>
    [Fact]
    public void EntryGuard_DownloadReportsSuccessButLandsAShortSet_Fails()
    {
        var rc = ProgramSupport.ForceProvisionMode(
            "Microsoft platform apps", _dir, "28.1.49838.53910", force: false,
            isPresent: d => ProvisioningCheck.PlatformAppsPresent(d, "w1"),
            download: (v, d, log) =>
            {
                WriteApp("Base Application"); // one of five: a partial extraction
                return 0;
            });

        Assert.NotEqual(0, rc);
        // ...and the old glob would have accepted it, which is why rc used to be 0.
        Assert.True(Directory.EnumerateFiles(_dir, "*.app").Any());
    }

    /// <summary>
    /// The service-tier branch of the same helper, wired to ArtifactDirState (#2661's other
    /// half). A directory carrying Ncl.dll but short the closure sentinel — 27.5.46862.48827's
    /// exact shape — must not short-circuit, though the old `any *.dll exists` glob accepts it.
    /// </summary>
    [Fact]
    public void EntryGuard_ServiceTier_NclPresentButClosureShort_DoesNotShortCircuit()
    {
        foreach (var f in EngineClosure.CoreEngineDlls)
            File.WriteAllText(Path.Combine(_dir, f), "x");
        var downloadCalled = false;

        var rc = ProgramSupport.ForceProvisionMode(
            "BC service-tier engine DLLs", _dir, "27.5.46862.48827", force: false,
            isPresent: d => ArtifactDirState.Classify(d).IsUsable,
            download: (v, d, log) =>
            {
                downloadCalled = true;
                File.WriteAllText(Path.Combine(d, EngineClosure.ClosureSentinel), "x");
                return 0;
            });

        Assert.Equal(0, rc);
        Assert.True(downloadCalled,
            "82 DLLs incl. Ncl.dll but no closure sentinel is not a usable service tier (#3878)");
        // The old predicate's answer, so the two are visibly different.
        Assert.True(Directory.EnumerateFiles(_dir, "*.dll").Any());
    }

    [Fact]
    public void EntryGuard_ServiceTier_CompleteClosure_ShortCircuits_NeverCallsDownload()
    {
        foreach (var f in EngineClosure.CoreEngineDlls)
            File.WriteAllText(Path.Combine(_dir, f), "x");
        File.WriteAllText(Path.Combine(_dir, EngineClosure.ClosureSentinel), "x");
        var downloadCalled = false;

        var rc = ProgramSupport.ForceProvisionMode(
            "BC service-tier engine DLLs", _dir, "28.1.49838.53910", force: false,
            isPresent: d => ArtifactDirState.Classify(d).IsUsable,
            download: (v, d, log) => { downloadCalled = true; return 0; });

        Assert.Equal(0, rc);
        Assert.False(downloadCalled, "a complete engine closure must not be re-downloaded");
    }

    // ── the predicate must track the downloader, not a copy of its list ──────────────────

    /// <summary>
    /// The guard asks for exactly what the download promises. W1PlatformAppNames is derived
    /// from ArtifactDownloader.W1PlatformAppPrefixes; if a prefix is added there and no name
    /// here, the guard accepts a set that is short by the new app — a false green that no
    /// other test would catch, because every existing fixture would still be complete.
    /// </summary>
    [Fact]
    public void EveryDownloaderPrefixHasAMatchingName_AndViceVersa()
    {
        var prefixes = typeof(AlRunner.Provisioning.ArtifactDownloader)
            .GetField("W1PlatformAppPrefixes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as string[];

        Assert.NotNull(prefixes);
        Assert.Equal(prefixes!.Length, ProvisioningCheck.W1PlatformAppNames.Length);

        foreach (var name in ProvisioningCheck.W1PlatformAppNames)
        {
            var expected = $"microsoft_{name.ToLowerInvariant()}_";
            Assert.Contains(expected, prefixes);
        }
    }
}
