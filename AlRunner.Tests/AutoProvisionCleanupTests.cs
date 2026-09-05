// Issue #2559: a failed `ProvisioningCheck.AutoProvision` used to leave an EMPTY
// `<artifacts>/<version>/` directory behind — created before the download attempt,
// never removed on failure. That empty directory then reads as a candidate version to
// `BcArtifacts.SelectArtifactVersionDir`, which picks the highest version-named directory
// purely by name, with no completeness check — so it can outrank an older directory that
// is actually complete and usable.
//
// AutoProvision now takes the downloader as an injectable seam (default:
// AlRunner.Provisioning.ArtifactDownloader.ServiceTier) so this is testable with no
// network access, and removes the target directory again on failure but ONLY when it was
// left completely empty — a partial download (files landed, then failed) is left alone
// because deleting it would throw away bytes the user already paid to fetch, and because
// the download threw was previously left to propagate uncontained.
//
// These are pure in-process unit tests against the static method directly — no subprocess,
// no fixture app.json, no BC apps involved at all — so the "application" floor guard
// (AlRunner.Tests/BaseAppFloorFixtureGuardTests.cs) does not apply here.

using System;
using System.IO;
using System.Linq;
using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class AutoProvisionCleanupTests : IDisposable
{
    private readonly string _artifactsRoot;

    public AutoProvisionCleanupTests()
    {
        _artifactsRoot = TestScratch.Dir("al-runner-autoprov");
        Directory.CreateDirectory(_artifactsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_artifactsRoot, recursive: true); } catch { }
    }

    private string VersionDir(string version) => Path.Combine(_artifactsRoot, version);

    // ── Positive: a failure that left the directory EMPTY removes it ───────────────────

    [Fact]
    public void AutoProvision_DownloaderReturnsNonZero_WritesNothing_RemovesEmptyVersionDir()
    {
        var dir = VersionDir("28.9.99999.0");

        var ok = ProvisioningCheck.AutoProvision(
            "28.9.99999.0", dir, log: _ => { },
            downloader: (_, outputDir, _) =>
            {
                // Mirrors ArtifactDownloader.ServiceTier's own first line: it creates the
                // dir before doing any network I/O, then fails before writing any file.
                Directory.CreateDirectory(outputDir);
                return 1; // non-zero: download failed
            });

        Assert.False(ok);
        Assert.False(Directory.Exists(dir), "an empty version directory left by a failed download must be removed");
    }

    [Fact]
    public void AutoProvision_DownloaderThrows_ExceptionContained_RemovesEmptyVersionDir()
    {
        var dir = VersionDir("28.9.99999.1");

        var ok = ProvisioningCheck.AutoProvision(
            "28.9.99999.1", dir, log: _ => { },
            downloader: (_, outputDir, _) =>
            {
                Directory.CreateDirectory(outputDir);
                throw new InvalidOperationException("network exploded");
            });

        Assert.False(ok);
        Assert.False(Directory.Exists(dir), "an exception from the downloader must be contained, not propagated, and must still trigger empty-dir cleanup");
    }

    [Fact]
    public void AutoProvision_DownloadReturnsZeroButClosureIncomplete_RemovesEmptyVersionDir()
    {
        var dir = VersionDir("28.9.99999.2");

        var ok = ProvisioningCheck.AutoProvision(
            "28.9.99999.2", dir, log: _ => { },
            downloader: (_, outputDir, _) =>
            {
                // rc == 0 ("success") but nothing was actually written — the exact shape
                // #2558's sibling issue describes for the test-toolkit downloader.
                Directory.CreateDirectory(outputDir);
                return 0;
            });

        Assert.False(ok);
        Assert.False(Directory.Exists(dir), "a closure that is still incomplete after a claimed-successful download must not leave an empty dir behind");
    }

    // ── Negative: a PARTIAL (non-empty) failed download is preserved, not wiped ────────

    [Fact]
    public void AutoProvision_PartialDownloadThenFailure_DirectorySurvivesWithItsFiles()
    {
        var dir = VersionDir("28.9.99999.3");

        var ok = ProvisioningCheck.AutoProvision(
            "28.9.99999.3", dir, log: _ => { },
            downloader: (_, outputDir, _) =>
            {
                Directory.CreateDirectory(outputDir);
                // One real file landed before the download failed partway through.
                File.WriteAllText(Path.Combine(outputDir, "Microsoft.Dynamics.Nav.Ncl.dll"), "partial");
                return 1;
            });

        Assert.False(ok);
        Assert.True(Directory.Exists(dir), "a partial download must never be deleted — those are resumable bytes");
        Assert.True(File.Exists(Path.Combine(dir, "Microsoft.Dynamics.Nav.Ncl.dll")));
    }

    // ── Positive: success leaves the directory in place with its real content ──────────

    [Fact]
    public void AutoProvision_CompleteClosureDownloaded_ReturnsTrue_DirectorySurvives()
    {
        var dir = VersionDir("28.9.99999.4");
        var closureFiles = new[]
        {
            "Microsoft.Dynamics.Nav.Ncl.dll",
            "Microsoft.Dynamics.Nav.Types.dll",
            "Microsoft.Dynamics.Nav.Common.dll",
            "Microsoft.Dynamics.Nav.Language.dll",
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
            "Microsoft.Identity.ServiceEssentials.Core.dll",
        };

        var ok = ProvisioningCheck.AutoProvision(
            "28.9.99999.4", dir, log: _ => { },
            downloader: (_, outputDir, _) =>
            {
                Directory.CreateDirectory(outputDir);
                foreach (var f in closureFiles) File.WriteAllText(Path.Combine(outputDir, f), "x");
                return 0;
            });

        Assert.True(ok);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(closureFiles.Length, Directory.EnumerateFiles(dir).Count());
    }

    // ── Second-order effect (#2559's own claim): an empty leftover would have outranked
    // a real, complete, older-patch directory. Prove the fix removes that possibility by
    // asserting SelectArtifactVersionDir picks the complete OLDER dir once the failed
    // NEWER attempt's empty dir is gone. ─────────────────────────────────────────────────

    [Fact]
    public void AutoProvision_EmptyLeftoverRemoved_OlderCompleteVersionStillSelectable()
    {
        var older = VersionDir("28.9.1.0");
        Directory.CreateDirectory(older);
        foreach (var f in new[]
        {
            "Microsoft.Dynamics.Nav.Ncl.dll",
            "Microsoft.Dynamics.Nav.Types.dll",
            "Microsoft.Dynamics.Nav.Common.dll",
            "Microsoft.Dynamics.Nav.Language.dll",
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
            "Microsoft.Identity.ServiceEssentials.Core.dll",
        }) File.WriteAllText(Path.Combine(older, f), "x");

        var newerDir = VersionDir("28.9.2.0");
        var ok = ProvisioningCheck.AutoProvision(
            "28.9.2.0", newerDir, log: _ => { },
            downloader: (_, outputDir, _) =>
            {
                Directory.CreateDirectory(outputDir);
                return 1; // fails, empty
            });
        Assert.False(ok);
        Assert.False(Directory.Exists(newerDir));

        // Without the fix, newerDir (higher version, empty) would still exist here and
        // SelectArtifactVersionDir — which orders purely by parsed Version, no
        // completeness check — would pick it over the real, usable `older` dir.
        var selected = BcArtifacts.SelectArtifactVersionDir(_artifactsRoot, requestedVersionOrNull: null);
        Assert.Equal(older, selected);
    }
}
