// BackupReaderTool — the process boundary between the runner and `bcbak`, the reader that
// decodes a BC SQL Server `.bak` directly (no SQL Server, no restore, no container).
//
// TRANSPORT IS INTERIM: one process per command. The reader has a serve mode that opens the
// backup once and answers newline-delimited JSON on stdin — measured at 317 ms of startup
// plus 1.5-15 ms per warm small table, against ~0.7-2.3 s for every invocation here (all 456
// CRONUS tables: 1.76 s vs 2m13s). #2263 swaps it. This file is the whole surface that has to
// change: everything else goes through Run(...) and knows nothing about the transport.
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
    private const string ExecutableName = "bcbak";

    private static string? _resolved;
    private static string? _identity;

    /// <summary>Every location <see cref="Resolve"/> probes, in order. Public shape (a list,
    /// not a formatted string) so the failure message and the tests agree by construction
    /// rather than by two people spelling the same paths twice.</summary>
    internal static IReadOnlyList<string> CandidateExecutables(string? envValue, string? home)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var trimmed = envValue.Trim();
            candidates.Add(trimmed);
            candidates.Add(Path.Combine(trimmed, ExecutableName));
        }
        if (!string.IsNullOrEmpty(home))
            candidates.Add(Path.Combine(home, ".cache", "al-runner", ExecutableName, ExecutableName));
        return candidates;
    }

    /// <summary>The resolved reader executable. Throws (naming every probed location and the
    /// env var that overrides them) rather than returning null, so a caller cannot continue
    /// against a database it never populated.</summary>
    internal static string Resolve()
    {
        if (_resolved != null) return _resolved;

        var env = Environment.GetEnvironmentVariable(ExecutableEnvVar);
        var home = TryUserHome();
        foreach (var candidate in CandidateExecutables(env, home))
            if (File.Exists(candidate))
                return _resolved = Path.GetFullPath(candidate);

        var onPath = TryFindOnPath(ExecutableName);
        if (onPath != null) return _resolved = onPath;

        var probed = string.Join("\n    ", CandidateExecutables(env, home).Append($"<each PATH entry>/{ExecutableName}"));
        throw new BackupReaderException(
            $"--test-data needs the BC backup reader '{ExecutableName}', which was not found.\n"
            + $"  Probed:\n    {probed}\n"
            + $"  Set {ExecutableEnvVar} to the executable (or to the directory containing it).");
    }

    /// <summary>Reset the memoised resolution/identity. Test-only seam: the resolution reads
    /// process environment state that a test needs to vary.</summary>
    internal static void ResetForTests()
    {
        _resolved = null;
        _identity = null;
    }

    private static string? TryUserHome()
    {
        try { return AlRunnerPaths.UserHome; }
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
    /// the reader's own stderr text — never converted into an empty result.</summary>
    internal static string Run(IReadOnlyList<string> args, int timeoutMs = 600_000)
    {
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
                $"the backup reader failed (exit {proc.ExitCode}) for: {exe} {string.Join(' ', args)}\n"
                + $"  {errText.Trim()}");
        return outText;
    }
}
