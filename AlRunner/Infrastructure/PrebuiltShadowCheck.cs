// PrebuiltShadowCheck — decides whether a prebuilt .app found in the package cache is still
// a valid stand-in for an impl bundle's SOURCE.
//
// The layered pre-pass prefers a real, alc-built .app over in-process symbol synthesis,
// because BC's native .app scanner merges tableextensions correctly where our synthetic
// symbols.json does not (see RunLayeredPrePass). That preference is correct — but it was
// unconditional, matched on AppId alone. A stale .app in a project's .alpackages therefore
// shadowed the source directory the user passed on the command line, and the failure
// surfaced as a wall of AL0791 / AL0185 diagnostics against source that is perfectly valid.
//
// Observed on Pageworks: a months-old Stefan Maron Consulting_Pageworks_1.0.0.0.app produced
// 136 bogus "namespace 'Copilot' is unknown" / "Codeunit '...' is missing" errors, because the
// cached package predates that source. Deleting the .app made the same run compile and execute
// 1076 tests.
//
// The first version of this check compared MTIMES. That answers a different question. Git writes
// mtimes at CHECKOUT, so their ordering says which file was last touched on this machine, never
// which bytes are current — and it is wrong in both directions.
//
// False fresh: a `NaviPartner_NP_Retail_9999.9999.9999.9999.app` was newer than every .al in the
// sibling Application bundle, so the run compiled that package instead of the working tree and
// dropped 322 Pages/ControlAddins through `EMIT-FAIL … excluding and retrying` — a developer
// silently testing bytes they never wrote. False stale: a fresh clone, a worktree switch or a CI
// checkout rewrites every source mtime to "now", so a current package reads as stale and costs a
// full needless compile.
//
// So the verdict comes from CONTENT: the AL text the package ships under src/*.al against the AL
// text in the bundle. Mtime survives as the fallback for packages that carry no AL source at all,
// where there is nothing to compare.
//
// That only works because alc packages source verbatim, which was measured rather than assumed:
// a real alc-built NP_Retail package ships 7,058 src/*.al entries against 7,058 .al files in the
// bundle, and 7,057 are byte-identical once CRLF and a BOM are normalised away. The single
// differing file is a genuine edit — and it is the whole defect: one file of drift in a
// 7,058-file tree, which mtime scored as "package is current" and which cost 322 dropped objects.
// If alc ever started reformatting what it packages, every real package would mismatch, this
// check would answer "stale" for all of them, and the symptom would be a needless full compile
// (safe, slow) rather than a wrong answer.

using System.Text;

namespace AlRunner.Infrastructure;

