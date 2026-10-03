// CacheCompileLock — one process compiles a cache key, the others wait and take the HIT (#5238).
//
// AlCacheWriter makes concurrent writers of one key CORRECT (byte-identical output, atomic
// rename), not CHEAP: every process that misses compiles. `--jobs` workers sharing a bundle
// start together, so on a cold cache they all miss together and each pays the whole compile.
// The cost is CPU and memory, not wall time, which is why the clock does not show it.
//
// The gate is a per-key lock file beside the entry, held from "the entry was missing" to "the
// entry is published". A waiter takes the lock when the holder lets go and then reads the entry
// again, which is a HIT when the holder finished. Contract, each pinned by CacheCompileLockTests:
//   * the lock is an OS file lock (FileShare.None), so a killed holder releases it with its
//     process: a stale lock cannot exist, only a live holder that is slow;
//   * the wait is bounded (WaitSecEnvVar, default DefaultMaxWait). On expiry the waiter says so
//     and compiles itself, which is the pre-gate behaviour: slower, never wrong;
//   * a lock that cannot be taken for a reason other than "held" says so and compiles, rather
//     than reading as "nobody else is compiling";
//   * the lock file is never deleted: unlinking a file another process has already opened lets a
//     third process lock a different inode of the same name.

namespace AlRunner.Infrastructure;

internal sealed class CacheCompileLock : IDisposable
{
    public const string WaitSecEnvVar = "AL_RUNNER_CACHE_LOCK_WAIT_SEC";

    /// <summary>Longer than any measured cold compile (a 293-file BaseApp bucket took 301 s).</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(30);

    /// <summary>A wait shorter than this prints nothing: most are a dependency of a second or two.</summary>
    public static readonly TimeSpan AnnounceAfter = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(100);

    private FileStream? _held;

    private CacheCompileLock(FileStream? held, bool waited, string? degraded)
    {
        _held = held;
        WaitedForSibling = waited;
        DegradedReason = degraded;
    }

    /// <summary>Another process held the key's lock when this one asked. The entry may exist now:
    /// read it again before compiling.</summary>
    public bool WaitedForSibling { get; }

    /// <summary>Set when this lock does NOT exclude anyone (the wait expired, or the lock file
    /// could not be used); the caller then compiles exactly as it did before the gate existed.</summary>
    public string? DegradedReason { get; }

    public bool HoldsLock => _held != null;

    /// <summary>
    /// Take <paramref name="lockPath"/>, waiting while another process or thread holds it.
    /// Never throws for a locking problem and never waits past <paramref name="maxWait"/>.
    /// </summary>
    /// <param name="what">Names the thing being compiled, for the two lines this can print.</param>
    /// <param name="say">Where those lines go (stderr in the runner).</param>
    public static CacheCompileLock Acquire(string lockPath, string what, TimeSpan maxWait, Action<string> say)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var waited = false;
        var announced = false;
        try
        {
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return Degraded(lockPath, what, say, ex, waited: false);
        }
        while (true)
        {
            try
            {
                var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1);
                return new CacheCompileLock(fs, waited, degraded: null);
            }
            catch (Exception ex) when (IsHeldElsewhere(ex))
            {
                waited = true;
            }
            catch (Exception ex)
            {
                return Degraded(lockPath, what, say, ex, waited);
            }

            if (clock.Elapsed >= maxWait)
            {
                var reason = $"waited {clock.Elapsed.TotalSeconds:F0}s for another process to finish compiling {what}";
                say($"  [cache] {reason} (limit {maxWait.TotalSeconds:F0}s, {WaitSecEnvVar}); compiling it here "
                    + "instead. The other process is still holding the lock: if it is hung, kill it.");
                return new CacheCompileLock(null, waited, reason);
            }
            if (!announced && clock.Elapsed >= AnnounceAfter)
            {
                announced = true;
                say($"  [cache] another process is compiling {what}; waiting for it (up to {maxWait.TotalSeconds:F0}s) "
                    + "and then reading the result from the cache");
            }
            Thread.Sleep(PollEvery);
        }
    }

    private static CacheCompileLock Degraded(string lockPath, string what, Action<string> say, Exception ex, bool waited)
    {
        var reason = $"could not take the compile lock {lockPath}: {ex.GetType().Name}: {ex.Message}";
        say($"  [cache] {reason} — compiling {what} without it, so a process compiling the same key "
            + "at the same time is not waited for");
        return new CacheCompileLock(null, waited, reason);
    }

    /// <summary>The wait to use: <see cref="WaitSecEnvVar"/> when it is a positive whole number of
    /// seconds, else <see cref="DefaultMaxWait"/>. A value that is set and unusable is named, not
    /// skipped silently.</summary>
    public static TimeSpan MaxWaitFromEnvironment(Action<string> say)
    {
        var raw = Environment.GetEnvironmentVariable(WaitSecEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultMaxWait;
        if (int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0)
            return TimeSpan.FromSeconds(s);
        say($"  [cache] {WaitSecEnvVar}='{raw}' is not a positive whole number of seconds; using "
            + $"{DefaultMaxWait.TotalSeconds:F0}s");
        return DefaultMaxWait;
    }

    /// <summary>True for exactly the exception a FileShare conflict raises: Windows reports a
    /// sharing or lock violation (32, 33), Unix reports EWOULDBLOCK from the flock .NET takes for
    /// FileShare.None (11 on Linux, 35 on macOS). Anything else, including the missing-path
    /// subclasses of IOException, is a lock that cannot be used and must not be polled for.</summary>
    internal static bool IsHeldElsewhere(Exception ex)
    {
        if (ex is not IOException) return false;
        if (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException
            or DriveNotFoundException or EndOfStreamException) return false;
        return OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 32 or 33 : ex.HResult is 11 or 35;
    }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _held, null);
        held?.Dispose();
    }
}
