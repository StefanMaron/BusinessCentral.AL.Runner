// UnitClaimQueue — first come, first served hand-out of test codeunits between `--jobs` workers
// that were given the SAME bundle (#5130). See docs/jobs-unit-claiming.md.
//
// Claiming is a file create: the parent makes one directory, every worker names a codeunit by
// creating a file in it with FileMode.CreateNew, and exactly one create succeeds. No parent
// round trip, no worker protocol, and a worker that dies leaves only the claims it made.

using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

internal sealed class UnitClaimQueue
{
    /// <summary>The directory the parent made for claim files. Unset = no claiming.</summary>
    public const string DirEnvVar = "AL_RUNNER_UNIT_CLAIM_DIR";

    /// <summary>The bundles (full paths, `|`-separated) that more than one worker runs. A bundle
    /// not listed runs every codeunit, exactly as without --jobs splitting.</summary>
    public const string BundlesEnvVar = "AL_RUNNER_UNIT_CLAIM_BUNDLES";

    private readonly string _dir;
    private readonly string _bundleKey;

    public UnitClaimQueue(string dir, string bundleFullPath)
    {
        _dir = dir;
        _bundleKey = Normalize(bundleFullPath);
    }

    /// <summary>
    /// The queue for <paramref name="bundleFullPath"/>, or null when this process is not a worker
    /// of a split bundle. An unset directory is the ordinary case (null); a directory set with no
    /// bundle list is a malformed hand-off and throws, because null there would run every unit on
    /// every worker and report each test once per worker.
    /// </summary>
    public static UnitClaimQueue? ForBundle(string bundleFullPath)
        => ForBundle(bundleFullPath,
            Environment.GetEnvironmentVariable(DirEnvVar),
            Environment.GetEnvironmentVariable(BundlesEnvVar));

    /// <summary><see cref="ForBundle(string)"/> over explicit values, so the decision is testable
    /// without writing this process's environment.</summary>
    internal static UnitClaimQueue? ForBundle(string bundleFullPath, string? dir, string? list)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        if (string.IsNullOrEmpty(list))
            throw new InvalidOperationException(
                $"{DirEnvVar} is set but {BundlesEnvVar} is not: refusing to run, because a worker "
                + "that cannot tell which bundles are shared would run every test codeunit "
                + "itself and report each test once per worker.");
        var key = Normalize(bundleFullPath);
        foreach (var b in list.Split('|', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(Normalize(b), key, StringComparison.OrdinalIgnoreCase))
                return new UnitClaimQueue(dir, bundleFullPath);
        return null;
    }

    /// <summary>
    /// Claim one test codeunit of one app group of this bundle. True exactly once across every
    /// process sharing the directory. A failure to create the file that is NOT "someone else made
    /// it" throws: reading it as "already claimed" would drop the unit from the run, and reading
    /// it as "claimed" would run it on several workers.
    /// </summary>
    public bool TryClaim(string assemblyName, string codeunitTypeName)
    {
        var path = Path.Combine(_dir, ClaimFileName(_bundleKey, assemblyName, codeunitTypeName));
        try
        {
            using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    /// <summary>Whether some worker has already claimed that codeunit. A read, never a claim.</summary>
    public bool IsClaimed(string assemblyName, string codeunitTypeName)
        => File.Exists(Path.Combine(_dir, ClaimFileName(_bundleKey, assemblyName, codeunitTypeName)));

    internal static string ClaimFileName(string bundleKey, string assemblyName, string codeunitTypeName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{bundleKey}\n{assemblyName}\n{codeunitTypeName}"));
        return Convert.ToHexString(hash, 0, 16) + ".claim";
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p.TrimEnd('/', '\\'); }
    }
}