internal static class PrebuiltShadowCheck
{
    /// <summary>
    /// Newest last-write time (UTC) across every <c>*.al</c> file under <paramref name="bundleDir"/>,
    /// recursively. <see cref="DateTime.MinValue"/> when the directory is missing, unreadable, or
    /// contains no AL source — in which case nothing can be newer than a prebuilt, so the prebuilt
    /// keeps winning.
    ///
    /// Only <c>*.al</c> counts: a build artifact, log, or editor swapfile dropped into the bundle
    /// must not read as "the source changed".
    /// </summary>
    public static DateTime NewestAlSourceUtc(string bundleDir)
    {
        try
        {
            if (!Directory.Exists(bundleDir)) return DateTime.MinValue;

            var newest = DateTime.MinValue;
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(bundleDir, "*.al"))
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Whether the bundle's source has changed since the prebuilt <c>.app</c> was written, i.e.
    /// the prebuilt is stale and must NOT be allowed to shadow the source.
    ///
    /// Equal timestamps keep the prebuilt: a freshly built .app and its sources routinely share
    /// a timestamp, and that is the normal "prebuilt is current" case.
    /// </summary>
    public static bool SourceIsNewer(DateTime prebuiltUtc, DateTime newestSourceUtc)
        => newestSourceUtc > prebuiltUtc;

    /// <summary>Whether the prebuilt is stale, and why — the text goes straight into the log line.</summary>
    public readonly record struct Verdict(bool Stale, string Reason);

    /// <summary>
    /// Content fingerprint of every <c>*.al</c> under <paramref name="bundleDir"/>, recursively,
    /// or null when there is no readable AL source — which means "nothing to compare", not
    /// "empty", and routes <see cref="Evaluate(string?, string?, DateTime, DateTime)"/> to the
    /// mtime fallback.
    ///
    /// <para>Hashes the MULTISET of file texts, not paths: a package flattens its sources to
    /// <c>src/&lt;filename&gt;.al</c> while the bundle keeps its directory layout, so the two
    /// sides are only comparable if layout is excluded. AL object identity lives in the source
    /// text (<c>codeunit 50100 "Foo"</c>), never in the filename, so this is the right
    /// granularity — a pure rename compiles to the same output and is correctly read as
    /// unchanged.</para>
    ///
    /// <para>Line endings and a leading BOM are normalised away, because git's autocrlf and
    /// editors rewrite both with no AL change and BC compiles either identically. Nothing beyond
    /// that is touched, so a genuine edit always registers.</para>
    /// </summary>
    public static string? SourceAlContentHash(string bundleDir)
    {
        try
        {
            if (!Directory.Exists(bundleDir)) return null;
            var perFile = new List<string>();
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(bundleDir, "*.al"))
                perFile.Add(HashAl(File.ReadAllText(file)));
            return Combine(perFile);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The same fingerprint over the AL a prebuilt <c>.app</c> ships under <c>src/*.al</c>, so it
    /// is directly comparable with <see cref="SourceAlContentHash"/>. Null for a package that
    /// carries no AL source (a symbols-only download) or that cannot be read as a package at all.
    /// </summary>
    public static string? PrebuiltAlContentHash(string appPath)
    {
        try
        {
            var packaged = AlRunner.AppLoader.ExtractAl(appPath);
            if (packaged.Count == 0) return null;
            return Combine(packaged.Select(s => HashAl(s.Source)).ToList());
        }
        // ExtractAl reads and unzips a third-party file, and a corrupt one can surface as almost
        // anything: a bad NAVX offset reaches the MemoryStream constructor as
        // ArgumentOutOfRangeException, a truncated zip as InvalidDataException, a bad byte
        // sequence as a decoder failure. All of them mean "no comparable content", i.e. the mtime
        // fallback, and none justifies ending a run because an unrelated .app in a package cache
        // is damaged. OutOfMemoryException is excluded deliberately: that is a resource failure,
        // not a malformed package, and reading it as "no content" would hide it behind a silently
        // different compile decision.
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// The staleness verdict for a prebuilt <c>.app</c> against the bundle it would shadow.
    /// </summary>
    public static Verdict Evaluate(string prebuiltAppPath, string bundleDir)
    {
        DateTime prebuiltUtc;
        try { prebuiltUtc = File.GetLastWriteTimeUtc(prebuiltAppPath); }
        catch (IOException) { prebuiltUtc = DateTime.MinValue; }
        catch (UnauthorizedAccessException) { prebuiltUtc = DateTime.MinValue; }

        // Source side first, and the package is only unzipped when there is something to compare
        // it against: with no AL under the bundle the verdict is the mtime fallback either way,
        // and reading the package would be waste. It is the expensive half — measured 389 ms for
        // a real 22 MB / 7,058-source ISV package — paid once per impl bundle that has a prebuilt
        // candidate, against an AL emit whose correctness it decides.
        var sourceHash = SourceAlContentHash(bundleDir);
        var prebuiltHash = sourceHash == null ? null : PrebuiltAlContentHash(prebuiltAppPath);

        return Evaluate(prebuiltHash, sourceHash, prebuiltUtc, NewestAlSourceUtc(bundleDir));
    }

    /// <summary>
    /// Content decides whenever both sides have content. Mtime is the fallback only for the shape
    /// where there is nothing to compare — a package carrying no AL source at all.
    /// </summary>
    public static Verdict Evaluate(string? prebuiltAlContentHash, string? sourceAlContentHash,
                                   DateTime prebuiltUtc, DateTime newestSourceUtc)
    {
        if (prebuiltAlContentHash != null && sourceAlContentHash != null)
        {
            var identical = string.Equals(prebuiltAlContentHash, sourceAlContentHash, StringComparison.Ordinal);
            return new Verdict(!identical,
                identical
                    ? "package src/*.al content is identical to the bundle's AL source"
                    : "package src/*.al content differs from the bundle's AL source");
        }

        var stale = SourceIsNewer(prebuiltUtc, newestSourceUtc);
        return new Verdict(stale,
            stale
                ? $"no AL source in the package to compare; source modified {newestSourceUtc:u} > package {prebuiltUtc:u}"
                : $"no AL source in the package to compare; package {prebuiltUtc:u} is at least as new as source");
    }

    // Spelled as an escape, not a literal: a raw U+FEFF in source is invisible in every editor and
    // the next reader cannot tell it from a stray keystroke.
    private const char Bom = '\uFEFF';

    private static string HashAl(string text)
    {
        var normalized = text.TrimStart(Bom).Replace("\r\n", "\n").Replace('\r', '\n');
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// One hash over the per-file hashes, sorted so neither directory-walk order nor the
    /// package's entry order can change the answer.
    /// </summary>
    private static string? Combine(List<string> perFileHashes)
    {
        if (perFileHashes.Count == 0) return null;
        perFileHashes.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', perFileHashes))));
    }
}
