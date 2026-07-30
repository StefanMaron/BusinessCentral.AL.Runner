using System;
using System.IO;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The layered pre-pass prefers a real, alc-built prebuilt .app over in-process symbol
/// synthesis, because BC's native .app scanner merges tableextensions correctly where our
/// synthetic symbols.json does not. That preference is right — but it used to be
/// unconditional, matched on AppId alone with no staleness check. A months-old .app sitting
/// in a project's .alpackages therefore beat the source directory the user passed on the
/// command line, and the failure surfaced as a wall of misleading AL0791/AL0185 diagnostics
/// against source that is perfectly valid (observed on Pageworks: 136 bogus errors).
///
/// These tests pin the staleness decision: prefer the prebuilt while it is at least as new
/// as the newest .al under the source bundle, and fall back to source once source is newer.
/// </summary>
public class PrebuiltShadowCheckTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PrebuiltNewerThanSource_IsNotStale_SoThePrebuiltStillWins()
    {
        var source = new DateTime(2026, 01, 01, 12, 00, 00, DateTimeKind.Utc);
        var prebuilt = source.AddHours(1);

        Assert.False(PrebuiltShadowCheck.SourceIsNewer(prebuilt, source));
    }

    [Fact]
    public void SourceNewerThanPrebuilt_IsStale_SoSourceMustWin()
    {
        var prebuilt = new DateTime(2026, 01, 01, 12, 00, 00, DateTimeKind.Utc);
        var source = prebuilt.AddSeconds(1);

        Assert.True(PrebuiltShadowCheck.SourceIsNewer(prebuilt, source));
    }

    [Fact]
    public void IdenticalTimestamps_KeepThePrebuilt()
    {
        // A freshly built .app and its sources routinely share a timestamp; that is the
        // normal "prebuilt is current" case and must NOT be treated as stale.
        var t = new DateTime(2026, 01, 01, 12, 00, 00, DateTimeKind.Utc);

        Assert.False(PrebuiltShadowCheck.SourceIsNewer(t, t));
    }

    [Fact]
    public void NewestAlSourceUtc_FindsTheNewestAlFileRecursively()
    {
        var dir = NewTempDir();
        try
        {
            var nested = Path.Combine(dir, "src", "Sub");
            Directory.CreateDirectory(nested);

            var older = Path.Combine(dir, "src", "A.al");
            File.WriteAllText(older, "codeunit 1 A { }");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));

            var newer = Path.Combine(nested, "B.al");
            File.WriteAllText(newer, "codeunit 2 B { }");
            var newest = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newer, newest);

            Assert.Equal(newest, PrebuiltShadowCheck.NewestAlSourceUtc(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NewestAlSourceUtc_IgnoresNonAlFiles()
    {
        var dir = NewTempDir();
        try
        {
            var al = Path.Combine(dir, "A.al");
            File.WriteAllText(al, "codeunit 1 A { }");
            var alTime = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(al, alTime);

            // A much newer non-.al file must not drag the answer forward — otherwise every
            // build artifact or log dropped in the folder would look like a source change.
            var other = Path.Combine(dir, "notes.txt");
            File.WriteAllText(other, "hello");
            File.SetLastWriteTimeUtc(other, new DateTime(2026, 12, 01, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(alTime, PrebuiltShadowCheck.NewestAlSourceUtc(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NewestAlSourceUtc_ReturnsMinValueWhenThereIsNoAlSource()
    {
        var dir = NewTempDir();
        try
        {
            // No .al anywhere => nothing can be newer than the prebuilt, so the prebuilt wins.
            Assert.Equal(DateTime.MinValue, PrebuiltShadowCheck.NewestAlSourceUtc(dir));
            Assert.False(PrebuiltShadowCheck.SourceIsNewer(
                new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
                PrebuiltShadowCheck.NewestAlSourceUtc(dir)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NewestAlSourceUtc_MissingDirectory_ReturnsMinValue()
    {
        Assert.Equal(DateTime.MinValue,
            PrebuiltShadowCheck.NewestAlSourceUtc(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"))));
    }
}
