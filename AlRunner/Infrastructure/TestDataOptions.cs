// TestDataOptions — the `--test-data` opt-in, the backup it resolves to, and the identity
// that keeps a baseline captured WITHOUT test data from being reused by a run that asked
// FOR it.
//
// WHY THE IDENTITY EXISTS (issue #2258)
//   The dependency+company install baseline is cached twice — in-process by
//   TestExecutor._depCompanyBaselineCache, keyed by
//   InstallTriggerRunner.CurrentDependencySetKey(), and on disk by
//   InstallBaselineDiskCache.BuildKeyText(depKey, schemaVersion). Neither key knows anything
//   about test data. Left alone, a snapshot captured from an empty database is restored into
//   a run that passed --test-data and the run proceeds against an empty database with no
//   error anywhere — the exact silent-wrong-answer class .claude/rules/loud-failures.md
//   exists to prevent. So CacheIdentity() is folded into depKey BEFORE either tier is
//   consulted (see TestExecutor.Run).
//
//   It covers everything that can change the hydrated rows:
//     - which backup file (path, length, last-write time),
//     - which company inside it,
//     - which reader build decoded it (BackupReaderTool.ExtractorIdentity — a reader fix
//       that changes decoded VALUES must not be masked by a stale snapshot),
//     - this file's own hydration schema version.
//
// FLAG SHAPE
//   `--test-data` (use the backup shipped in the artifact cache) and `--test-data=PATH`
//   (use an explicit .bak). The equals form is deliberate: a space-separated optional value
//   cannot be told apart from the bundle path that follows it, so accepting one would make
//   `al-runner --test-data tests/foo` ambiguous. Adding more equals-form values later is not
//   a breaking change.
namespace AlRunner.Infrastructure;

/// <summary>Thrown when --test-data was asked for and cannot be honoured. Naming the paths
/// probed is the point: falling through to an empty database is what this replaces.</summary>
public sealed class TestDataUnavailableException : Exception
{
    public TestDataUnavailableException(string message) : base(message) { }
}

internal static class TestDataOptions
{
    /// <summary>Bump when the hydration changes which rows or values land in the store. An
    /// old on-disk baseline that still deserialises cleanly under new hydration semantics is
    /// the one failure mode a cache cannot detect for itself.
    ///
    /// 1 — #2258, the first slice: tables with `$ext` rows skipped whole.
    /// 2 — #2261, table-extension fields merged in. A version-1 baseline deserialises fine and
    ///     is silently missing every extension field of every extended table, so it must not
    ///     be reused.
    /// 3 — #2259, Date/DateTime/Time/DateFormula rebuilt instead of refused. A version-2
    ///     baseline deserialises fine and is silently missing every table one of those types
    ///     used to veto — 45 of the 54 still refusing after #2261.
    /// 4 — #2262, tables load ON DEMAND at first touch instead of eagerly before the install
    ///     triggers. A version-3 baseline holds every hydratable table's rows and would be
    ///     restored, in full, at every codeunit/test boundary — which is precisely the
    ///     per-boundary cost this version exists to stop paying. It deserialises fine, so
    ///     nothing but the version keeps a run from silently inheriting the eager cost.
    /// 5 — #2270 and #2268: Blob, Media, MediaSet, RecordId and Duration rebuilt, and a DB
    ///     NULL answered for every column type instead of only Text/Code. A version-4 baseline
    ///     deserialises fine and is silently missing every table one of those used to veto —
    ///     29 of the 41 still refusing after #2259.</summary>
    internal const int HydrationSchemaVersion = 5;

    /// <summary>Off unless --test-data was passed. Absent the flag NOTHING here runs: no
    /// backup is opened, no reader is located, and CacheIdentity() returns the empty string
    /// so the install-baseline cache key is byte-identical to what it was before #2258.</summary>
    internal static bool Enabled { get; set; }

    /// <summary>Explicit `.bak` from `--test-data=PATH`; null means "resolve from the
    /// artifact cache".</summary>
    internal static string? ExplicitBackupPath { get; set; }

    /// <summary>Company to hydrate. Null means "the first company the backup reports", which
    /// is logged at hydration time — a stated choice, not a guess about which company matters.</summary>
    internal static string? CompanyOverride { get; set; }

    private static string? _cachedIdentity;

    internal static void ResetForTests()
    {
        Enabled = false;
        ExplicitBackupPath = null;
        CompanyOverride = null;
        _cachedIdentity = null;
    }

