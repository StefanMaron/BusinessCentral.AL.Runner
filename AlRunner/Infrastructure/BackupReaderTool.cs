// BackupReaderTool — the process boundary between the runner and `bcbak`, the reader that
// decodes a BC SQL Server `.bak` directly (no SQL Server, no restore, no container).
//
// TRANSPORT: `read`, `tables` and `companies` go over the reader's SERVE mode
// (BackupReaderServe.cs), one child for the run; anything else (`describe` has no call site)
// spawns one process per command. This file plus BackupReaderServe.cs are the whole transport
// surface: everything else goes through Run(...) and knows nothing about it.
// Measurements: docs/test-data-reader-transport.md.
//
// WHY A SUBPROCESS AND NOT A PACKAGE REFERENCE
//   The reader is a separate project that knows nothing about AL Runner, and it must stay
//   that way: it is a general-purpose BC backup reader, not a runner component. Coupling at
//   the process boundary keeps the dependency one-directional and swappable — replacing this
//   file with a package reference later changes nothing outside it.
//
// LOCATING THE BINARY
//   AL_RUNNER_BCBAK first (a file, or a directory containing `bcbak`), then a probed
//   per-user cache directory, then PATH. No path to any particular checkout is compiled in.
//   Auto-provision installs the pinned release into the cache slot (BackupReaderProvisioning,
//   #4925); the other two are the user's and never replaced.
//   Absence is a loud, actionable failure naming every location probed — never a silent
//   "no test data" run.
//
// EXTRACTOR IDENTITY
//   ExtractorIdentity() hashes the resolved executable AND its sibling managed assemblies.
//   That is deliberate: for a framework-dependent build the apphost (`bcbak`) is byte-
//   identical between builds and only the `.dll`s change, so hashing the exe alone would
//   let a reader fix that changes DECODED VALUES be masked by a cached install baseline
//   keyed on an unchanged identity. The identity is folded into the baseline cache key by
//   TestDataOptions.CacheIdentity(), so upgrading the reader invalidates the snapshot.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

/// <summary>Thrown when the backup reader cannot be located, or refuses a request. Never
/// swallowed into an empty-database run — see .claude/rules/loud-failures.md.</summary>
public sealed class BackupReaderException : Exception
{
    public BackupReaderException(string message) : base(message) { }
}

internal static class BackupReaderTool
{
    internal const string ExecutableEnvVar = "AL_RUNNER_BCBAK";
    private const string InstallDirName = "bcbak";
    // Windows' process launch appends `.exe` to an extensionless name, so a file named `bcbak`
    // there is found by File.Exists and then cannot be started.
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "bcbak.exe" : "bcbak";

    private static string? _resolved;
    private static string? _identity;

