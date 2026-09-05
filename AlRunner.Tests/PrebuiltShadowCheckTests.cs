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
        var dir = TestScratch.FlatDir("al-runner-shadow-");
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
            PrebuiltShadowCheck.NewestAlSourceUtc(TestScratch.FlatDir("definitely-not-here-")));
    }
}

/// <summary>
/// The staleness verdict moved from mtime to CONTENT (issue #2610).
///
/// Mtime answers a different question: git writes mtimes at checkout, so their ordering says
/// which file was last touched on this machine, not which bytes are current. It is wrong in both
/// directions, and the two directions cost different things — a false STALE is a needless full
/// compile, a false FRESH is a developer testing bytes they never wrote. Both are pinned below.
/// </summary>
public class PrebuiltShadowContentCheckTests
{
    private static string NewTempDir()
    {
        var dir = TestScratch.Dir("prebuilt-shadow-content");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string CodeunitA = "codeunit 50100 \"A\"\n{\n    procedure P(): Integer begin exit(1); end;\n}\n";
    private const string CodeunitB = "codeunit 50101 \"B\"\n{\n    procedure Q(): Integer begin exit(2); end;\n}\n";

    /// <summary>A bundle directory holding the given AL sources, in nested folders, because a real
    /// bundle has a directory layout and a package does not.</summary>
    private static string WriteBundle(params string[] sources)
    {
        var dir = NewTempDir();
        for (var i = 0; i < sources.Length; i++)
        {
            var sub = Path.Combine(dir, "src", "group" + i);
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, $"Object{i}.al"), sources[i]);
        }
        return dir;
    }

    /// <summary>A NAVX .app carrying the given AL under flat src/*.al entries, the way alc packages it.</summary>
    private static string WriteAppWithAl(params string[] sources)
    {
        var dir = NewTempDir();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("NavxManifest.xml");
            using (var s = manifest.Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + "<Package xmlns=\"http://schemas.microsoft.com/navx/2015/manifest\">"
                    + $"<App Id=\"{Guid.NewGuid()}\" Name=\"N\" Publisher=\"P\" Version=\"1.0.0.0\"/>"
                    + "<Dependencies/></Package>"));
            for (var i = 0; i < sources.Length; i++)
            {
                var e = zip.CreateEntry($"src/Packaged{i}.al");
                using var es = e.Open();
                es.Write(System.Text.Encoding.UTF8.GetBytes(sources[i]));
            }
        }
        var zipBytes = ms.ToArray();
        var bytes = new byte[8 + zipBytes.Length];
        bytes[0] = (byte)'N'; bytes[1] = (byte)'A'; bytes[2] = (byte)'V'; bytes[3] = (byte)'X';
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(bytes, 8);
        var path = Path.Combine(dir, "prebuilt.app");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- content decides, and it beats mtime in both directions ------------------

    /// <summary>
    /// The defect that motivated this. The package's mtime is NEWER than every source file, which
    /// the old check read as "package is current" — and its AL differs, so the run compiled bytes
    /// the developer never wrote.
    /// </summary>
    [Fact]
    public void PackageNewerThanSourceButDifferentContent_IsStale()
    {
        var bundle = WriteBundle(CodeunitA, CodeunitB);
        var app = WriteAppWithAl(CodeunitA, CodeunitB.Replace("exit(2)", "exit(999)"));
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddDays(1));

        var verdict = PrebuiltShadowCheck.Evaluate(app, bundle);

        Assert.True(verdict.Stale, "content differs, so the package must not shadow the source");
        Assert.Contains("differs", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction, which costs a needless full compile: a fresh clone or worktree switch
    /// rewrites every source mtime to now, so a package built from exactly this source reads as
    /// stale under mtime ordering. Its content is identical, so it is not.
    /// </summary>
    [Fact]
    public void PackageOlderThanSourceButIdenticalContent_IsNotStale()
    {
        var bundle = WriteBundle(CodeunitA, CodeunitB);
        var app = WriteAppWithAl(CodeunitA, CodeunitB);
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddDays(-30));

        var verdict = PrebuiltShadowCheck.Evaluate(app, bundle);

        Assert.False(verdict.Stale, "the package ships exactly this AL, so it is current whatever the mtimes say");
        Assert.Contains("identical", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>Line endings and a leading BOM are rewritten by git and editors with no AL change,
    /// and BC compiles either identically, so they must not read as drift.</summary>
    [Fact]
    public void CrlfAndBomDifferences_AreNotContentDrift()
    {
        var bundle = WriteBundle(CodeunitA, CodeunitB);
        var app = WriteAppWithAl("﻿" + CodeunitA.Replace("\n", "\r\n"), CodeunitB.Replace("\n", "\r\n"));

        Assert.False(PrebuiltShadowCheck.Evaluate(app, bundle).Stale);
    }

    /// <summary>Layout is excluded on purpose: a package flattens sources to src/&lt;name&gt;.al
    /// while the bundle keeps its folders, and AL object identity is in the source text, never in
    /// the filename. So a pure rename compiles to the same output and is not drift.</summary>
    [Fact]
    public void FileNamesAndFolders_DoNotAffectTheVerdict()
    {
        var bundle = NewTempDir();
        Directory.CreateDirectory(Path.Combine(bundle, "deeply", "nested"));
        File.WriteAllText(Path.Combine(bundle, "deeply", "nested", "TotallyDifferentName.al"), CodeunitA);
        File.WriteAllText(Path.Combine(bundle, "Another.al"), CodeunitB);

        Assert.False(PrebuiltShadowCheck.Evaluate(WriteAppWithAl(CodeunitB, CodeunitA), bundle).Stale);
    }

    /// <summary>An object added to the bundle and missing from the package is drift, and this is
    /// the case a hash over only the package's own files would miss.</summary>
    [Fact]
    public void AnObjectPresentInTheBundleAndMissingFromThePackage_IsStale()
    {
        var bundle = WriteBundle(CodeunitA, CodeunitB);
        var app = WriteAppWithAl(CodeunitA);
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddDays(1));

        Assert.True(PrebuiltShadowCheck.Evaluate(app, bundle).Stale);
    }

    /// <summary>And the mirror: an object deleted from the bundle but still shipped in the package.</summary>
    [Fact]
    public void AnObjectDeletedFromTheBundleButStillInThePackage_IsStale()
    {
        var bundle = WriteBundle(CodeunitA);
        var app = WriteAppWithAl(CodeunitA, CodeunitB);
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddDays(1));

        Assert.True(PrebuiltShadowCheck.Evaluate(app, bundle).Stale);
    }

    // ---- the mtime fallback, for the shape with nothing to compare ---------------

    /// <summary>
    /// A symbols-only package ships no src/*.al, so there is nothing to compare and mtime is all
    /// that is left. Both directions, so the fallback is not just "returns something".
    /// </summary>
    [Theory]
    // A package written 30 days ago against source written now: source is newer, so stale.
    [InlineData(-30, true)]
    // A package written after the source: not stale, and the prebuilt keeps winning.
    [InlineData(30, false)]
    public void PackageWithNoAlSource_FallsBackToMtimeOrdering(int packageAgeDays, bool expectedStale)
    {
        var bundle = WriteBundle(CodeunitA);
        var app = WriteAppWithAl(); // manifest only
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddDays(packageAgeDays));

        var verdict = PrebuiltShadowCheck.Evaluate(app, bundle);

        Assert.Equal(expectedStale, verdict.Stale);
        Assert.Contains("no AL source in the package", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A bundle with no AL at all cannot be compared either, and nothing can be newer
    /// than the package, so the package keeps winning — the pre-existing contract.</summary>
    [Fact]
    public void BundleWithNoAlSource_FallsBackToMtimeAndKeepsThePrebuilt()
    {
        var bundle = NewTempDir();
        var app = WriteAppWithAl(CodeunitA);

        var verdict = PrebuiltShadowCheck.Evaluate(app, bundle);

        Assert.False(verdict.Stale);
        Assert.Contains("no AL source in the package", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A damaged package in some unrelated package cache must not end the run. It has no
    /// comparable content, so the verdict falls back to mtime — and with source newer, to "stale",
    /// which means "compile from source", the safe direction.
    /// </summary>
    [Fact]
    public void UnreadablePackage_FallsBackToMtimeRatherThanThrowing()
    {
        var bundle = WriteBundle(CodeunitA);
        var dir = NewTempDir();
        var app = Path.Combine(dir, "corrupt.app");
        File.WriteAllBytes(app, System.Text.Encoding.UTF8.GetBytes("NAVX not really a package at all"));
        File.SetLastWriteTimeUtc(app, DateTime.UtcNow.AddDays(-30));

        var verdict = PrebuiltShadowCheck.Evaluate(app, bundle);

        Assert.True(verdict.Stale);
        Assert.Contains("no AL source in the package", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PrebuiltAlContentHash_IsNullForAPackageWithNoAlSource()
        => Assert.Null(PrebuiltShadowCheck.PrebuiltAlContentHash(WriteAppWithAl()));

    [Fact]
    public void SourceAlContentHash_IsNullForADirectoryWithNoAlSource()
        => Assert.Null(PrebuiltShadowCheck.SourceAlContentHash(NewTempDir()));

    [Fact]
    public void SourceAlContentHash_IsNullForAMissingDirectory()
        => Assert.Null(PrebuiltShadowCheck.SourceAlContentHash(
            TestScratch.FlatDir("no-such-dir-")));
}