    /// <summary>
    /// Parse one argument. Returns false when <paramref name="arg"/> is not a --test-data
    /// form, so the caller's flag loop is unchanged for every other flag.
    /// </summary>
    internal static bool TryParseArg(string arg)
    {
        if (arg == "--test-data") { Enabled = true; ExplicitBackupPath = null; return true; }
        if (arg.StartsWith("--test-data=", StringComparison.Ordinal))
        {
            Enabled = true;
            var value = arg["--test-data=".Length..];
            ExplicitBackupPath = string.IsNullOrWhiteSpace(value) ? null : value;
            return true;
        }
        return false;
    }

    /// <summary>The `.bak` filename BC's sandbox artifact ships for a country channel:
    /// <c>BusinessCentral-W1.bak</c>, <c>BusinessCentral-US.bak</c>, … Pure over the country
    /// so it is testable without an artifact on disk.</summary>
    internal static string BackupFileName(string country)
        => $"BusinessCentral-{(string.IsNullOrWhiteSpace(country) ? "W1" : country.Trim().ToUpperInvariant())}.bak";

    /// <summary>
    /// Every location <see cref="ResolveBackupPath"/> probes for the shipped backup, in
    /// order. The BcContainerHelper sandbox cache is first because that is the layout the
    /// artifact is published in (<c>sandbox/&lt;version&gt;/&lt;country&gt;/</c>); the
    /// runner's own artifacts root is probed second so a future
    /// <c>provision --test-data</c> writing there needs no change here.
    /// </summary>
    internal static IReadOnlyList<string> CandidateBackupPaths(
        string? home, string? runnerArtifactsRoot, string version, string country)
    {
        var file = BackupFileName(country);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(home))
            candidates.Add(Path.Combine(home, ".bcartifacts.cache", "sandbox", version, country, file));
        if (!string.IsNullOrEmpty(runnerArtifactsRoot))
            candidates.Add(Path.Combine(runnerArtifactsRoot, version, country, file));
        return candidates;
    }

    /// <summary>The backup this run hydrates from. Throws — naming every probed path — rather
    /// than returning null: a missing backup under --test-data must never degrade into a run
    /// against an empty database.</summary>
    internal static string ResolveBackupPath()
    {
        if (ExplicitBackupPath != null)
        {
            var explicitPath = Path.GetFullPath(ExplicitBackupPath);
            if (File.Exists(explicitPath)) return explicitPath;
            throw new TestDataUnavailableException(
                $"--test-data={ExplicitBackupPath}: no such file (looked for '{explicitPath}').");
        }

        var version = BcArtifacts.SelectedVersion.ToString();
        var country = BcArtifacts.SelectedCountry;
        string? home = null;
        try { home = AlRunnerPaths.UserHome; } catch { /* named in the message below */ }

        var candidates = CandidateBackupPaths(home, BcArtifacts.ArtifactsRootDir, version, country);
        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        // Everything actionable on the FIRST line: the bundle reporter keeps only line 1 of
        // an EXEC-FAIL message, so a "Probed:" list on line 3 never reaches the user.
        throw new TestDataUnavailableException(
            $"--test-data: no BC backup for BC {version} ({country}) at any of "
            + string.Join(" or ", candidates.Select(c => $"'{c}'"))
            + $" — it ships inside the BC sandbox artifact as sandbox/{version}/{country}/{BackupFileName(country)}; "
            + "pass an explicit one with --test-data=/path/to/BusinessCentral-W1.bak.");
    }

    /// <summary>
    /// The identity folded into the install-baseline cache key. Empty string when --test-data
    /// is off, so the key is unchanged for every run that does not opt in.
    /// </summary>
    internal static string CacheIdentity()
    {
        if (!Enabled) return "";
        return _cachedIdentity ??= BuildCacheIdentity(
            ResolveBackupPath(), CompanyOverride, BackupReaderTool.ExtractorIdentity());
    }

    /// <summary>
    /// Pure identity composition, so the "different backup / different company / different
    /// reader build must produce a different key" claim is testable without a 900 MB backup
    /// or a reader binary. File identity is (full path, length, last-write UTC ticks) rather
    /// than a content hash: the backup is ~1 GB and re-hashing it on every run would cost far
    /// more than the whole hydration it guards.
    /// </summary>
    internal static string BuildCacheIdentity(string backupPath, string? company, string extractorIdentity)
    {
        var full = Path.GetFullPath(backupPath);
        long length = -1;
        long writeTicks = -1;
        var info = new FileInfo(full);
        if (info.Exists) { length = info.Length; writeTicks = info.LastWriteTimeUtc.Ticks; }
        var payload = string.Join('|',
            "testdata", HydrationSchemaVersion.ToString(), full, length.ToString(),
            writeTicks.ToString(), company ?? "<first>", extractorIdentity);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return "td" + Convert.ToHexString(
            sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)))[..16];
    }
}