    /// <summary>Every location <see cref="Resolve"/> probes, in order. Public shape (a list,
    /// not a formatted string) so the failure message and the tests agree by construction
    /// rather than by two people spelling the same paths twice.</summary>
    internal static IReadOnlyList<string> CandidateExecutables(string? envValue, string? cacheRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var trimmed = envValue.Trim();
            candidates.Add(trimmed);
            candidates.Add(Path.Combine(trimmed, ExecutableName));
        }
        var managed = ManagedInstallPath(cacheRoot);
        if (managed != null)
            candidates.Add(managed);
        return candidates;
    }

    /// <summary>The per-user cache slot auto-provision installs the pinned reader into (#4925);
    /// null when there is no cache root.</summary>
    internal static string? ManagedInstallPath(string? cacheRoot)
        => string.IsNullOrEmpty(cacheRoot) ? null : Path.Combine(cacheRoot, InstallDirName, ExecutableName);

    /// <summary>The per-user cache root this process probes; null when it cannot be resolved.</summary>
    internal static string? DefaultCacheRoot() => TryDefaultCacheRoot();

    /// <summary>The resolved reader executable. Throws (naming every probed location and the
    /// env var that overrides them) rather than returning null, so a caller cannot continue
    /// against a database it never populated.</summary>
    internal static string Resolve()
    {
        if (_resolved != null) return _resolved;

        var env = Environment.GetEnvironmentVariable(ExecutableEnvVar);
        var cacheRoot = TryDefaultCacheRoot();
        foreach (var candidate in CandidateExecutables(env, cacheRoot))
            if (File.Exists(candidate))
                return _resolved = Path.GetFullPath(candidate);

        var onPath = FindOnPath();
        if (onPath != null) return _resolved = onPath;

        throw new BackupReaderException(NotFoundMessage(CandidateExecutables(env, cacheRoot)));
    }

    /// <summary>Everything actionable on the FIRST line (#2779): the bundle reporter keeps only
    /// line 1 of an EXEC-FAIL message.</summary>
    internal static string NotFoundMessage(IReadOnlyList<string> candidates)
    {
        var probed = string.Join(", ",
            candidates.Append($"<each PATH entry>/{ExecutableName}").Select(c => $"'{c}'"));
        return $"--test-data needs the BC backup reader '{ExecutableName}', which was not found — "
            + $"probed {probed}. Auto-provision installs the pinned release (off under --no-auto-provision), "
            + $"or run `al-runner provision --test-data`, or set {ExecutableEnvVar} to the executable "
            + "(or to the directory containing it).";
    }

    /// <summary>The reader on PATH, or null.</summary>
    internal static string? FindOnPath() => TryFindOnPath(ExecutableName);

    /// <summary>Reset the memoised resolution/identity. Test-only seam: the resolution reads
    /// process environment state that a test needs to vary.</summary>
    internal static void ResetForTests()
    {
        _resolved = null;
        _identity = null;
    }

    // The per-user cache root (CacheRoots.DefaultRoot, so AL_RUNNER_CACHE_ROOT moves this probe
    // too); null when it cannot be resolved, which drops only that one candidate.
    private static string? TryDefaultCacheRoot()
    {
        try { return CacheRoots.DefaultRoot; }
        catch { return null; }
    }

    private static string? TryFindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string full;
            try { full = Path.Combine(dir, name); }
            catch (ArgumentException) { continue; }
            if (File.Exists(full)) return Path.GetFullPath(full);
        }
        return null;
    }

    /// <summary>
    /// A stable identity for the exact reader build in use, folded into the install-baseline
    /// cache key. Hashes the executable plus every sibling managed assembly (name, length and
    /// contents, in ordinal name order) — see the file header for why the executable alone is
    /// not enough.
    /// </summary>
    internal static string ExtractorIdentity() => _identity ??= ComputeIdentity(Resolve());

    internal static string ComputeIdentity(string executablePath)
    {
        var files = new List<string> { executablePath };
        var dir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (dir != null && Directory.Exists(dir))
            files.AddRange(Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly));

        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var file in files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal))
        {
            byte[] content;
            try { content = File.ReadAllBytes(file); }
            catch (IOException ex)
            {
                throw new BackupReaderException(
                    $"cannot compute the backup reader's identity: '{file}' is unreadable ({ex.Message}). "
                    + "The identity keys the cached install baseline, so continuing would risk reusing a "
                    + "snapshot produced by a different reader build.");
            }
            sb.Append(Path.GetFileName(file)).Append(':').Append(content.Length).Append(':')
              .Append(Convert.ToHexString(sha.ComputeHash(content))).Append('\n');
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Run the reader and return stdout. A non-zero exit is an error, surfaced with
    /// the reader's own stderr text — never converted into an empty result.
    ///
    /// A command the serve transport models is answered over the shared serve process, as the
    /// text the CLI would have printed, so no caller can tell which transport answered.
    /// Everything else spawns a process here.</summary>
    internal static string Run(IReadOnlyList<string> args, int timeoutMs = 600_000)
    {
        if (BackupReaderServe.TryRun(args, out var served, timeoutMs)) return served;

        var exe = Resolve();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new BackupReaderException($"failed to start the backup reader '{exe}'");

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new BackupReaderException(
                $"the backup reader did not exit within {timeoutMs}ms for: {exe} {string.Join(' ', args)}");
        }
        proc.WaitForExit();

        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new BackupReaderException(
                $"the backup reader failed (exit {proc.ExitCode}): {Condense(errText)} "
                + $"— command: {exe} {string.Join(' ', args)}");
        return outText;
    }

    /// <summary>
    /// Collapse a reader diagnostic onto ONE line, because that is the only line that survives
    /// (#2779).
    ///
    /// The reader's stderr IS the diagnosis. It used to be appended as line 2 of the exception
    /// message, and every bundle-level reporter keeps only line 1 of an EXEC-FAIL message
    /// (ExecFailure.Describe, and ExecFailureTests pins that one-line contract deliberately —
    /// a multi-line suite-error line would break every consumer downstream). Measured on the
    /// ms-bucket workflow's first run (Actions run 33967273260): all that reached results.json
    /// was "the backup reader failed (exit 1) for: … BusinessCentral-W1.bak", while the reader
    /// had printed "block 116504 of MSDA region is neither mapped by the derived extent list
    /// nor padding filler — backup layout differs from the derived model, refusing to guess",
    /// which names the cause outright. Diagnosing it took a manual re-run against the same
    /// backup. See .claude/rules/loud-failures.md: a tool that fails with a reason must not be
    /// reduced to an exit code.
    ///
    /// Empty input is reported as such rather than producing a message with a hole in it —
    /// "the reader said nothing" is itself a fact worth reading, and distinguishes a crashed
    /// binary from a refusal.
    /// </summary>
    internal static string Condense(string? text, int maxLines = 5, int maxChars = 600)
    {
        var lines = (text ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return "(the reader printed nothing to stderr)";

        var kept = string.Join(" | ", lines.Take(maxLines));
        if (lines.Count > maxLines) kept += $" | (+{lines.Count - maxLines} more line(s))";
        if (kept.Length > maxChars) kept = kept[..maxChars] + "… (truncated)";
        return kept;
    }
}
