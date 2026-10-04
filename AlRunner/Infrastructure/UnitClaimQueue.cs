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
    private readonly UnitClaimLedger? _ledger;

    public UnitClaimQueue(string dir, string bundleFullPath, UnitClaimLedger? ledger = null)
    {
        _dir = dir;
        _bundleKey = Normalize(bundleFullPath);
        _ledger = ledger;
    }

    /// <summary>
    /// The queue for <paramref name="bundleFullPath"/>, or null when this process is not a worker
    /// of a split bundle. An unset directory is the ordinary case (null); a directory set with no
    /// bundle list is a malformed hand-off and throws, because null there would run every unit on
    /// every worker and report each test once per worker.
    /// </summary>
    public static UnitClaimQueue? ForBundle(string bundleFullPath, UnitClaimLedger? ledger = null)
        => ForBundle(bundleFullPath,
            Environment.GetEnvironmentVariable(DirEnvVar),
            Environment.GetEnvironmentVariable(BundlesEnvVar), ledger);

    /// <summary><see cref="ForBundle(string)"/> over explicit values, so the decision is testable
    /// without writing this process's environment.</summary>
    internal static UnitClaimQueue? ForBundle(string bundleFullPath, string? dir, string? list,
        UnitClaimLedger? ledger = null)
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
                return new UnitClaimQueue(dir, bundleFullPath, ledger);
        return null;
    }

    /// <summary>
    /// Claim one test codeunit of one app group of this bundle. True exactly once across every
    /// process sharing the directory. A failure to create the file that is NOT "someone else made
    /// it" throws: reading it as "already claimed" would drop the unit from the run, and reading
    /// it as "claimed" would run it on several workers.
    /// </summary>
    public bool TryClaim(string assemblyName, string codeunitTypeName)
        => TryCreate(assemblyName, codeunitTypeName, record: true);

    /// <summary><paramref name="record"/>: tell the ledger, so --tdd's re-run can give the claim back
    /// (<see cref="UnitClaimLedger"/>). A dropped object's claim is not recorded: its owner keeps it across the
    /// re-run (<see cref="ClaimDropped(string, IReadOnlyList{TddExcludedObjectDetail}, ISet{string})"/>).</summary>
    private bool TryCreate(string assemblyName, string codeunitTypeName, bool record)
    {
        var path = Path.Combine(_dir, ClaimFileName(_bundleKey, assemblyName, codeunitTypeName));
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            if (record) _ledger?.Won(path);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    /// <summary>
    /// Claim a test codeunit the COMPILE dropped (EMIT-EXCLUDED, #5256). It never reaches the run,
    /// so <see cref="TryClaim"/> never sees it, yet every worker of a shared bundle compiles the
    /// bundle and finds the same drop: the claim is what makes exactly one of them report its
    /// tests as SKIPPED. The prefix keeps the key apart from a real codeunit's type name.
    /// </summary>
    public bool TryClaimDropped(string moduleName, string droppedObjectKey)
        => TryCreate(moduleName, DroppedClaimType(droppedObjectKey), record: false);

    private static string DroppedClaimType(string droppedObjectKey) => "emit-excluded:" + droppedObjectKey;
    private static string DroppedObjectKey(TddExcludedObjectDetail d) => d.FilePath + "|" + d.ObjectDisplayName;

    /// <summary>The dropped objects of <paramref name="details"/> this worker claimed and so
    /// reports; the others belong to the worker that claimed them. One claim per object, so
    /// several dropped objects may fall to different workers and are still reported once each.</summary>
    internal IReadOnlyList<TddExcludedObjectDetail> ClaimDropped(
        string moduleName, IReadOnlyList<TddExcludedObjectDetail> details)
        => details.Where(d => TryClaimDropped(moduleName, DroppedObjectKey(d))).ToList();

    /// <summary>
    /// <see cref="ClaimDropped(string, IReadOnlyList{TddExcludedObjectDetail})"/> for a process that compiles the
    /// same bundle more than once (--tdd's re-run, #5037, throws a pass's rows away and compiles again): the claim
    /// file a first pass created answers "exists" to the second, so without <paramref name="ownedByThisProcess"/>
    /// the worker that won an object would lose it and nobody would report its rows. The set holds the claim
    /// files this process created and is the caller's, for the life of the process. A resumed attempt is another
    /// process and does not own them: its carry holds the rows (ResumeCarry).
    /// </summary>
    internal IReadOnlyList<TddExcludedObjectDetail> ClaimDropped(
        string moduleName, IReadOnlyList<TddExcludedObjectDetail> details, ISet<string> ownedByThisProcess)
        => details.Where(d =>
        {
            var key = DroppedObjectKey(d);
            var file = ClaimFileName(_bundleKey, moduleName, DroppedClaimType(key));
            if (ownedByThisProcess.Contains(file)) return true;
            if (!TryClaimDropped(moduleName, key)) return false;
            ownedByThisProcess.Add(file);
            return true;
        }).ToList();

    /// <summary>The lock the workers of this shared bundle serialise their compile phase on
    /// (<see cref="CompilePhase"/>). Beside the claim files, so it dies with the run's scratch.</summary>
    internal string CompilePhaseLockPath
        => Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_bundleKey)), 0, 16)
            + ".compile-phase.lock");

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

/// <summary>
/// The claim files of test codeunits this process created and has not yet reported (#5326). --tdd's re-run
/// (#5037) throws a pass's results away and runs the cycle again; a claim that pass made answers "exists" to
/// the next, so the codeunit it ran was skipped and reported by nobody. <see cref="Release"/> deletes exactly the
/// files this process made, at the moment it discards a pass: the codeunit is free again, so the re-run (or a
/// peer that gets there first) claims it once and reports it once, and a claim another worker holds is never
/// touched. A resumed attempt is another process with an empty ledger. Dropped objects' claims are not here
/// (their owner keeps them, <see cref="UnitClaimQueue.ClaimDropped(string, IReadOnlyList{TddExcludedObjectDetail}, ISet{string})"/>).
/// </summary>
internal sealed class UnitClaimLedger
{
    private readonly List<string> _won = new();

    internal void Won(string claimFile) { lock (_won) _won.Add(claimFile); }

    /// <summary>Delete every claim file this process created since the last release; returns how many.
    /// A file that cannot be deleted throws: leaving it would make the re-run skip a codeunit nobody reports.</summary>
    internal int Release()
    {
        string[] files;
        lock (_won) { files = _won.ToArray(); _won.Clear(); }
        foreach (var f in files)
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"--tdd: could not release the claim file {f} of a test codeunit the discarded pass ran ({ex.Message}); "
                    + "the re-run would find it claimed and skip the codeunit, which no worker would then report.", ex);
            }
        }
        return files.Length;
    }
}
