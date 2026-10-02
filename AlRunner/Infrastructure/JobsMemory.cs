// JobsMemory — how much memory a `--jobs` fan-out may spend, and what one worker is expected to
// cost (#5130, #5216). A worker of a shared bundle loads ALL of it, so splitting one bundle
// across N workers multiplies that bundle's memory by N; this is what limits N.
//
// The per-worker figure is a measured fit, not a guess: docs/jobs-unit-claiming.md § "Memory"
// holds the runs it came from and how to repeat them.

namespace AlRunner.Infrastructure;

internal static class JobsMemory
{
    /// <summary>Overrides the free-memory reading, in MB. Set it where the reading is wrong (a box
    /// shared with other jobs) or absent (a platform this cannot read).</summary>
    public const string FreeMemoryEnvVar = "AL_RUNNER_JOBS_FREE_MEMORY_MB";

    /// <summary>Peak memory of one worker, as <c>BaseMb + CoeffMb * tests^Exponent</c>, tests being
    /// the number it runs. The base is what a worker costs before it runs anything (BC's runtime, the
    /// loaded bundle, the test-data company, its backup-reader sidecar). The rest is what running
    /// tests leaves behind, and it grows with an exponent below 1: the first tests touch the tables
    /// and metadata that later ones reuse.</summary>
    public sealed record WorkerModel(long BaseMb, double CoeffMb, double Exponent)
    {
        public long EstimateBytes(double testsRun)
            => (BaseMb + (long)Math.Ceiling(CoeffMb * Math.Pow(Math.Max(0, testsRun), Exponent))) * 1024 * 1024;
    }

    /// <summary>Fraction of the free memory a plan may claim. The same bucket peaked 20% apart
    /// between repeats, and the box has other users, so the plan keeps a margin.</summary>
    public const double Headroom = 0.8;

    /// <summary>The least-squares fit in docs/jobs-unit-claiming.md § Memory, on peak proportional set
    /// size (shared pages counted once, not per worker): 1,370 MB for a worker that has run nothing,
    /// whatever the bundle's size, and 6.9 MB x tests^0.68 on top. The measured runs sit between
    /// -16% and +18% of it, which is bucket-to-bucket spread that test count does not explain, so the
    /// plan keeps its headroom. JobsMemoryModelTests holds the runs and the band.</summary>
    public static readonly WorkerModel Model = new(BaseMb: 1370, CoeffMb: 6.9, Exponent: 0.68);

    private static readonly Lazy<long?> Reading = new(Read);

    /// <summary>Free memory in bytes, read once per process so every planning call in one run sees
    /// the same number, or null when it cannot be read. Null is "unknown", and the caller does not
    /// size anything from it: an unknown reading must not be read as plenty or as none.</summary>
    public static long? FreeBytes() => Reading.Value;

    private static long? Read()
    {
        var overrideBytes = ParseFreeMemoryOverride(
            Environment.GetEnvironmentVariable(FreeMemoryEnvVar), out var warning);
        // A value that was set but is not usable is said, never silently dropped: the one who set it
        // meant to change what the plan sees.
        if (warning != null) Console.Error.WriteLine($"jobs: {warning}");
        if (overrideBytes != null) return overrideBytes;
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return Combine(ParseMemAvailableBytes(File.ReadAllText("/proc/meminfo")), ReadCgroupRemainingBytes());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The override in bytes. Unset or empty is simply "not set" (null, no warning); anything
    /// else that is not a positive whole number of MB (<c>8GB</c>, <c>0</c>, <c>-1</c>) is null with a
    /// warning naming it, because 0 and a typo would otherwise fall back to the machine's reading.</summary>
    internal static long? ParseFreeMemoryOverride(string? value, out string? warning)
    {
        warning = null;
        if (string.IsNullOrEmpty(value)) return null;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mb)
            && mb > 0)
            return mb * 1024 * 1024;
        warning = $"ignoring {FreeMemoryEnvVar}='{value}': it must be a positive whole number of MB. "
            + "Reading the machine instead (on a platform that cannot, sharing is not sized).";
        return null;
    }

    /// <summary>The host's available memory limited by the cgroup's remaining memory: the smaller of
    /// the two, since either one is where a worker hits the wall. An unknown host reading is unknown
    /// whatever the cgroup says; an unknown cgroup limit leaves the host reading.</summary>
    internal static long? Combine(long? hostAvailable, long? cgroupRemaining)
        => hostAvailable == null ? null
            : cgroupRemaining == null ? hostAvailable
            : Math.Min(hostAvailable.Value, cgroupRemaining.Value);

    /// <summary>The tightest (smallest) of the remaining-memory figures along a cgroup path: a limit on
    /// any ancestor binds the worker. Null when none of them is known.</summary>
    internal static long? Tightest(IEnumerable<long?> remaining)
    {
        long? tightest = null;
        foreach (var r in remaining)
            if (r != null && (tightest == null || r < tightest)) tightest = r;
        return tightest;
    }

    /// <summary><c>MemAvailable</c> from /proc/meminfo text, in bytes; null when absent or not a number.</summary>
    internal static long? ParseMemAvailableBytes(string meminfo)
    {
        foreach (var line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) && kb >= 0 ? kb * 1024 : null;
        }
        return null;
    }

    /// <summary>What a cgroup v2 limit leaves: <c>memory.max</c> minus <c>memory.current</c>. Null for
    /// "max" (no limit) or anything unreadable; the host's reading then stands.</summary>
    internal static long? CgroupRemainingBytes(string? maxText, string? currentText)
    {
        if (maxText == null || currentText == null) return null;
        if (!long.TryParse(maxText.Trim(), out var max)) return null;   // "max" parses to nothing
        if (!long.TryParse(currentText.Trim(), out var current)) return null;
        return Math.Max(0, max - current);
    }

    /// <summary>The cgroup v2 directories this process sits in, deepest first, from the
    /// <c>0::/path</c> line of /proc/self/cgroup. A limit on any ancestor binds the worker too.</summary>
    internal static IReadOnlyList<string> CgroupDirectories(string procSelfCgroup, string root = "/sys/fs/cgroup")
    {
        foreach (var line in procSelfCgroup.Split('\n'))
        {
            if (!line.StartsWith("0::", StringComparison.Ordinal)) continue;
            var dirs = new List<string>();
            var rel = line[3..].Trim().Trim('/');
            while (true)
            {
                dirs.Add(rel.Length == 0 ? root : root + "/" + rel);
                if (rel.Length == 0) break;
                var slash = rel.LastIndexOf('/');
                rel = slash < 0 ? "" : rel[..slash];
            }
            return dirs;
        }
        return Array.Empty<string>();
    }

    private static long? ReadCgroupRemainingBytes()
    {
        try
        {
            return Tightest(CgroupDirectories(File.ReadAllText("/proc/self/cgroup")).Select(dir =>
            {
                try
                {
                    return CgroupRemainingBytes(
                        File.ReadAllText(dir + "/memory.max"), File.ReadAllText(dir + "/memory.current"));
                }
                catch (IOException) { return (long?)null; }   // the root cgroup has no such files
            }));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
